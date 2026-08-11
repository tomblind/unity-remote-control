using System;
using System.IO;
using System.Text;

namespace Urc.Protocol
{
    /// <summary>
    /// Where logs and artifacts live. Shared, because the CLI reads files the editor writes.
    ///
    /// A USER-GLOBAL cache directory, never the project tree. Two reasons, and the second is the one
    /// that decides it:
    ///
    /// 1. Zero project footprint — nothing to hide from git, no .gitignore to check, none of the
    ///    apparatus both prior tools needed.
    /// 2. `git clean -xdf` deletes an Assets-mode install, and Unity wipes `Temp/` on launch. A
    ///    crashed session's log is precisely what you go looking for afterwards, so it must not live
    ///    anywhere that routine cleanup destroys.
    /// </summary>
    public static class UrcPaths
    {
        /// <summary>
        /// %LOCALAPPDATA%/urc on Windows, ~/Library/Application Support/urc on macOS,
        /// $XDG_STATE_HOME/urc (else ~/.local/state/urc) on Linux.
        /// </summary>
        public static string Root
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable("URC_HOME");
                if (!string.IsNullOrEmpty(overridden)) return overridden;

                if (Environment.OSVersion.Platform != PlatformID.Unix)
                {
                    var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                    if (!string.IsNullOrEmpty(local)) return Path.Combine(local, "urc");
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (Directory.Exists("/System/Library/CoreServices"))   // macOS
                    return Path.Combine(home, "Library", "Application Support", "urc");

                var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                return string.IsNullOrEmpty(xdg)
                    ? Path.Combine(home, ".local", "state", "urc")
                    : Path.Combine(xdg, "urc");
            }
        }

        /// <summary>
        /// Per-project directory: a readable name plus a hash, so it is both greppable by a human and
        /// unambiguous between two projects that share a folder name.
        /// </summary>
        public static string ForProject(string canonicalProjectPath)
        {
            var name = Sanitize(LastSegment(canonicalProjectPath));
            var hash = StableHash(canonicalProjectPath ?? "");
            return Path.Combine(Root, "projects", $"{name}-{hash}");
        }

        public static string SessionLog(string canonicalProjectPath, string sessionId) =>
            Path.Combine(ForProject(canonicalProjectPath), $"session-{Sanitize(sessionId)}.jsonl");

        public static string ArtifactDir(string canonicalProjectPath) =>
            Path.Combine(ForProject(canonicalProjectPath), "artifacts");

        /// <summary>
        /// FNV-1a, NOT string.GetHashCode.
        ///
        /// .NET randomizes GetHashCode per process, so the editor and the CLI would compute different
        /// directories for the same project and the CLI would silently read nothing. This must stay
        /// deterministic across processes, machines and runtimes.
        ///
        /// Case-folded, because the same project reaches us with different drive-letter casing
        /// depending on who launched the caller.
        /// </summary>
        public static string StableHash(string value)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            var hash = offset;
            foreach (var c in value.ToLowerInvariant())
            {
                hash ^= c;
                hash *= prime;
            }

            return hash.ToString("x8");
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return "project";
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
            return string.IsNullOrEmpty(name) ? "project" : name;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');

            var result = sb.ToString().Trim('-');
            return result.Length == 0 ? "unknown" : result;
        }
    }
}
