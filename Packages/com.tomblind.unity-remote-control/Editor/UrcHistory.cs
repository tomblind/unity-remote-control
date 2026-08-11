using System;
using System.Collections.Generic;
using UnityEditor;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// A rolling record of answered requests, for the editor window.
    ///
    /// Both prior tools had one and it earned its place for a reason that is easy to miss: when an
    /// agent drives your editor, things happen that you did not initiate and cannot see. "Why did my
    /// scene change?" and "is something actually connected?" are answerable at a glance here and
    /// almost nowhere else.
    ///
    /// Held in SessionState, like the job journal — it survives the domain reloads that punctuate
    /// normal work (otherwise the list would clear every few commands and be useless) and dies with
    /// the editor, which is the right lifetime for "what happened in this session".
    ///
    /// Main thread only: SessionState is a Unity API.
    /// </summary>
    internal static class UrcHistory
    {
        private const string Key = "urc.history";

        /// <summary>Enough to answer "what just happened" without turning the window into a log viewer.</summary>
        private const int Limit = 25;

        /// <summary>Snippets are unbounded; the window shows a recognisable prefix, not the source.</summary>
        private const int MaxDetailChars = 80;

        public sealed class Entry
        {
            public string JobId;
            public string Cmd;
            public string Status;
            public string Detail;
            public string FinishedAtUtc;
            public int DurationMs;
            public int Generation;
            public int ClientPid;

            public Json ToJson() =>
                Json.Object()
                    .Set("jobId", JobId)
                    .Set("cmd", Cmd)
                    .Set("status", Status)
                    .SetIf("detail", Detail)
                    .SetIf("finishedAt", FinishedAtUtc)
                    .Set("durationMs", DurationMs)
                    .Set("generation", Generation)
                    .Set("clientPid", ClientPid);

            public static Entry FromJson(Json json) => new Entry
            {
                JobId = json["jobId"].AsString(""),
                Cmd = json["cmd"].AsString(""),
                Status = json["status"].AsString(""),
                Detail = json["detail"].AsString(),
                FinishedAtUtc = json["finishedAt"].AsString(),
                DurationMs = json["durationMs"].AsInt(),
                Generation = json["generation"].AsInt(),
                ClientPid = json["clientPid"].AsInt(),
            };
        }

        public static void Record(UrcJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.Id)) return;

            var entry = new Entry
            {
                JobId = job.Id,
                Cmd = job.Cmd,
                Status = job.Status ?? UrcProtocol.Status.Interrupted,
                Detail = Shorten(job.Detail),
                FinishedAtUtc = job.FinishedAtUtc,
                DurationMs = Duration(job),
                Generation = job.FinishedGeneration != 0 ? job.FinishedGeneration : job.StartedGeneration,
                ClientPid = job.ClientPid,
            };

            var entries = Recent();

            // A re-attach can complete the same job twice; keep one row per job.
            entries.RemoveAll(e => e.JobId == entry.JobId);
            entries.Add(entry);

            while (entries.Count > Limit) entries.RemoveAt(0);

            var array = Json.Array();
            foreach (var item in entries) array.Add(item.ToJson());
            SessionState.SetString(Key, array.ToString());
        }

        /// <summary>Oldest first, so the window can render newest-first by walking backwards.</summary>
        public static List<Entry> Recent()
        {
            var entries = new List<Entry>();

            var raw = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(raw)) return entries;
            if (!Json.TryParse(raw, out var json) || json.ValueKind != Json.Kind.Array) return entries;

            foreach (var item in json.Items) entries.Add(Entry.FromJson(item));
            return entries;
        }

        public static void Clear() => SessionState.EraseString(Key);

        private static int Duration(UrcJob job)
        {
            if (!DateTime.TryParse(job.StartedAtUtc, out var started)) return 0;
            if (!DateTime.TryParse(job.FinishedAtUtc, out var finished)) return 0;

            var ms = (finished - started).TotalMilliseconds;
            return ms <= 0 || ms > int.MaxValue ? 0 : (int)ms;
        }

        /// <summary>Collapses a snippet to one recognisable line — newlines would break the row layout.</summary>
        private static string Shorten(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return null;

            var flat = detail.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");

            return flat.Length <= MaxDetailChars ? flat : flat.Substring(0, MaxDetailChars) + "…";
        }
    }
}
