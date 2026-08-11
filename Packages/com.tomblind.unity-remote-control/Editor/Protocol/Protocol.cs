using System;

namespace Urc.Protocol
{
    /// <summary>
    /// Wire constants shared by the CLI and the Unity package. This file is compiled into both, so a
    /// change here breaks both sides at once — which is the point.
    /// </summary>
    public static class UrcProtocol
    {
        /// <summary>
        /// Bumped only for breaking wire changes. Additive fields do not bump it: both sides ignore
        /// unknown keys, so a newer peer stays readable by an older one.
        ///
        /// Normally the CLI ships beside the package and versions match by construction (see the
        /// install model). This exists for the case where they don't, so the failure is a clear
        /// message instead of mysterious behavior.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// Administratively-scoped multicast (239.0.0.0/8), which is never routed off-site. Combined
        /// with a TTL of 0 the traffic never leaves this host at all.
        /// </summary>
        public const string MulticastGroup = "239.255.42.1";

        public const int MulticastPort = 41234;

        /// <summary>
        /// Hop limit 0 keeps datagrams on the originating host. This is what makes discovery safe:
        /// the alternative (broadcast) cannot traverse loopback, so it would have to bind a real
        /// interface and advertise project paths and a control port to the LAN.
        /// </summary>
        public const int MulticastTtl = 0;

        /// <summary>Discriminates our datagrams from anything else sharing the group.</summary>
        public const string Magic = "urc";

        /// <summary>
        /// Discovery replies must not fragment, so they carry counts and states only — never log
        /// text, error messages, or anything else unbounded.
        /// </summary>
        public const int MaxDatagramBytes = 1400;

        /// <summary>A connection must send a valid request within this window or it is dropped.</summary>
        public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(2);

        /// <summary>Client→server request kinds.</summary>
        public static class Op
        {
            public const string Exec = "exec";
            public const string Compile = "compile";
            /// <summary>Live-stream resume. Surfaced to users as `urc resume`.</summary>
            public const string Attach = "attach";
            /// <summary>Discovery query (datagram only).</summary>
            public const string Query = "query";
        }

        /// <summary>Server→client frame kinds.</summary>
        public static class Ev
        {
            /// <summary>Sent unprompted on accept. The server always speaks first.</summary>
            public const string Hello = "hello";
            public const string Accepted = "accepted";
            public const string Log = "log";
            public const string State = "state";
            public const string Reloading = "reloading";
            public const string Result = "result";
            public const string Busy = "busy";
            public const string Error = "error";
            /// <summary>Discovery reply (datagram only).</summary>
            public const string Reply = "reply";
        }

        /// <summary>Editor lifecycle state, reported in discovery replies and `state` frames.</summary>
        public static class State
        {
            public const string Idle = "idle";
            public const string Compiling = "compiling";
            public const string Importing = "importing";
            public const string PlayMode = "playmode";
            public const string Busy = "busy";
            public const string Reloading = "reloading";
        }

        /// <summary>Terminal job outcomes.</summary>
        public static class Status
        {
            public const string Ok = "ok";
            public const string Failed = "failed";
            /// <summary>Job died with the domain — only reachable for async work.</summary>
            public const string Interrupted = "interrupted";
            public const string Busy = "busy";
        }
    }

    /// <summary>
    /// Project-path canonicalization. Both sides must normalize identically or matching produces
    /// phantom "no editor running" errors with the editor sitting right there.
    ///
    /// This is not hypothetical: Claude hands out a lowercase drive letter when launched from VS Code
    /// and an uppercase one from a terminal, and Unity reports its own casing independently of both.
    ///
    /// Symlink resolution is deliberately absent — it needs APIs netstandard2.1 lacks, and the editor
    /// reports the path Unity itself opened. The CLI resolves links before calling this.
    /// </summary>
    public static class ProjectPaths
    {
        /// <summary>
        /// Absolute, forward-slashed, no trailing separator, with a Windows drive letter upper-cased.
        ///
        /// Body case is deliberately preserved — comparison, not storage, is where case-insensitivity
        /// belongs, and folding the whole path would corrupt what users see on case-sensitive
        /// filesystems. The drive letter is the one exception: it is case-insensitive by definition,
        /// and leaving it alone means the same project prints as `f:/…` from one caller and `F:/…`
        /// from another (verified — `Path.GetFullPath` preserves whatever casing it was handed).
        /// </summary>
        public static string Canonicalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string full;
            try { full = System.IO.Path.GetFullPath(path.Trim()); }
            catch (Exception) { return null; }

            full = full.Replace('\\', '/');
            while (full.Length > 1 && full.EndsWith("/", StringComparison.Ordinal))
                full = full.Substring(0, full.Length - 1);

            if (full.Length >= 2 && full[1] == ':' && char.IsLetter(full[0]))
                full = char.ToUpperInvariant(full[0]) + full.Substring(1);

            return full;
        }

        /// <summary>
        /// Compares two canonicalized paths. Windows and macOS default to case-insensitive
        /// filesystems; Linux does not, and treating it as insensitive there would let a request
        /// reach the wrong project — which for a tool that executes arbitrary C# is a real hazard,
        /// not a cosmetic one.
        /// </summary>
        public static bool Equal(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a, b, CaseInsensitiveFileSystem
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }

        private static bool CaseInsensitiveFileSystem =>
            Environment.OSVersion.Platform != PlatformID.Unix ||
            // Unix here covers both macOS and Linux; distinguish by a path only macOS has.
            System.IO.Directory.Exists("/System/Library/CoreServices");
    }
}
