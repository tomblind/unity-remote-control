using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// A read-only status window: Window ▸ Unity Remote Control.
    ///
    /// There is nothing to configure — no port to pick, no server to start, no registration to
    /// perform. That is the point of the design, so this window exists purely to answer "is it
    /// working, and if not, why", which is otherwise invisible from inside the editor.
    /// </summary>
    internal sealed class UrcWindow : EditorWindow
    {
        [MenuItem("Window/Unity Remote Control")]
        private static void Open()
        {
            var window = GetWindow<UrcWindow>();
            window.titleContent = new GUIContent("Remote Control");
            window.minSize = new Vector2(360, 260);
        }

        private void OnEnable()
        {
            // The interesting values change without user input, so repaint on a timer rather than
            // only on interaction.
            EditorApplication.update += Repaint;
        }

        private void OnDisable() => EditorApplication.update -= Repaint;

        /// <summary>
        /// Height of the history list, measured from the window rect rather than left to the layout
        /// system. `GUILayout.ExpandHeight(true)` does not distribute the leftover space here, so the
        /// list sat at its minimum with the rest of the window empty below it.
        ///
        /// Applied only on the Layout event. IMGUI runs Layout then Repaint over the same frame, and
        /// feeding a different size to each half throws "GUILayout mismatched" errors — so a fresh
        /// measurement taken during Repaint lands on the NEXT frame, never mid-frame.
        /// </summary>
        private float _historyHeight = 140;
        private float _measuredHistoryHeight = 140;

        private const float MinHistoryHeight = 80;

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout) _historyHeight = _measuredHistoryHeight;

            EditorGUILayout.Space(4);

            var running = UrcServer.IsRunning;
            EditorGUILayout.HelpBox(
                running
                    ? $"Listening on 127.0.0.1:{UrcServer.Port}"
                    : "Not running. " + (UrcServer.LastError ?? "See the console."),
                running ? MessageType.Info : MessageType.Error);

            if (!string.IsNullOrEmpty(UrcServer.LastError) && running)
                EditorGUILayout.HelpBox(UrcServer.LastError, MessageType.Warning);

            // The one degradation that presents as a mysterious hang rather than an error.
            if (!UrcMainThread.CanRunUnfocused)
            {
                EditorGUILayout.HelpBox(
                    "Unfocused execution is unavailable: an internal EditorApplication API could not " +
                    "be resolved. Commands will only complete while this window has focus.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);

            Row("Project", UrcEditorState.ProjectPath);
            Row("Unity", UrcEditorState.UnityVersion);
            Row("Package", UrcVersion.Value + $"  (protocol v{UrcProtocol.Version})");

            EditorGUILayout.Space(6);

            Row("State", UrcEditorState.State);
            Row("Generation", UrcEditorState.Generation.ToString());
            Row("Session", UrcEditorState.SessionId);

            var tickAge = UrcEditorState.SecondsSinceLastTick;
            Row("Last tick", tickAge < 1 ? "just now" : $"{tickAge:0.#}s ago");

            var pending = UrcEditorState.PendingJobId;
            Row("Pending job", string.IsNullOrEmpty(pending) ? "none" : pending);

            // Each exec loads an assembly that cannot be unloaded until the next domain reload.
            // Surfacing the count makes the growth observable rather than mysterious.
            Row("Snippets loaded", UrcEditorState.LoadedSnippets.ToString() + "  (until next reload)");

            var errors = UrcEditorState.CompileErrors;
            if (errors > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    $"The project has {errors} compile error{(errors == 1 ? "" : "s")}. " +
                    "Old code is still live — a failed compile reloads nothing.",
                    MessageType.Error);
            }

            EditorGUILayout.Space(8);

            var log = UrcPaths.SessionLog(UrcEditorState.ProjectPath, UrcEditorState.SessionId);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Console capture", GUILayout.Width(110));
                if (GUILayout.Button("Reveal", GUILayout.Width(70))) Reveal(log);
                EditorGUILayout.SelectableLabel(File.Exists(log) ? "on disk" : "not yet written",
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Drive this editor from the project root:", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(".urc/urc exec --code 'return 2+2;'",
                EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            DrawHistory();
        }

        private Vector2 _historyScroll;

        /// <summary>
        /// Recent requests, newest first.
        ///
        /// This is the part of the window that answers questions nothing else can. When an agent is
        /// driving, things change in your project that you did not do — and without a visible record,
        /// "why did my scene move?" or "is anything even connected?" have no answer short of reading
        /// a log file.
        /// </summary>
        private void DrawHistory()
        {
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Recent requests", EditorStyles.boldLabel);
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    UrcHistory.Clear();
                    _expanded.Clear();
                    _expandedSources.Clear();
                    _sliceCache.Clear();
                }
            }

            // Measure the space left below this header. Repaint is the only event with real rects;
            // the value is stored for the next frame's Layout pass (see _historyHeight).
            if (Event.current.type == EventType.Repaint)
            {
                var header = GUILayoutUtility.GetLastRect();
                _measuredHistoryHeight = Mathf.Max(MinHistoryHeight, position.height - header.yMax - 8);
            }

            var entries = UrcHistory.Recent();
            if (entries.Count == 0)
            {
                EditorGUILayout.LabelField("nothing yet this session", EditorStyles.miniLabel);
                return;
            }

            using var scroll = new EditorGUILayout.ScrollViewScope(
                _historyScroll, GUILayout.Height(_historyHeight));
            _historyScroll = scroll.scrollPosition;

            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                var expanded = _expanded.Contains(entry.JobId);

                using (new EditorGUILayout.HorizontalScope())
                {
                    // A button rather than EditorGUILayout.Foldout: Foldout accepts no layout
                    // options, so it claims the rest of the line and shoves every other column off
                    // the row. A fixed-width button gives exact control.
                    if (GUILayout.Button(expanded ? "▼" : "▶", EditorStyles.label, GUILayout.Width(14)))
                    {
                        if (expanded) _expanded.Remove(entry.JobId);
                        else
                        {
                            _expanded.Add(entry.JobId);

                            // Open the last source by default. When sources were combined, the
                            // trailing --code is what the agent actually invoked; the snippet files
                            // above it are library text that is usually already familiar.
                            var last = SourceCount(entry) - 1;
                            if (last > 0) _expandedSources.Add(SourceKey(entry, last));
                        }
                    }

                    EditorGUILayout.LabelField(Glyph(entry.Status), StatusStyle(entry.Status), GUILayout.Width(16));
                    EditorGUILayout.LabelField(entry.Cmd, EditorStyles.miniLabel, GUILayout.Width(56));
                    EditorGUILayout.LabelField(Age(entry.FinishedAtUtc), EditorStyles.miniLabel, GUILayout.Width(52));
                    EditorGUILayout.LabelField($"{entry.DurationMs} ms", EditorStyles.miniLabel, GUILayout.Width(62));
                    EditorGUILayout.LabelField(entry.Summary, EditorStyles.miniLabel);
                }

                if (expanded) DrawExpanded(entry);
            }
        }

        private readonly HashSet<string> _expanded = new HashSet<string>();

        /// <summary>Foldout state for individual sources, keyed by job id and source index.</summary>
        private readonly HashSet<string> _expandedSources = new HashSet<string>();

        private static string SourceKey(UrcHistory.Entry entry, int index) => entry.JobId + "#" + index;

        private static int SourceCount(UrcHistory.Entry entry) =>
            entry.Sources == null ? 0 : entry.Sources.Count;

        private void DrawExpanded(UrcHistory.Entry entry)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                DrawRequest(entry);
                DrawPane("Response", entry.Response);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"job {entry.JobId} · gen {entry.Generation} · client pid {entry.ClientPid}",
                        EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// The request, split back into the sources the CLI combined.
        ///
        /// An agent composing three snippet files and a trailing --code arrives here as one long
        /// concatenation, which is not something a person can scan — and being able to see what an
        /// agent ran is the entire reason this window has a history. The CLI sends the line spans
        /// alongside the code precisely so the concatenation can be undone here.
        /// </summary>
        private void DrawRequest(UrcHistory.Entry entry)
        {
            // One source is already exactly what ran; splitting it would add a foldout around
            // nothing. Same for an older entry recorded before spans were sent.
            if (SourceCount(entry) < 2) { DrawPane("Request", entry.Request); return; }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Request", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField($"{SourceCount(entry)} sources", EditorStyles.miniLabel);
                if (GUILayout.Button("Copy all", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.systemCopyBuffer = entry.Request ?? "";
                    ShowNotice("Request copied");
                }
            }

            using (new EditorGUI.IndentLevelScope())
            {
                var index = 0;
                foreach (var span in entry.Sources.Items)
                {
                    var key = SourceKey(entry, index);
                    var open = _expandedSources.Contains(key);
                    var count = span["lines"].AsInt();

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(open ? "▼" : "▶", EditorStyles.label, GUILayout.Width(14)))
                        {
                            if (open) _expandedSources.Remove(key);
                            else _expandedSources.Add(key);
                        }

                        EditorGUILayout.LabelField(FileName(span["name"].AsString("?")), EditorStyles.miniLabel);
                        EditorGUILayout.LabelField($"{count} line{(count == 1 ? "" : "s")}",
                            EditorStyles.miniLabel, GUILayout.Width(56));

                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
                        {
                            EditorGUIUtility.systemCopyBuffer = Slice(entry, span, index);
                            ShowNotice("Snippet copied");
                        }
                    }

                    if (open) DrawText(Slice(entry, span, index));
                    index++;
                }
            }
        }

        /// <summary>A labelled pane with a copy button.</summary>
        private static void DrawPane(string label, string text)
        {
            var content = string.IsNullOrEmpty(text) ? "(empty)" : text;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(70));
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    EditorGUIUtility.systemCopyBuffer = content;
                    ShowNotice($"{label} copied");
                }
            }

            DrawText(content);
        }

        /// <summary>
        /// A read-only, selectable text block.
        ///
        /// TextArea rather than SelectableLabel because selection survives the repaint this window
        /// does every tick — a SelectableLabel loses it, which makes manual selection impossible on
        /// a live-updating window. The copy buttons exist for the same reason: they are the reliable
        /// path when the text is long.
        /// </summary>
        private static void DrawText(string text)
        {
            var content = string.IsNullOrEmpty(text) ? "(empty)" : text;

            var lines = Mathf.Clamp(content.Split('\n').Length, 1, 12);
            var height = lines * EditorGUIUtility.singleLineHeight + 6;

            // Read-only by discarding the result: editable-looking text the user cannot actually
            // change would be worse than a pane that is obviously inert.
            EditorGUILayout.TextArea(content, MonoStyle, GUILayout.Height(height));
        }

        /// <summary>
        /// One source's text, cut out of the combined request.
        ///
        /// Cached because this window repaints on every editor tick, and re-splitting the request
        /// a hundred times a second to redraw a pane that cannot have changed is pure waste. A
        /// history entry never mutates once recorded, so job id plus source index is a stable key.
        /// </summary>
        private readonly Dictionary<string, string> _sliceCache = new Dictionary<string, string>();

        private string Slice(UrcHistory.Entry entry, Json span, int index)
        {
            var key = SourceKey(entry, index);
            if (_sliceCache.TryGetValue(key, out var cached)) return cached;

            var text = SliceLines(entry.Request, span["line"].AsInt(1), span["lines"].AsInt());

            // Only expanded sources ever land here, but a long session should not accumulate
            // without limit; dropping the lot is fine, it rebuilds on the next repaint.
            if (_sliceCache.Count > 64) _sliceCache.Clear();

            _sliceCache[key] = text;
            return text;
        }

        private static string SliceLines(string text, int startLine, int lineCount)
        {
            if (string.IsNullOrEmpty(text) || lineCount <= 0) return "";

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var from = Mathf.Clamp(startLine - 1, 0, lines.Length);

            // The retained request is capped, so a span can point past what survived.
            if (from >= lines.Length) return "(truncated — use Copy all)";

            var to = Mathf.Min(from + lineCount, lines.Length);
            return string.Join("\n", lines, from, to - from);
        }

        /// <summary>A full path eats the row; the file name is what identifies a snippet.</summary>
        private static string FileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";

            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            return slash >= 0 && slash < name.Length - 1 ? name.Substring(slash + 1) : name;
        }

        private static void ShowNotice(string message)
        {
            var window = focusedWindow;
            if (window != null) window.ShowNotification(new GUIContent(message), 1.0);
        }

        private static GUIStyle _monoStyle;

        private static GUIStyle MonoStyle =>
            _monoStyle ??= new GUIStyle(EditorStyles.textArea)
            {
                font = EditorStyles.miniLabel.font,
                wordWrap = false,
                richText = false,
            };

        private static string Glyph(string status)
        {
            switch (status)
            {
                case UrcProtocol.Status.Ok: return "✔";
                case UrcProtocol.Status.Interrupted: return "~";
                default: return "✖";
            }
        }

        // Cached styles with an explicit textColor. GUI.color does NOT reliably tint label text —
        // the editor skin's own normal.textColor wins — which is why the glyphs rendered plain.
        private static GUIStyle _okStyle, _failStyle, _warnStyle;

        private static GUIStyle StatusStyle(string status)
        {
            switch (status)
            {
                case UrcProtocol.Status.Ok:
                    return _okStyle ??= Tinted(new Color(0.35f, 0.78f, 0.38f));
                case UrcProtocol.Status.Interrupted:
                    return _warnStyle ??= Tinted(new Color(0.92f, 0.73f, 0.25f));
                default:
                    return _failStyle ??= Tinted(new Color(0.90f, 0.40f, 0.40f));
            }
        }

        private static GUIStyle Tinted(Color color)
        {
            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.focused.textColor = color;
            return style;
        }

        /// <summary>Relative time, because "38s ago" answers "was that mine?" and a timestamp does not.</summary>
        private static string Age(string finishedAtUtc)
        {
            if (!DateTime.TryParse(finishedAtUtc, out var finished)) return "";

            var elapsed = DateTime.UtcNow - finished.ToUniversalTime();
            if (elapsed.TotalSeconds < 1) return "now";
            if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            return $"{(int)elapsed.TotalHours}h ago";
        }

        private static void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(110));
                // Selectable so a path or session id can be copied out rather than retyped.
                EditorGUILayout.SelectableLabel(value ?? "-",
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) EditorUtility.RevealInFinder(path);
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                        EditorUtility.RevealInFinder(dir);
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[urc] could not reveal {path}: {e.Message}");
            }
        }
    }
}
