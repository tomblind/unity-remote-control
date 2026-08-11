using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc.Editor
{
    /// <summary>
    /// The editor's state, published as volatiles for threads that must never touch Unity.
    ///
    /// This is the single most important rule in the package: the discovery responder and the TCP
    /// accept thread answer entirely from the fields here, calling no Unity API and taking no lock
    /// held by the main thread. That is *why* `urc status` works mid-compile and mid-import — the
    /// prior file-protocol tool polled from `EditorApplication.update` and therefore went deaf
    /// exactly when the editor got busy, which is when you most need an answer.
    ///
    /// Every field is written only by <see cref="Pump"/> on the main thread and read from anywhere.
    /// </summary>
    [InitializeOnLoad]
    internal static class UrcEditorState
    {
        private const string GenerationKey = "urc.generation";

        // `volatile` cannot be applied to long, so tick time goes through Volatile.Read/Write.
        private static long _lastTickUtcTicks;

        private static volatile string _state = UrcProtocol.State.Idle;
        private static volatile string _pendingJobId;
        private static volatile int _loadedSnippets;

        /// <summary>
        /// Increments once per domain load, held in SessionState so it survives the reload but dies
        /// with the process. Comparing it across a reload is how a client proves a *specific* reload
        /// finished, rather than inferring it from timing.
        /// </summary>
        public static int Generation { get; }

        /// <summary>Identifies this editor session. Unlike a pid, it cannot be recycled.</summary>
        public static string SessionId { get; }

        public static string ProjectPath { get; }
        public static string UnityVersion { get; }
        public static string PackageVersion { get; }

        static UrcEditorState()
        {
            // Runs during [InitializeOnLoad], so only load-time-safe APIs are touched here.
            Generation = SessionState.GetInt(GenerationKey, 0) + 1;
            SessionState.SetInt(GenerationKey, Generation);

            SessionId = SessionState.GetString("urc.sessionId", null);
            if (string.IsNullOrEmpty(SessionId))
            {
                SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
                SessionState.SetString("urc.sessionId", SessionId);
            }

            var dataPath = Application.dataPath;                       // "<project>/Assets"
            var root = System.IO.Directory.GetParent(dataPath)?.FullName ?? dataPath;
            ProjectPath = ProjectPaths.Canonicalize(root);

            UnityVersion = Application.unityVersion;
            PackageVersion = UrcVersion.Value;

            Stamp();
            // The update subscription belongs to UrcMainThread, which owns the single pump: it stamps
            // this state and drains the dispatch queue in one tick, in that order.
        }

        /// <summary>
        /// Forces this type's static constructor to run on the caller's thread.
        ///
        /// Not ceremony: the constructor calls SessionState and Application.dataPath, which are
        /// main-thread only. Without an explicit touch from [InitializeOnLoad], the first access
        /// could come from the discovery thread — which would run those Unity APIs off the main
        /// thread. Both this type and UrcServer are [InitializeOnLoad], but their relative order is
        /// not guaranteed, so the server calls this before starting any thread.
        /// </summary>
        public static void EnsureInitialized() { }

        public static double SecondsSinceLastTick
        {
            get
            {
                var ticks = Volatile.Read(ref _lastTickUtcTicks);
                if (ticks == 0) return 0;
                var elapsed = DateTime.UtcNow.Ticks - ticks;
                return elapsed <= 0 ? 0 : elapsed / (double)TimeSpan.TicksPerSecond;
            }
        }

        public static string State => _state;
        public static string PendingJobId => _pendingJobId;
        public static int LoadedSnippets => _loadedSnippets;

        public static void SetPendingJob(string jobId) => _pendingJobId = jobId;
        public static void ReportSnippetLoaded() => Interlocked.Increment(ref _loadedSnippets);

        private static volatile int _compileErrors;

        /// <summary>
        /// Mirrored off SessionState so the settle loop can read it from the connection thread —
        /// the journal itself is main-thread only.
        /// </summary>
        public static int CompileErrors => _compileErrors;
        public static void SetCompileErrorCount(int count) => _compileErrors = count;

        /// <summary>
        /// Marks the editor as going down for a reload. Called from beforeAssemblyReload so a client
        /// that catches the discovery reply during the window sees `reloading` rather than a stale
        /// `idle` followed by silence.
        /// </summary>
        public static void MarkReloading() => _state = UrcProtocol.State.Reloading;

        /// <summary>
        /// Order matters: play mode wins over compiling, because a compile during play mode is still
        /// a state where play-mode restrictions apply. `isUpdating` covers asset import.
        ///
        /// Called only from the main-thread pump. Stamping happens BEFORE the queue is drained, so a
        /// long job on this tick does not make the editor look stalled to a client reading the age.
        /// </summary>
        internal static void Stamp()
        {
            Volatile.Write(ref _lastTickUtcTicks, DateTime.UtcNow.Ticks);

            if (EditorApplication.isPlayingOrWillChangePlaymode) _state = UrcProtocol.State.PlayMode;
            else if (EditorApplication.isCompiling) _state = UrcProtocol.State.Compiling;
            else if (EditorApplication.isUpdating) _state = UrcProtocol.State.Importing;
            else if (!string.IsNullOrEmpty(_pendingJobId)) _state = UrcProtocol.State.Busy;
            else _state = UrcProtocol.State.Idle;
        }

        /// <summary>Snapshot for a discovery reply. Safe to call from any thread.</summary>
        public static DiscoveryReply Snapshot(int tcpPort) => new DiscoveryReply
        {
            ProjectPath = ProjectPath,
            UnityVersion = UnityVersion,
            PackageVersion = PackageVersion,
            Pid = UrcProcess.Id,
            TcpPort = tcpPort,
            Generation = Generation,
            State = State,
            SecondsSinceLastTick = SecondsSinceLastTick,
            SessionId = SessionId,
            PendingJobId = PendingJobId,
            LoadedSnippets = LoadedSnippets,
        };
    }

    /// <summary>Process id, resolved once — the reply path must not pay for a lookup per datagram.</summary>
    internal static class UrcProcess
    {
        public static readonly int Id = System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    internal static class UrcVersion
    {
        /// <summary>Kept in step with package.json by hand; there are two of them and both are read by tooling.</summary>
        public const string Value = "0.1.0";
    }
}
