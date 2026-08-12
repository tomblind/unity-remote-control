using System;
using System.IO;
using Urc.Protocol;

namespace Urc.Discovery
{
    /// <summary>
    /// Remembers which process last served a project, so a miss can be diagnosed instead of guessed.
    ///
    /// The problem it solves: during a domain reload NOTHING answers discovery, and in a large
    /// project a reload takes tens of seconds — measured at ~30s of continuous silence in a real
    /// game project. A blind retry cannot tell that apart from "the editor is closed", so it either
    /// gives up too early (the reported flakiness: "no editor running" for an editor that is plainly
    /// there, succeeding on the next call) or makes every genuine absence wait just as long.
    ///
    /// Mid-session the client already resolves this correctly: it holds the pid from before the
    /// reload and watches THAT process. This gives the same signal to a cold start.
    ///
    /// Purely a hint. If it is missing, stale, or wrong, discovery still works — it just falls back
    /// to the short retry. Nothing is correct only because this file exists.
    /// </summary>
    internal static class EditorHint
    {
        private sealed class Hint
        {
            public int Pid;
            public string SessionId;
        }

        private static string PathFor(string canonicalProjectPath) =>
            Path.Combine(UrcPaths.ForProject(canonicalProjectPath), "last-editor.json");

        /// <summary>
        /// Records the editor that answered. Written only when it CHANGES, so a hot loop of commands
        /// does not rewrite the same file over and over.
        /// </summary>
        public static void Remember(DiscoveryReply reply)
        {
            if (reply == null || reply.Pid <= 0 || string.IsNullOrEmpty(reply.ProjectPath)) return;

            try
            {
                var existing = Load(reply.ProjectPath);
                if (existing != null && existing.Pid == reply.Pid) return;

                var path = PathFor(reply.ProjectPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                File.WriteAllText(path, Json.Object()
                    .Set("pid", reply.Pid)
                    .SetIf("sessionId", reply.SessionId)
                    .ToString());
            }
            catch (Exception)
            {
                // A hint that cannot be written costs nothing but the fast path.
            }
        }

        /// <summary>
        /// True when a process that recently served this project is still alive.
        ///
        /// Alive but silent means mid-reload: worth waiting for. Gone means gone: fail now rather
        /// than burn the caller's timeout on an editor that will never answer.
        /// </summary>
        public static bool LastEditorStillAlive(string canonicalProjectPath, out int pid)
        {
            pid = 0;

            var hint = Load(canonicalProjectPath);
            if (hint == null || hint.Pid <= 0) return false;

            pid = hint.Pid;
            return ProcessLiveness.IsAlive(hint.Pid);
        }

        private static Hint Load(string canonicalProjectPath)
        {
            try
            {
                var path = PathFor(canonicalProjectPath);
                if (!File.Exists(path)) return null;
                if (!Json.TryParse(File.ReadAllText(path), out var json)) return null;

                return new Hint
                {
                    Pid = json["pid"].AsInt(),
                    SessionId = json["sessionId"].AsString(),
                };
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
