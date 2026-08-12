using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Urc.Protocol;

namespace Urc
{
    /// <summary>
    /// Works out which Unity project a command is aimed at, and matches it against the editors that
    /// answered discovery.
    ///
    /// The rule that matters: discovery narrows candidates, it never chooses. Running arbitrary C#
    /// against the wrong project because it happened to be the only editor open is a silent failure
    /// with an unbounded blast radius, so a single running editor is never assumed to be the right
    /// one.
    /// </summary>
    public static class ProjectResolver
    {
        public const string EnvVar = "URC_PROJECT";

        private static readonly string[] ProjectMarker = { "ProjectSettings", "ProjectVersion.txt" };

        /// <summary>
        /// Resolution order: explicit flag, then environment, then a walk up from the working
        /// directory. The walk is a convenience for humans in a terminal, never a substitute — it is
        /// genuinely ambiguous in repos that nest the Unity project below the git root, where an
        /// agent at the repo root and one inside the project would resolve differently.
        /// </summary>
        public static string Resolve(string explicitPath, out string source)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                source = "--project";
                return ProjectPaths.Canonicalize(explicitPath);
            }

            var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                source = "$" + EnvVar;
                return ProjectPaths.Canonicalize(fromEnv);
            }

            source = "working directory";
            return WalkUp(Directory.GetCurrentDirectory());
        }

        /// <summary>Nearest enclosing directory containing ProjectSettings/ProjectVersion.txt, like git finding .git.</summary>
        public static string WalkUp(string start)
        {
            DirectoryInfo dir;
            try { dir = new DirectoryInfo(start); }
            catch (Exception) { return null; }

            while (dir != null)
            {
                var marker = Path.Combine(dir.FullName, ProjectMarker[0], ProjectMarker[1]);
                if (File.Exists(marker)) return ProjectPaths.Canonicalize(dir.FullName);
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// A main thread quiet for longer than this is busy, wedged, or not a real editor at all.
        /// Import workers report tick ages in the thousands of seconds — they never tick.
        /// </summary>
        private const double HealthyTickSeconds = 5;

        /// <summary>
        /// The early-exit predicate for discovery.
        ///
        /// Stopping at the first reply that merely matches the project is wrong when more than one
        /// process answers for it: the first responder wins the race and the real editor is never
        /// heard. That is precisely how a command lands on an AssetImportWorker and hangs — measured
        /// live, a worker answered first with a tick age of 10007s while the real editor sat at 0.
        ///
        /// So a reply only ends the search if it looks like something that can actually run a
        /// command. Anything else keeps the window open, and the full candidate set gets ranked.
        /// The healthy case still exits in milliseconds; only the suspicious case pays the window.
        /// </summary>
        /// <summary>
        /// "Did the editor we are looking for answer at all?" — no health check.
        ///
        /// Distinct from <see cref="Satisfies"/> on purpose. A busy editor has a stale tick and so
        /// fails Satisfies, but it is still PRESENT, and retrying for it would add seconds to every
        /// command issued during an import. Only a genuine absence is worth another query.
        /// </summary>
        public static Func<DiscoveryReply, bool> Present(string projectPath, int requiredPid) =>
            reply =>
            {
                if (projectPath == null) return false;
                if (!ProjectPaths.Equal(reply.ProjectPath, projectPath)) return false;
                return requiredPid <= 0 || reply.Pid == requiredPid;
            };

        public static Func<DiscoveryReply, bool> Satisfies(string projectPath, int requiredPid) =>
            reply =>
            {
                if (projectPath == null) return false;
                if (!ProjectPaths.Equal(reply.ProjectPath, projectPath)) return false;

                // An explicit pid is the answer by definition — no need to look further.
                if (requiredPid > 0) return reply.Pid == requiredPid;

                return reply.SecondsSinceLastTick < HealthyTickSeconds;
            };

        /// <summary>Last path segment — what a human calls the project.</summary>
        public static string DisplayName(string canonicalPath)
        {
            if (string.IsNullOrEmpty(canonicalPath)) return "(unknown)";
            var slash = canonicalPath.LastIndexOf('/');
            return slash >= 0 && slash < canonicalPath.Length - 1
                ? canonicalPath.Substring(slash + 1)
                : canonicalPath;
        }

        /// <summary>
        /// Picks the editor serving <paramref name="projectPath"/>, or explains why it cannot.
        ///
        /// The failure message names the editors that ARE running, so an agent can correct itself in
        /// one step instead of guessing — the difference between one wasted call and several.
        /// </summary>
        public static bool TrySelect(
            IReadOnlyList<DiscoveryReply> replies,
            string projectPath,
            out DiscoveryReply match,
            out string error,
            int requiredPid = 0)
        {
            match = null;
            error = null;

            if (projectPath == null)
            {
                error = "could not determine which Unity project to use.\n" +
                        "  Pass --project <path>, set $" + EnvVar + ", or run from inside a Unity project.";
                return false;
            }

            var candidates = new List<DiscoveryReply>();
            foreach (var reply in replies)
            {
                if (!ProjectPaths.Equal(reply.ProjectPath, projectPath)) continue;
                if (requiredPid > 0 && reply.Pid != requiredPid) continue;
                candidates.Add(reply);
            }

            if (requiredPid > 0 && candidates.Count == 0)
            {
                error = $"no editor with pid {requiredPid} is serving {DisplayName(projectPath)}.";
                return false;
            }

            if (candidates.Count > 0)
            {
                // More than one responder for a single project should not happen — the batch-mode
                // guard keeps Unity's AssetImportWorker processes off the air, and Unity will not open
                // the same project twice. If it happens anyway, prefer the one whose main thread is
                // actually ticking: a responder that never ticks cannot run a command, and picking it
                // means a hang until timeout that looks like editor flakiness rather than a bad pick.
                var best = candidates[0];
                foreach (var reply in candidates)
                    if (reply.SecondsSinceLastTick < best.SecondsSinceLastTick) best = reply;

                if (!best.IsCompatible)
                {
                    error = $"editor for {DisplayName(projectPath)} speaks protocol v{best.Protocol}, " +
                            $"this CLI speaks v{UrcProtocol.Version}.\n" +
                            "  The CLI ships beside the Unity package — re-run the installer for this project.";
                    return false;
                }

                if (candidates.Count > 1)
                {
                    Console.Error.WriteLine(
                        $"note: {candidates.Count} editors answered for {DisplayName(projectPath)}; " +
                        $"chose pid {best.Pid} (most recently ticked). Use --pid to be explicit.");
                }

                match = best;
                return true;
            }

            var sb = new StringBuilder();
            sb.Append("no editor running for ").Append(projectPath);
            if (replies.Count > 0)
            {
                sb.Append("\n  running: ");
                for (var i = 0; i < replies.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(DisplayName(replies[i].ProjectPath));
                }
                sb.Append("\n  use --project to target one of those.");
            }
            else
            {
                sb.Append("\n  no Unity editors responded. Is Unity open, and is the package installed?");
            }

            error = sb.ToString();
            return false;
        }
    }
}
