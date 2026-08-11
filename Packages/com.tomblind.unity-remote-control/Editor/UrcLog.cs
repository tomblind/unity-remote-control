using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// Captures the editor console to an append-only JSONL file — but ONLY WHILE A COMMAND IS
    /// RUNNING.
    ///
    /// This is what makes results cheap. Console output and stack traces never travel back in a
    /// result frame; they would flood an agent's context, where they persist for the whole session.
    /// A result carries counts and a cursor instead, and the caller fetches the text only when the
    /// counts justify it. `urc logs` then reads the file DIRECTLY, with no editor involved, so it
    /// still answers while the editor is wedged or mid-reload.
    ///
    /// WHY NOT CAPTURE CONTINUOUSLY. An earlier version did, justified as crash forensics — which
    /// was mostly wrong: Unity already writes Editor.log, containing this output plus native detail
    /// we never see, so we were duplicating it. What this file uniquely provides is STRUCTURE
    /// (gen/seq cursors, levels, trimmed stacks, boundary markers), and that is only useful around a
    /// command. Meanwhile a real project logs constantly — the reference project churns heavily —
    /// so always-on capture meant a file write per line, flushed, forever, to record output nobody
    /// asked about. The prior tool captured only during commands and that proved sufficient.
    ///
    /// When capture is off the hook stays subscribed and costs one volatile read per log line.
    /// </summary>
    [InitializeOnLoad]
    internal static class UrcLog
    {
        /// <summary>Rotate past this, keeping one previous file. Bounded, but generous enough to cover a session.</summary>
        private const long MaxBytes = 8 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _path;
        private static int _seq;

        static UrcLog()
        {
            try
            {
                _path = UrcPaths.SessionLog(UrcEditorState.ProjectPath, UrcEditorState.SessionId);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                Rotate();
                Open();
            }
            catch (Exception e)
            {
                // Logging must never take the server down with it.
                Debug.LogWarning($"[urc] console capture unavailable: {e.Message}");
                return;
            }

            // The domain-load boundary is NOT written here: capture is off at load, and an idle
            // editor reloading is not worth a line. MarkGeneration emits it lazily, the first time
            // this domain actually writes something. Boundaries are exempt from level filtering and
            // never consume a --tail budget, so they stay free to the reader either way.
            Application.logMessageReceivedThreaded += OnLogThreaded;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting += Close;
        }

        /// <summary>
        /// Position in the stream, as `sessionId:generation:seq`. Stamped into results at DISPATCH,
        /// so `urc logs --since <cursor>` shows everything logged after the command started — across
        /// any number of domain reloads. It is a watermark, not a domain filter.
        /// </summary>
        public static string Cursor =>
            $"{UrcEditorState.SessionId}:{UrcEditorState.Generation}:{Volatile.Read(ref _seq)}";

        public static int ErrorCount => Volatile.Read(ref _errors);
        public static int WarningCount => Volatile.Read(ref _warnings);
        public static int TotalCount => Volatile.Read(ref _total);

        private static int _errors;
        private static int _warnings;
        private static int _total;

        /// <summary>
        /// Refcounted, because the bracket spans a command AND its settle window, and a re-attach
        /// after a domain reload re-enters it. Reset implicitly by each new domain, which is correct:
        /// a fresh domain captures nothing until a command asks it to.
        /// </summary>
        private static int _capturing;

        public static bool IsCapturing => Volatile.Read(ref _capturing) > 0;

        /// <summary>
        /// Opens the capture window. Safe from the connection thread — an interlocked increment,
        /// no Unity API — so capture starts immediately rather than on the next editor tick.
        /// </summary>
        public static void BeginCapture() => Interlocked.Increment(ref _capturing);

        public static void EndCapture()
        {
            // Clamp rather than let a stray release drive this negative and silently disable capture
            // for the rest of the session.
            if (Interlocked.Decrement(ref _capturing) < 0) Interlocked.Exchange(ref _capturing, 0);
        }

        /// <summary>Counts since a given point, for the compact summary a result frame carries.</summary>
        public static Json SummarySince(int errorsAt, int warningsAt, int totalAt, string cursor)
        {
            var errors = ErrorCount - errorsAt;
            var warnings = WarningCount - warningsAt;
            var total = TotalCount - totalAt;

            if (errors <= 0 && warnings <= 0 && total <= 0) return null;

            return Json.Object()
                .Set("errors", Math.Max(0, errors))
                .Set("warnings", Math.Max(0, warnings))
                .Set("total", Math.Max(0, total))
                .SetIf("since", cursor);
        }

        public static void Snapshot(out int errors, out int warnings, out int total)
        {
            errors = ErrorCount;
            warnings = WarningCount;
            total = TotalCount;
        }

        /// <summary>
        /// Fires on ANY thread — Unity's threaded log callback is the whole reason this can capture
        /// output from background work, but it also means everything here must be thread-safe and
        /// must never throw back into the caller.
        /// </summary>
        private static void OnLogThreaded(string message, string stackTrace, LogType type)
        {
            // The cheap early-out that makes this affordable in a project that logs constantly:
            // one volatile read, no allocation, no IO.
            if (!IsCapturing) return;

            try
            {
                switch (type)
                {
                    case LogType.Error:
                    case LogType.Exception:
                    case LogType.Assert:
                        Interlocked.Increment(ref _errors);
                        break;
                    case LogType.Warning:
                        Interlocked.Increment(ref _warnings);
                        break;
                }
                Interlocked.Increment(ref _total);

                var line = Json.Object()
                    .Set("ts", DateTime.UtcNow.ToString("o"))
                    .Set("level", Level(type))
                    .Set("gen", UrcEditorState.Generation)
                    .Set("seq", Interlocked.Increment(ref _seq))
                    .Set("message", message ?? "")
                    // Only errors carry a stack: a warning's stack is noise, and this file is read by
                    // people and agents looking for a cause.
                    .SetIf("stack", IsError(type) && !string.IsNullOrEmpty(stackTrace)
                        ? Json.String(TrimStack(stackTrace))
                        : null);

                Write(line);
            }
            catch (Exception)
            {
                // A logging failure must never propagate into whatever produced the log.
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            // From playModeStateChanged rather than inferred, so boundaries appear even with
            // "Enter Play Mode" domain reloading disabled.
            if (change == PlayModeStateChange.EnteredPlayMode) WriteEvent("play-enter");
            else if (change == PlayModeStateChange.ExitingPlayMode) WriteEvent("play-exit");
        }

        private static void OnBeforeReload()
        {
            WriteEvent("domain-unload");
            Close();
        }

        private static void WriteEvent(string name)
        {
            Write(Json.Object()
                .Set("ts", DateTime.UtcNow.ToString("o"))
                .Set("level", "info")
                .Set("gen", UrcEditorState.Generation)
                .Set("seq", Interlocked.Increment(ref _seq))
                .Set("event", name)
                .Set("message", name));
        }

        private static int _lastWrittenGeneration;

        private static void Write(Json line)
        {
            if (!IsCapturing) return;

            lock (Gate)
            {
                if (_writer == null) return;
                MarkGeneration();
                WriteRaw(line);
            }
        }

        /// <summary>
        /// Emits a domain-load boundary lazily, the first time a generation writes anything.
        ///
        /// Since capture only runs during commands, an editor that reloads while idle would
        /// otherwise leave a `domain-unload` with no matching `domain-load` — the stream would look
        /// truncated rather than merely quiet. Writing the marker on first use keeps it coherent
        /// without any always-on cost. Caller holds the lock.
        /// </summary>
        private static void MarkGeneration()
        {
            var generation = UrcEditorState.Generation;
            if (_lastWrittenGeneration == generation) return;
            _lastWrittenGeneration = generation;

            WriteRaw(Json.Object()
                .Set("ts", DateTime.UtcNow.ToString("o"))
                .Set("level", "info")
                .Set("gen", generation)
                .Set("seq", Interlocked.Increment(ref _seq))
                .Set("event", "domain-load")
                .Set("message", "domain-load"));
        }

        private static void WriteRaw(Json line)
        {
            try
            {
                _writer.WriteLine(line.ToString());
            }
            catch (Exception)
            {
                // Disk full, permissions, a closed handle during shutdown. Drop the line rather than
                // take the editor down.
                _writer = null;
            }
        }

        private static void Open()
        {
            lock (Gate)
            {
                // FileShare.ReadWrite so `urc logs` can read the file while the editor holds it open.
                var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    NewLine = "\n",
                    // Per-line flush: the point of this file is surviving a crash, and a buffered
                    // tail is exactly the part you lose.
                    AutoFlush = true,
                };
            }
        }

        private static void Rotate()
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length < MaxBytes) return;

                var previous = _path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(_path, previous);
            }
            catch (Exception)
            {
                // Rotation is best-effort; a file we cannot rotate is still a file we can append to.
            }
        }

        private static void Close()
        {
            lock (Gate)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch (Exception) { }
                _writer = null;
            }
        }

        /// <summary>
        /// Cuts our own plumbing off the bottom of a stack trace.
        ///
        /// A single Debug.LogError from a snippet produces ~25 frames, of which two are the caller's:
        /// the rest is Roslyn's submission machinery, async builders, this package, and the editor
        /// update loop. Shipping all of it defeats the point of keeping output out of results — the
        /// noise just moves into the log an agent then reads.
        ///
        /// Frames are innermost-first, so everything from the first plumbing frame down is dropped.
        /// A trace with no plumbing markers (an ordinary project error) is untouched apart from the cap.
        /// </summary>
        internal static string TrimStack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return stack;

            var lines = stack.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (IsPlumbing(line)) break;

                kept.Add(line.TrimEnd());
                if (kept.Count >= MaxStackFrames) break;
            }

            if (kept.Count == 0) return null;

            var trimmed = lines.Length > kept.Count;
            var text = string.Join("\n", kept.ToArray());
            return trimmed ? text + "\n  … (urc runner frames omitted)" : text;
        }

        private const int MaxStackFrames = 12;

        private static bool IsPlumbing(string frame) =>
            frame.IndexOf("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal) >= 0 ||
            frame.IndexOf("Microsoft.CodeAnalysis.Scripting", StringComparison.Ordinal) >= 0 ||
            frame.IndexOf("Urc.Editor", StringComparison.Ordinal) >= 0 ||
            frame.IndexOf("Submission#0:<Initialize>", StringComparison.Ordinal) >= 0 ||
            frame.IndexOf("Submission#0:<Factory>", StringComparison.Ordinal) >= 0 ||
            frame.IndexOf("EditorApplication:Internal_CallUpdateFunctions", StringComparison.Ordinal) >= 0;

        private static bool IsError(LogType type) =>
            type == LogType.Error || type == LogType.Exception || type == LogType.Assert;

        private static string Level(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                default: return "log";
            }
        }
    }
}
