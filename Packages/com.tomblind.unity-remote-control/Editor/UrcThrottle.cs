using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// Suspends Unity's editor throttling while a command is running.
    ///
    /// Without this an unfocused editor idles between ticks and a job that should take 80ms takes
    /// ~1.6s (measured). Suspending the InteractionMode preference is the only mechanism that works;
    /// external window-message pokes were verified ineffective by the prior project.
    ///
    /// THE COORDINATION STATE IS A FILE, NOT EditorPrefs. `InteractionMode` is one machine-global
    /// value shared by every Unity editor this user runs, so the bookkeeping — who currently holds a
    /// bracket, and what the user's real value was — must be visible to ALL of them. EditorPrefs
    /// cannot do that: Unity caches them per process and never invalidates, so one editor's write is
    /// invisible to another that has already read the key.
    ///
    /// That was verified directly, with two live editors: A wrote a value and read it back; B still
    /// read the old one. Under the previous EditorPrefs design an editor could therefore stash a
    /// stale backup and restore the wrong value on release, or miss another editor's claim entirely
    /// and restore while it was still needed — which is the exact latch this bookkeeping exists to
    /// prevent.
    ///
    /// A file has no such cache: every read hits the filesystem. Reads and writes happen only at
    /// bracket transitions, never on a timer.
    ///
    /// All members are MAIN THREAD ONLY — EditorPrefs is a Unity API.
    /// </summary>
    internal static class UrcThrottle
    {
        /// <summary>The editor-global Interaction Mode preference. 1 = "No Throttling".</summary>
        private const string InteractionModePrefKey = "InteractionMode";
        private const int NoThrottling = 1;

        private static int _localHolds;
        private static bool _warnedApply;

        private static string StatePath => Path.Combine(UrcPaths.Root, "throttle.json");

        /// <summary>
        /// Engages the bracket for one command. Refcounted, so overlapping work in this process
        /// engages once and releases once.
        /// </summary>
        public static void Engage()
        {
            if (Interlocked.Increment(ref _localHolds) > 1) return;

            var self = UrcProcess.Id;

            Mutate(state =>
            {
                // Only the FIRST holder on the machine records the user's real value. Now that
                // "first" is decided by genuinely shared state, exactly one editor's reading is
                // used — the one that engaged before anyone had set the pref to 1.
                if (!state.HasBackup)
                {
                    state.HasBackup = true;
                    state.Backup = EditorPrefs.GetInt(InteractionModePrefKey, 0);
                }

                if (!state.Owners.Contains(self)) state.Owners.Add(self);
            });

            EditorPrefs.SetInt(InteractionModePrefKey, NoThrottling);
            Apply();
        }

        public static void Release()
        {
            if (Interlocked.Decrement(ref _localHolds) > 0) return;

            // Clamp: a stray release must not drive this negative and silently disable the bracket
            // for the rest of the session.
            if (Volatile.Read(ref _localHolds) < 0) Interlocked.Exchange(ref _localHolds, 0);

            ReleaseShared();
        }

        private static void ReleaseShared()
        {
            var restoreTo = 0;
            var restore = false;

            Mutate(state =>
            {
                state.Owners.Remove(UrcProcess.Id);

                // Whoever empties the list restores. Dead pids were already pruned on read, so a
                // crashed editor cannot leave the machine unthrottled forever.
                if (state.Owners.Count > 0 || !state.HasBackup) return;

                restore = true;
                restoreTo = state.Backup;
                state.HasBackup = false;
            });

            if (!restore) return;

            EditorPrefs.SetInt(InteractionModePrefKey, restoreTo);
            Apply();
        }

        /// <summary>
        /// Reconciles after a domain load, on the first update tick.
        ///
        /// Throttle writes during [InitializeOnLoad] HALF-APPLY — the pref store updates but the live
        /// session keeps the old value (observed on 6000.3). The deferred tick is guaranteed to
        /// arrive, because a pending restore implies no-throttle is natively active.
        ///
        /// This also covers a bracket that died with the previous domain: the pid in the shared file
        /// is this same process, so the fresh domain — holding nothing — drops it and restores if it
        /// was the last.
        /// </summary>
        public static void ScheduleSync()
        {
            EditorApplication.update -= SyncOnce;
            EditorApplication.update += SyncOnce;
        }

        private static void SyncOnce()
        {
            EditorApplication.update -= SyncOnce;

            if (Volatile.Read(ref _localHolds) == 0) ReleaseShared();

            // Self-heal: re-apply whatever the pref currently is, in case this editor picked up
            // another editor's transient no-throttle value during one of its own reloads.
            Apply();
        }

        // ---- shared state -------------------------------------------------------------------

        private sealed class State
        {
            public readonly List<int> Owners = new List<int>();
            public bool HasBackup;
            public int Backup;
        }

        /// <summary>How long to keep retrying for the lock before giving up on this transition.</summary>
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Read-modify-write under an EXCLUSIVE lock, so two editors engaging at the same instant
        /// cannot interleave and lose one another's claim.
        ///
        /// FileShare.None for the whole operation, not just the write: the danger is a read that
        /// goes stale before its own write lands, so the lock has to span both. Contention is brief
        /// and rare — brackets are seconds long and editors are started by hand — so a short retry
        /// is enough, and failing to acquire is not fatal: the worst case is a bracket that misses a
        /// transition, which the next one repairs.
        /// </summary>
        private static void Mutate(Action<State> change)
        {
            var deadline = DateTime.UtcNow + LockTimeout;

            try { Directory.CreateDirectory(UrcPaths.Root); }
            catch (Exception e) { Debug.LogWarning($"[urc] throttle state unavailable: {e.Message}"); return; }

            while (true)
            {
                try
                {
                    using var stream = new FileStream(StatePath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);

                    var state = Read(stream);
                    change(state);
                    Write(stream, state);
                    return;
                }
                catch (IOException)
                {
                    // Held by another editor. Retry until the deadline.
                    if (DateTime.UtcNow >= deadline)
                    {
                        Debug.LogWarning(
                            "[urc] could not lock the shared throttle state; another editor is holding it. " +
                            "Editor responsiveness may be affected until the next command.");
                        return;
                    }
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException e)
                {
                    Debug.LogWarning($"[urc] throttle state not writable: {e.Message}");
                    return;
                }
            }
        }

        private static State Read(FileStream stream)
        {
            var state = new State();

            try
            {
                stream.Position = 0;
                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read == 0) return state;
                if (!Json.TryParse(Encoding.UTF8.GetString(bytes, 0, read), out var json)) return state;

                foreach (var owner in json["owners"].Items)
                {
                    var pid = owner.AsInt();
                    // Pid reuse is guarded by requiring the process to actually be a Unity editor;
                    // otherwise a recycled pid belonging to something else would hold the bracket open.
                    if (pid > 0 && !state.Owners.Contains(pid) && IsLiveUnityProcess(pid)) state.Owners.Add(pid);
                }

                if (json.Has("backup"))
                {
                    state.HasBackup = true;
                    state.Backup = json["backup"].AsInt();
                }
            }
            catch (Exception)
            {
                // A torn or corrupt file is treated as empty rather than fatal — the next transition
                // rewrites it cleanly.
            }

            return state;
        }

        private static void Write(FileStream stream, State state)
        {
            var json = Json.Object();

            var owners = Json.Array();
            foreach (var pid in state.Owners) owners.Add(pid);
            json.Set("owners", owners);

            if (state.HasBackup) json.Set("backup", state.Backup);

            var bytes = Encoding.UTF8.GetBytes(json.ToString());

            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.SetLength(bytes.Length);   // truncate: a shorter document must not leave a tail
            stream.Flush();
        }

        private static bool IsLiveUnityProcess(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.ProcessName.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;   // exited or inaccessible
            }
        }

        /// <summary>
        /// Pushes the preference into the live editor loop. The applier is internal, so this is a
        /// reflection shim; if a Unity version removes it the pref still takes effect on the next
        /// domain reload (each new domain reads it at init), so reload survival keeps working and
        /// only the immediate in-session application is lost. Warn once so the degradation is visible.
        /// </summary>
        private static void Apply()
        {
            try
            {
                var apply = typeof(EditorApplication).GetMethod(
                    "UpdateInteractionModeSettings", BindingFlags.NonPublic | BindingFlags.Static);
                if (apply != null) { apply.Invoke(null, null); return; }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[urc] Applying InteractionMode failed: {e.Message}");
                return;
            }

            if (_warnedApply) return;
            _warnedApply = true;
            Debug.LogWarning(
                "[urc] Could not resolve EditorApplication.UpdateInteractionModeSettings — InteractionMode " +
                "changes will only take effect after the next domain reload. Unfocused work will be slow. " +
                $"This usually means a Unity version change; revisit {nameof(UrcThrottle)}.{nameof(Apply)}.");
        }
    }
}
