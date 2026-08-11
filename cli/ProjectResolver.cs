using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc
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
            out string error)
        {
            match = null;
            error = null;

            if (projectPath == null)
            {
                error = "could not determine which Unity project to use.\n" +
                        "  Pass --project <path>, set $" + EnvVar + ", or run from inside a Unity project.";
                return false;
            }

            foreach (var reply in replies)
            {
                if (!ProjectPaths.Equal(reply.ProjectPath, projectPath)) continue;

                if (!reply.IsCompatible)
                {
                    error = $"editor for {DisplayName(projectPath)} speaks protocol v{reply.Protocol}, " +
                            $"this CLI speaks v{UrcProtocol.Version}.\n" +
                            "  The CLI ships beside the Unity package — re-run the installer for this project.";
                    return false;
                }

                match = reply;
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
