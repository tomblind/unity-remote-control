using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Urc.Editor
{
    /// <summary>
    /// Suspends Unity's editor throttling while a job is in flight.
    ///
    /// Without this an unfocused editor idles between ticks, and a job that should take 80ms takes
    /// ~1.6s (measured). External window-message pokes were verified ineffective by the prior
    /// project; suspending the InteractionMode preference is the only mechanism that works.
    ///
    /// THE STATE HERE IS DELIBERATELY MACHINE-GLOBAL, NOT PER-PROJECT. `InteractionMode` is a single
    /// preference shared by every Unity editor this user runs, while the *applied* throttle state is
    /// per-process. If each project kept its own backup, two editors with overlapping brackets would
    /// stash each other's transient no-throttle value and permanently latch the machine unthrottled —
    /// a bug that outlives both editors. So: the first engager on the machine stashes the user's real
    /// value, every engager records its pid, and whoever empties the owner list restores.
    ///
    /// Stored in EditorPrefs rather than SessionState so a crashed editor still restores later; dead
    /// pids are pruned from the owner list on every touch, so a crash cannot wedge the machine.
    ///
    /// All members are MAIN THREAD ONLY — EditorPrefs is a Unity API.
    /// </summary>
    internal static class UrcThrottle
    {
        private const string BackupKey = "Urc.Shared.InteractionModeBackup";
        private const string OwnersKey = "Urc.Shared.ThrottleOwners";

        /// <summary>The editor-global Interaction Mode preference. 1 = "No Throttling".</summary>
        private const string InteractionModePrefKey = "InteractionMode";
        private const int NoThrottling = 1;

        private static int _localHolds;
        private static bool _warnedApply;

        /// <summary>
        /// Engages the bracket for one job. Refcounted, so overlapping work in this process engages
        /// once and releases once.
        /// </summary>
        public static void Engage()
        {
            if (Interlocked.Increment(ref _localHolds) > 1) return;

            // The HasKey guard does double duty: it stops a re-engage after a domain reload from
            // clobbering the backup with our own no-throttle value, and stops a second editor's
            // overlapping bracket from stashing the first one's.
            if (!EditorPrefs.HasKey(BackupKey))
                EditorPrefs.SetInt(BackupKey, EditorPrefs.GetInt(InteractionModePrefKey, 0));

            var owners = ReadLiveOwners();
            var self = UrcProcess.Id;
            if (!owners.Contains(self)) owners.Add(self);
            WriteOwners(owners);

            EditorPrefs.SetInt(InteractionModePrefKey, NoThrottling);
            Apply();
        }

        public static void Release()
        {
            if (Interlocked.Decrement(ref _localHolds) > 0) return;
            RestoreShared();
        }

        private static void RestoreShared()
        {
            var owners = ReadLiveOwners();
            owners.Remove(UrcProcess.Id);
            WriteOwners(owners);

            // Another editor still holds a bracket; it (or the dead-pid pruning) restores last.
            if (owners.Count > 0) return;
            if (!EditorPrefs.HasKey(BackupKey)) return;

            EditorPrefs.SetInt(InteractionModePrefKey, EditorPrefs.GetInt(BackupKey, 0));
            EditorPrefs.DeleteKey(BackupKey);
            Apply();
        }

        /// <summary>
        /// Defers the post-reload reconciliation to the first update tick.
        ///
        /// Throttle writes during [InitializeOnLoad] HALF-APPLY — the EditorPrefs store updates but
        /// the live session keeps the old value (observed on 6000.3). The deferred tick is guaranteed
        /// to arrive, because a pending restore implies no-throttle is natively active.
        /// </summary>
        public static void ScheduleSync()
        {
            EditorApplication.update -= SyncOnce;
            EditorApplication.update += SyncOnce;
        }

        private static void SyncOnce()
        {
            EditorApplication.update -= SyncOnce;

            // No-op when this process holds no bracket and none is recoverable.
            if (_localHolds == 0) RestoreShared();

            // Self-heal: re-apply whatever the pref currently is, in case this editor picked up
            // another editor's transient no-throttle value during one of its own reloads.
            Apply();
        }

        private static List<int> ReadLiveOwners()
        {
            var owners = ParseOwners(EditorPrefs.GetString(OwnersKey, ""));
            owners.RemoveAll(pid => !IsLiveUnityProcess(pid));
            return owners;
        }

        private static void WriteOwners(List<int> owners)
        {
            if (owners.Count == 0) EditorPrefs.DeleteKey(OwnersKey);
            else EditorPrefs.SetString(OwnersKey, string.Join(";", owners.ConvertAll(p => p.ToString()).ToArray()));
        }

        internal static List<int> ParseOwners(string raw)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var part in raw.Split(';'))
                if (int.TryParse(part.Trim(), out var pid) && pid > 0 && !result.Contains(pid))
                    result.Add(pid);

            return result;
        }

        /// <summary>
        /// Pid reuse is guarded by requiring the process to actually be a Unity editor — otherwise a
        /// recycled pid belonging to some unrelated program would keep the machine unthrottled.
        /// </summary>
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
        /// only the immediate in-session application is lost. Warn once so that degradation is visible.
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
