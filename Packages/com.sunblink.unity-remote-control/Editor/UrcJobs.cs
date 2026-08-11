using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Sunblink.Urc.Editor
{
    /// <summary>
    /// In-memory view of jobs, so the connection thread never touches Unity.
    ///
    /// This type exists for one reason. <see cref="UrcJobStore"/> is backed by SessionState, which is
    /// a Unity API and therefore main-thread only. But the connection thread has to wait for a job
    /// and then report it — and if that wait poked SessionState, the whole design would collapse:
    /// touching Unity from the accept thread is exactly what stops working while the editor is
    /// compiling or importing, which is when answering matters most.
    ///
    /// So the split is absolute: the main thread owns the journal, this registry mirrors what the
    /// connection thread needs, and a job the registry has never seen is fetched by ASKING the main
    /// thread rather than reading around it.
    /// </summary>
    internal static class UrcJobs
    {
        internal sealed class Handle
        {
            public readonly UrcJob Job;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);

            public Handle(UrcJob job) { Job = job; }
        }

        private static readonly ConcurrentDictionary<string, Handle> Live =
            new ConcurrentDictionary<string, Handle>();

        /// <summary>Registers a job before it is enqueued. Safe from the connection thread — no Unity here.</summary>
        public static Handle Register(UrcJob job)
        {
            var handle = new Handle(job);
            Live[job.Id] = handle;
            return handle;
        }

        /// <summary>Called by the worker after the journal write, never before it.</summary>
        public static void MarkComplete(string jobId)
        {
            if (Live.TryGetValue(jobId, out var handle)) handle.Completed.Set();
        }

        public static bool TryGet(string jobId, out Handle handle) => Live.TryGetValue(jobId, out handle);

        /// <summary>
        /// Waits for a job to finish, waking the editor while it does.
        ///
        /// The wake is what keeps an UNFOCUSED editor progressing — the normal state for agent-driven
        /// work, and where a naive wait simply hangs until someone clicks on Unity. Returns false if
        /// the caller's abort condition fired first.
        /// </summary>
        public static bool Await(Handle handle, Func<bool> keepWaiting)
        {
            while (keepWaiting())
            {
                if (handle.Completed.Wait(15)) return true;
                UrcMainThread.WakeEditor();
            }
            return false;
        }

        /// <summary>
        /// Reads a job the registry has never seen — the post-reload `attach` path, where the journal
        /// is in SessionState and this thread may not read it. Hands the lookup to the main thread
        /// and waits for the answer.
        /// </summary>
        public static UrcJob LoadViaMainThread(string jobId, Func<bool> keepWaiting)
        {
            UrcJob result = null;
            var done = new ManualResetEventSlim(false);

            UrcMainThread.Enqueue(() =>
            {
                try { result = string.IsNullOrEmpty(jobId) ? UrcJobStore.LoadCurrentOrLast() : UrcJobStore.Load(jobId); }
                finally { done.Set(); }
                return System.Threading.Tasks.Task.CompletedTask;
            });

            while (keepWaiting())
            {
                if (done.Wait(15)) return result;
                UrcMainThread.WakeEditor();
            }

            return null;
        }

        /// <summary>Drops finished handles so a long session does not accumulate them.</summary>
        public static void Forget(string jobId)
        {
            if (Live.TryRemove(jobId, out var handle)) handle.Completed.Dispose();
        }
    }
}
