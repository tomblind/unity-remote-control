using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Sunblink.Urc.Protocol;

namespace Sunblink.Urc.Editor
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
