using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Sunblink.Urc.Editor
{
    /// <summary>
    /// Pumps work from the connection thread onto the editor main thread, and keeps an unfocused
    /// editor ticking while that work is in flight.
    ///
    /// The unfocused case is not an edge case — it is the normal state for agent-driven work, and it
    /// is where the naive implementation silently hangs. Two internal editor APIs handle it, both
    /// resolved by reflection and both optional:
    ///
    /// - <c>SignalTick</c> wakes the native editor loop from any thread, so EditorApplication.update
    ///   keeps firing while a request waits even after an idle unfocused editor has gone quiet.
    /// - <c>UpdateSceneIfNeeded</c> runs the queued player-loop update directly. An inactive editor
    ///   DROPS QueuePlayerLoopUpdate requests, which freezes Time.time and stalls any time-based wait
    ///   inside a snippet.
    ///
    /// If a future Unity removes either, execution still works — but only while the editor has focus,
    /// so the degradation is reported loudly rather than presenting as a mysterious hang.
    /// </summary>
    internal static class UrcMainThread
    {
        private static readonly ConcurrentQueue<Func<Task>> Queue = new ConcurrentQueue<Func<Task>>();
        private static readonly Action SignalTick = BindEditorApplication("SignalTick");
        private static readonly Action UpdateSceneIfNeeded = BindEditorApplication("UpdateSceneIfNeeded");
        private static int _keepAlive;

        /// <summary>False when the unfocused-execution APIs could not be resolved.</summary>
        public static bool CanRunUnfocused => SignalTick != null && UpdateSceneIfNeeded != null;

        public static void EnablePump()
        {
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;

            if (!CanRunUnfocused)
            {
                var missing = SignalTick == null
                    ? (UpdateSceneIfNeeded == null ? "SignalTick and UpdateSceneIfNeeded" : "SignalTick")
                    : "UpdateSceneIfNeeded";
                Debug.LogWarning(
                    $"[urc] Unfocused execution unavailable: could not resolve internal EditorApplication " +
                    $"API(s) {missing}. Commands will only complete while the editor window has focus. " +
                    $"This usually means a Unity version change broke the reflection lookup — see " +
                    $"{nameof(UrcMainThread)}.");
            }
        }

        public static void DisablePump() => EditorApplication.update -= Pump;

        public static void Enqueue(Func<Task> job) => Queue.Enqueue(job);

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and waits for its value.
        ///
        /// The connection thread uses this whenever it needs something only Unity can answer —
        /// reading the SessionState journal, building a compile report — instead of reaching for
        /// those APIs directly, which would break the guarantee that it keeps answering while the
        /// editor is busy.
        /// </summary>
        public static T Request<T>(Func<T> work, Func<bool> keepWaiting, T fallback)
        {
            var result = fallback;
            var done = new ManualResetEventSlim(false);

            Enqueue(() =>
            {
                try { result = work(); }
                catch (Exception e) { Debug.LogException(e); }
                finally { done.Set(); }
                return Task.CompletedTask;
            });

            while (keepWaiting())
            {
                if (done.Wait(15)) return result;
                WakeEditor();
            }

            return fallback;
        }

        /// <summary>
        /// Wakes the native editor loop from any thread. Called by the connection thread while it
        /// waits, so an idle unfocused editor keeps servicing the queue.
        /// </summary>
        public static void WakeEditor() => SignalTick?.Invoke();

        /// <summary>Hold while a job needs the editor to keep ticking; always pair in a finally.</summary>
        public static void RequestKeepAlive() => Interlocked.Increment(ref _keepAlive);

        public static void ReleaseKeepAlive() => Interlocked.Decrement(ref _keepAlive);

        private static void Pump()
        {
            // Stamp first: a long job on this tick must not make the editor look stalled to a client.
            UrcEditorState.Stamp();

            while (Queue.TryDequeue(out var job))
            {
                try
                {
                    // Started here, on the main thread, so async continuations post back to it via
                    // Unity's SynchronizationContext. The job reports its own completion.
                    _ = job();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            if (Volatile.Read(ref _keepAlive) > 0)
            {
                EditorApplication.QueuePlayerLoopUpdate();

                // An inactive editor drops the queued update, so run it directly to keep Time.time
                // advancing without focus.
                if (UpdateSceneIfNeeded != null && !InternalEditorUtility.isApplicationActive)
                    UpdateSceneIfNeeded();
            }
        }

        private static Action BindEditorApplication(string methodName)
        {
            try
            {
                var method = typeof(EditorApplication).GetMethod(
                    methodName, BindingFlags.Static | BindingFlags.NonPublic);
                return method == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), method);
            }
            catch (Exception e)
            {
                // Signature drift on a Unity upgrade (an added overload → AmbiguousMatchException, a
                // changed signature → ArgumentException) must NOT throw out of this static field
                // initializer: that would fault the type and kill dispatch entirely. Degrade to null.
                Debug.LogWarning($"[urc] Failed to bind EditorApplication.{methodName}: {e.Message}");
                return null;
            }
        }
    }
}
