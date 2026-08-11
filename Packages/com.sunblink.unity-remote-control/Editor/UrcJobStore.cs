using System;
using System.Collections.Generic;
using UnityEditor;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc.Editor
{
    /// <summary>
    /// Durable job state, held in Unity's <see cref="SessionState"/>.
    ///
    /// SessionState is native-side key/value storage that survives a domain reload but dies with the
    /// editor process — and that lifetime is exactly right. The only window in which "is my job still
    /// alive?" is a meaningful question IS the lifetime of the process: if the editor is gone, the
    /// job is gone, and the client learns that from pid liveness instead.
    ///
    /// This is why there are no journal files. Nothing to write atomically, nothing to garbage
    /// collect, nothing to go stale, and no torn reads to tolerate.
    ///
    /// Writes happen at state transitions only — never on a timer.
    /// </summary>
    internal static class UrcJobStore
    {
        private const string JobKeyPrefix = "urc.job.";
        private const string PendingKey = "urc.pendingJob";
        private const string RecentKey = "urc.recentJobs";

        /// <summary>
        /// How many finished jobs stay readable. `resume` almost always wants the current or last
        /// job; the rest is a small courtesy for a client that reconnects late.
        /// </summary>
        private const int RecentLimit = 8;

        /// <summary>
        /// SessionState is in-process memory, not a database. A bounded projection of a return value
        /// is small, but a snippet returning a huge string must not be allowed to park megabytes here
        /// for the rest of the session.
        /// </summary>
        private const int MaxSerializedJobChars = 64 * 1024;

        private static readonly object Gate = new object();

        public static string PendingJobId
        {
            get => Nullify(SessionState.GetString(PendingKey, ""));
            private set
            {
                SessionState.SetString(PendingKey, value ?? "");
                UrcEditorState.SetPendingJob(value);
            }
        }

        public static void Save(UrcJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.Id)) return;

            lock (Gate)
            {
                var payload = job.ToJson().ToString();
                if (payload.Length > MaxSerializedJobChars)
                {
                    // Drop the value rather than the job: knowing a job succeeded, with a pointer to
                    // where the output went, beats losing the record entirely.
                    job.Value = Json.String(
                        $"<{payload.Length} chars — too large for the session journal; see the log>");
                    payload = job.ToJson().ToString();
                }

                SessionState.SetString(JobKeyPrefix + job.Id, payload);
                Remember(job.Id);

                PendingJobId = job.IsTerminal ? null : job.Id;
            }
        }

        public static UrcJob Load(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return null;

            var payload = SessionState.GetString(JobKeyPrefix + jobId, "");
            if (string.IsNullOrEmpty(payload)) return null;

            return Json.TryParse(payload, out var json) ? UrcJob.FromJson(json) : null;
        }

        /// <summary>The job `urc resume` means when given no id: the pending one, else the most recent.</summary>
        public static UrcJob LoadCurrentOrLast()
        {
            var pending = PendingJobId;
            if (!string.IsNullOrEmpty(pending))
            {
                var job = Load(pending);
                if (job != null) return job;
            }

            var recent = RecentIds();
            return recent.Count > 0 ? Load(recent[recent.Count - 1]) : null;
        }

        /// <summary>
        /// Called once per domain load. A job left running by the previous domain had its
        /// continuation destroyed with that domain, so it can never complete — mark it interrupted
        /// now rather than leaving a client waiting on a result that will never come.
        ///
        /// A job that finished BEFORE the reload is already terminal and is left alone: its result is
        /// exactly what the reconnecting client came back for. This is what makes the pre-reload
        /// delivery flush an optimization rather than a correctness requirement.
        /// </summary>
        public static void ReconcileAfterReload()
        {
            lock (Gate)
            {
                var pending = PendingJobId;
                if (string.IsNullOrEmpty(pending)) return;

                var job = Load(pending);
                if (job == null) { PendingJobId = null; return; }

                if (job.IsTerminal) { PendingJobId = null; return; }

                if (job.StartedGeneration < UrcEditorState.Generation)
                {
                    job.Complete(UrcProtocol.Status.Interrupted,
                        "the domain reloaded while this job was running, so it was destroyed with it.",
                        null);
                    SessionState.SetString(JobKeyPrefix + job.Id, job.ToJson().ToString());
                    PendingJobId = null;
                }
            }
        }

        private static List<string> RecentIds()
        {
            var raw = SessionState.GetString(RecentKey, "");
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var id in raw.Split(','))
                if (!string.IsNullOrEmpty(id)) list.Add(id);
            return list;
        }

        private static void Remember(string jobId)
        {
            var recent = RecentIds();
            recent.Remove(jobId);
            recent.Add(jobId);

            while (recent.Count > RecentLimit)
            {
                SessionState.EraseString(JobKeyPrefix + recent[0]);
                recent.RemoveAt(0);
            }

            SessionState.SetString(RecentKey, string.Join(",", recent.ToArray()));
        }

        private static string Nullify(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
