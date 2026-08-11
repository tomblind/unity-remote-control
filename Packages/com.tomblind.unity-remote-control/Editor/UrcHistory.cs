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

        /// <summary>Collapsed rows show a recognisable prefix; the full text lives in the entry.</summary>
        private const int SummaryChars = 80;

        /// <summary>
        /// Per-field cap on the retained text. This is held in SessionState — editor memory, not a
        /// database — so a snippet that returns a megabyte must not park it there for the session.
        /// Generous enough that a realistic request and result survive intact.
        /// </summary>
        private const int MaxRetainedChars = 8 * 1024;

        public sealed class Entry
        {
            public string JobId;
            public string Cmd;
            public string Status;
            public string FinishedAtUtc;
            public int DurationMs;
            public int Generation;
            public int ClientPid;

            /// <summary>What was asked for, in full — the snippet source for an exec.</summary>
            public string Request;

            /// <summary>What came back, in full — the value, or the failure and its summary.</summary>
            public string Response;

            /// <summary>One-line prefix of the request, for the collapsed row.</summary>
            public string Summary => Flatten(Request, SummaryChars);

            public Json ToJson() =>
                Json.Object()
                    .Set("jobId", JobId)
                    .Set("cmd", Cmd)
                    .Set("status", Status)
                    .SetIf("finishedAt", FinishedAtUtc)
                    .Set("durationMs", DurationMs)
                    .Set("generation", Generation)
                    .Set("clientPid", ClientPid)
                    .SetIf("request", Request)
                    .SetIf("response", Response);

            public static Entry FromJson(Json json) => new Entry
            {
                JobId = json["jobId"].AsString(""),
                Cmd = json["cmd"].AsString(""),
                Status = json["status"].AsString(""),
                FinishedAtUtc = json["finishedAt"].AsString(),
                DurationMs = json["durationMs"].AsInt(),
                Generation = json["generation"].AsInt(),
                ClientPid = json["clientPid"].AsInt(),
                Request = json["request"].AsString(),
                Response = json["response"].AsString(),
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
                FinishedAtUtc = job.FinishedAtUtc,
                DurationMs = Duration(job),
                Generation = job.FinishedGeneration != 0 ? job.FinishedGeneration : job.StartedGeneration,
                ClientPid = job.ClientPid,
                Request = Cap(job.Detail),
                Response = Cap(Describe(job)),
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

        /// <summary>Renders what came back, in the shape a person wants to read or copy.</summary>
        private static string Describe(UrcJob job)
        {
            var parts = new List<string>();

            if (job.Value != null && !job.Value.IsNull)
            {
                parts.Add(job.Value.ValueKind == Json.Kind.String
                    ? job.Value.AsString()
                    : job.Value.ToString());
            }

            if (!string.IsNullOrEmpty(job.Summary)) parts.Add(job.Summary);
            if (!string.IsNullOrEmpty(job.ValueArtifact)) parts.Add("full value: " + job.ValueArtifact);

            return parts.Count == 0 ? "(no value)" : string.Join("\n", parts.ToArray());
        }

        private static string Cap(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return text.Length <= MaxRetainedChars
                ? text
                : text.Substring(0, MaxRetainedChars) + $"\n… <{text.Length} chars, truncated>";
        }

        /// <summary>Collapses text to one recognisable line — newlines would break the row layout.</summary>
        private static string Flatten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var flat = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");

            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }
    }
}
