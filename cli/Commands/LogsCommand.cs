using System;
using System.Collections.Generic;
using System.IO;
using Sunblink.Urc.Discovery;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc
{
    /// <summary>
    /// Reads the editor's captured console straight off disk.
    ///
    /// Deliberately does NOT talk to the editor. That is what makes it the last-resort diagnostic:
    /// it works while the editor is wedged behind a modal dialog, mid-reload, or dead — which is
    /// exactly when you want to know what it was doing. Asking the editor would fail in all three.
    /// </summary>
    internal static class LogsCommand
    {
        public static int Run(Args args)
        {
            var project = ProjectResolver.Resolve(args.Get("project"), out _);
            if (project == null)
            {
                Program.Error("could not determine which Unity project to use.\n" +
                              "  Pass --project <path>, set $" + ProjectResolver.EnvVar +
                              ", or run from inside a Unity project.");
                return ExitCode.Usage;
            }

            var since = args.Get("since");

            // The session is normally learned from discovery, but a dead editor answers nothing —
            // so fall back to the newest session file on disk, which is the crash case.
            var sessionId = SessionFromCursor(since) ?? SessionFromDiscovery(project);

            var file = ResolveFile(project, sessionId, out var note);
            if (file == null)
            {
                Program.Error($"no captured log for {ProjectResolver.DisplayName(project)}.\n" +
                              "  The editor writes one as soon as the package loads; has it run yet?");
                return ExitCode.Failed;
            }

            if (note != null) Console.Error.WriteLine("note: " + note);

            var lines = Read(file, args, since, out var incomplete);

            if (incomplete)
            {
                // A warned slice must never be read as "no errors" — rotation may have discarded the
                // very entries being looked for.
                Console.Error.WriteLine(
                    "note: rotation discarded entries near this cursor; this slice may be incomplete.");
            }

            if (args.Json)
            {
                var array = Json.Array();
                foreach (var line in lines) array.Add(line);
                Console.WriteLine(Json.Object().Set("file", file).Set("lines", array).ToString());
                return ExitCode.Ok;
            }

            foreach (var line in lines) Console.WriteLine(Format(line));
            if (lines.Count == 0) Console.Error.WriteLine("(nothing matched)");
            return ExitCode.Ok;
        }

        private static List<Json> Read(string file, Args args, string since, out bool incomplete)
        {
            incomplete = false;

            var level = args.Get("level");
            if (args.Has("errors")) level = "error";

            var tail = int.TryParse(args.Get("tail"), out var t) && t > 0 ? t : 0;
            // --since reads the full slice by default; an explicit --tail still wins.
            if (tail == 0 && string.IsNullOrEmpty(since)) tail = 50;

            ParseCursor(since, out _, out var sinceGen, out var sinceSeq);

            var matched = new List<Json>();

            foreach (var raw in ReadAllLines(file))
            {
                if (!Json.TryParse(raw, out var line)) continue;

                var isBoundary = line.Has("event");

                if (sinceSeq > 0)
                {
                    var gen = line["gen"].AsInt();
                    var seq = line["seq"].AsInt();

                    // A watermark across generations, not a per-domain filter: everything after the
                    // cursor counts, including output from a later code era.
                    if (gen < sinceGen || (gen == sinceGen && seq <= sinceSeq)) continue;

                    // Pre-cursor-era lines carry no gen and cannot be placed; dropping them is the
                    // documented behaviour.
                    if (gen == 0) continue;
                }

                // Boundaries are exempt from level filtering and never consume the tail budget —
                // they are the dividers that make the rest readable.
                if (!isBoundary && !string.IsNullOrEmpty(level) &&
                    !string.Equals(line["level"].AsString(""), level, StringComparison.OrdinalIgnoreCase))
                    continue;

                matched.Add(line);
            }

            if (tail > 0 && matched.Count > tail)
            {
                var trimmed = new List<Json>();
                var budget = tail;

                for (var i = matched.Count - 1; i >= 0 && budget > 0; i--)
                {
                    trimmed.Insert(0, matched[i]);
                    if (!matched[i].Has("event")) budget--;
                }

                matched = trimmed;
            }

            return matched;
        }

        /// <summary>Reads the rotated file first, so a slice spanning a rotation stays in order.</summary>
        private static IEnumerable<string> ReadAllLines(string file)
        {
            var rotated = file + ".1";
            if (File.Exists(rotated))
                foreach (var line in SafeRead(rotated)) yield return line;

            foreach (var line in SafeRead(file)) yield return line;
        }

        /// <summary>
        /// FileShare.ReadWrite because the editor holds the file open for append. Without it, reading
        /// a live session's log would fail with a sharing violation.
        /// </summary>
        private static IEnumerable<string> SafeRead(string path)
        {
            var lines = new List<string>();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Length > 0) lines.Add(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return lines;
        }

        private static string Format(Json line)
        {
            if (line.Has("event"))
                return $"---- {line["event"].AsString("")} (gen {line["gen"].AsInt()}) ----";

            var level = line["level"].AsString("log");
            var marker = level == "error" || level == "assert" ? "E" : level == "warning" ? "W" : " ";
            var text = line["message"].AsString("");

            var stack = line["stack"].AsString();
            if (!string.IsNullOrEmpty(stack)) text += "\n    " + stack.Replace("\n", "\n    ").TrimEnd();

            return $"{marker} {text}";
        }

        private static string ResolveFile(string project, string sessionId, out string note)
        {
            note = null;
            var dir = UrcPaths.ForProject(project);
            if (!Directory.Exists(dir)) return null;

            if (!string.IsNullOrEmpty(sessionId))
            {
                var exact = UrcPaths.SessionLog(project, sessionId);
                if (File.Exists(exact)) return exact;
                note = $"session {sessionId} has no log on disk; falling back to the newest.";
            }

            string newest = null;
            var newestTime = DateTime.MinValue;

            foreach (var candidate in Directory.GetFiles(dir, "session-*.jsonl"))
            {
                var written = File.GetLastWriteTimeUtc(candidate);
                if (written <= newestTime) continue;
                newestTime = written;
                newest = candidate;
            }

            return newest;
        }

        private static string SessionFromDiscovery(string project)
        {
            foreach (var reply in DiscoveryClient.Query(TimeSpan.FromMilliseconds(150)))
                if (ProjectPaths.Equal(reply.ProjectPath, project))
                    return reply.SessionId;

            return null;
        }

        private static string SessionFromCursor(string cursor)
        {
            ParseCursor(cursor, out var session, out _, out _);
            return session;
        }

        /// <summary>Cursor form is `sessionId:generation:seq`.</summary>
        private static void ParseCursor(string cursor, out string session, out int generation, out int seq)
        {
            session = null;
            generation = 0;
            seq = 0;

            if (string.IsNullOrEmpty(cursor)) return;

            var parts = cursor.Split(':');
            if (parts.Length < 3) return;

            session = parts[0];
            int.TryParse(parts[1], out generation);
            int.TryParse(parts[2], out seq);
        }
    }
}
