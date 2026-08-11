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

        private void OnGUI()
        {
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
                    UrcHistory.Clear();
            }

            var entries = UrcHistory.Recent();
            if (entries.Count == 0)
            {
                EditorGUILayout.LabelField("nothing yet this session", EditorStyles.miniLabel);
                return;
            }

            using var scroll = new EditorGUILayout.ScrollViewScope(_historyScroll, GUILayout.MinHeight(120));
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
                        else _expanded.Add(entry.JobId);
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

        private void DrawExpanded(UrcHistory.Entry entry)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                DrawPane("Request", entry.Request);
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
        /// A read-only, scrollable, selectable text pane with a copy button.
        ///
        /// TextArea rather than SelectableLabel because selection survives the repaint this window
        /// does every tick — a SelectableLabel loses it, which makes manual selection impossible on
        /// a live-updating window. The copy button exists for the same reason: it is the reliable
        /// path when the text is long.
        /// </summary>
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

            var lines = Mathf.Clamp(content.Split('\n').Length, 1, 12);
            var height = lines * EditorGUIUtility.singleLineHeight + 6;

            // Read-only by discarding the result: editable-looking text the user cannot actually
            // change would be worse than a pane that is obviously inert.
            EditorGUILayout.TextArea(content, MonoStyle, GUILayout.Height(height));
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
