using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// Watches every compile, whether or not anyone asked for one.
    ///
    /// This is ambient by design. A compile can start because an agent edited a file, because a
    /// snippet ran `git pull`, or because someone saved in their IDE — so "did the project just
    /// break?" is a question that must be answerable regardless of who triggered it. It is also why
    /// `compile` is a thin trigger rather than a special mode: the observation machinery is shared.
    ///
    /// Errors are captured STRUCTURALLY from CompilerMessage, never scraped from console text —
    /// scraping breaks on localized editors and multi-line messages.
    ///
    /// Results are journalled to SessionState, because the reload that follows a successful compile
    /// destroys everything in memory, and the report has to outlive it.
    /// </summary>
    [InitializeOnLoad]
    internal static class UrcCompileWatch
    {
        private const string ErrorsKey = "urc.compile.errors";
        private const string EpochKey = "urc.compile.epoch";

        private static readonly List<Entry> Pending = new List<Entry>();
        private static readonly object Gate = new object();

        internal sealed class Entry
        {
            public string File;
            public int Line;
            public string Code;
            public string Message;
            /// <summary>How many call sites produced this same error. One missing type yields hundreds.</summary>
            public int Count = 1;
        }

        static UrcCompileWatch()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>Bumped on every compile, so a client can tell one compile's report from the next.</summary>
        public static int Epoch => SessionState.GetInt(EpochKey, 0);

        private static void OnCompilationStarted(object _)
        {
            lock (Gate) Pending.Clear();
            SessionState.SetInt(EpochKey, Epoch + 1);
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;

            lock (Gate)
            {
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error) continue;

                    var code = ExtractCode(message.message);
                    var text = Normalize(message.message);

                    // Deduplicate as we go. A single missing type produces the same error at every
                    // call site; three DISTINCT problems shown inline are worth far more to a caller
                    // than the same one three times.
                    var existing = Pending.Find(e => e.Code == code && e.Message == text);
                    if (existing != null) { existing.Count++; continue; }

                    Pending.Add(new Entry
                    {
                        File = Relative(message.file),
                        Line = message.line,
                        Code = code,
                        Message = text,
                    });
                }
            }
        }

        private static void OnCompilationFinished(object _)
        {
            lock (Gate)
            {
                var array = Json.Array();
                foreach (var entry in Pending)
                {
                    array.Add(Json.Object()
                        .SetIf("file", entry.File)
                        .Set("line", entry.Line)
                        .SetIf("code", entry.Code)
                        .Set("message", entry.Message)
                        .Set("count", entry.Count));
                }

                // Survives the reload that a SUCCESSFUL compile triggers. A failed compile reloads
                // nothing — the old code stays live, which is itself worth telling the caller.
                SessionState.SetString(ErrorsKey, array.ToString());
                UrcEditorState.SetCompileErrorCount(Pending.Count);
            }
        }

        /// <summary>The last compile's errors, deduplicated. Main thread only (SessionState).</summary>
        public static Json LastErrors()
        {
            var raw = SessionState.GetString(ErrorsKey, "");
            if (string.IsNullOrEmpty(raw)) return Json.Array();
            return Json.TryParse(raw, out var json) && json.ValueKind == Json.Kind.Array ? json : Json.Array();
        }

        /// <summary>
        /// A compile report for a result frame: distinct errors first, the rest deferred.
        /// Kept structurally separate from a command's own status — "my command failed" and "the
        /// project no longer compiles" are different facts, and conflating them makes an agent debug
        /// the wrong thing.
        /// </summary>
        public static Json Report(int inlineLimit = 3)
        {
            var errors = LastErrors();
            if (errors.Count == 0) return Json.Object().Set("status", "ok").Set("errorCount", 0);

            var inline = Json.Array();
            var shown = 0;
            foreach (var error in errors.Items)
            {
                if (shown++ >= inlineLimit) break;
                inline.Add(error);
            }

            return Json.Object()
                .Set("status", "failed")
                .Set("errorCount", errors.Count)
                .Set("errors", inline)
                .Set("note", "old code is still live — a failed compile reloads nothing.");
        }

        private static string ExtractCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            // "error CS0246: The type ..." — take the token after "error ".
            var marker = message.IndexOf("error ", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return null;

            var start = marker + "error ".Length;
            var colon = message.IndexOf(':', start);
            if (colon <= start) return null;

            var code = message.Substring(start, colon - start).Trim();
            return code.Length is > 0 and <= 12 ? code : null;
        }

        private static string Normalize(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";

            // Drop the leading "<file>(line,col): error CSxxxx: " so identical problems at different
            // call sites collapse to one entry.
            var marker = message.IndexOf(": error ", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var colon = message.IndexOf(':', marker + ": error ".Length);
                if (colon > 0) return message.Substring(colon + 1).Trim();
            }

            return message.Trim();
        }

        private static string Relative(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;

            var normalized = file.Replace('\\', '/');
            var project = UrcEditorState.ProjectPath;
            if (!string.IsNullOrEmpty(project) &&
                normalized.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(project.Length).TrimStart('/');
            }

            return normalized;
        }
    }
}
