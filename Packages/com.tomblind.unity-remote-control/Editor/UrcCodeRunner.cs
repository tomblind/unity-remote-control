using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using UnityEngine;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// Evaluates C# on the editor main thread via Roslyn scripting.
    ///
    /// Requires Roslyn to be present — either the optional com.tomblind.unity-remote-control.roslyn
    /// package or the project's own copy. This code names no specific Roslyn assembly, so it binds to
    /// whichever is loaded; but it does not compile with none.
    /// </summary>
    internal static class UrcCodeRunner
    {
        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.IO",
            "System.Threading",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEditor",
            "UnityEngine.SceneManagement",
            "UnityEditor.SceneManagement",
        };

        private static ScriptOptions _baseOptions;
        private static InteractiveAssemblyLoader _loader;

        public sealed class RunResult
        {
            public string Status;
            public string Summary;
            public Json Value;
            public bool Truncated;
            public string ValueArtifact;
            public List<string> AutoUsings;
        }

        /// <summary>
        /// Beyond this, a returned string is written to an artifact and the result carries a preview
        /// plus the path. This is the documented way to get everything: serialize the shape yourself
        /// and return the string, rather than fighting the collection caps.
        /// </summary>
        private const int MaxInlineStringChars = 2048;

        /// <summary>Drops the cached options so the next run rebuilds them. Both caches die with the domain anyway.</summary>
        public static void InvalidateCache()
        {
            _baseOptions = null;
            _loader = null;
        }

        public static async Task<RunResult> RunAsync(string code, IEnumerable<string> extraUsings)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new RunResult { Status = UrcProtocol.Status.Failed, Summary = "empty snippet." };

            var options = GetBaseOptions();
            if (extraUsings != null)
            {
                var extra = extraUsings.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
                if (extra.Length > 0) options = options.AddImports(extra);
            }

            var attempt = await TryRun(code, options);
            if (attempt.Diagnostics == null) return attempt.Result;

            // One retry with an auto-resolved using. This exists purely to save a round trip: a
            // missing import is the single most common snippet failure, and an agent that has to be
            // told about it pays a full conversation turn to add one line.
            var resolved = ResolveMissingUsings(attempt.Diagnostics, out var ambiguous);
            if (ambiguous != null)
            {
                var result = Failure(attempt.Diagnostics);
                result.Summary += "\n" + ambiguous;
                return result;
            }

            if (resolved.Count == 0) return Failure(attempt.Diagnostics);

            var retry = await TryRun(code, options.AddImports(resolved));
            if (retry.Diagnostics != null) return Failure(retry.Diagnostics);

            retry.Result.AutoUsings = resolved;
            return retry.Result;
        }

        private struct Attempt
        {
            public RunResult Result;
            /// <summary>Non-null only when compilation failed.</summary>
            public List<Diagnostic> Diagnostics;
        }

        private static async Task<Attempt> TryRun(string code, ScriptOptions options)
        {
            Script<object> script;
            try
            {
                script = CSharpScript.Create<object>(code, options, globalsType: null,
                    assemblyLoader: GetAssemblyLoader());
            }
            catch (Exception ex)
            {
                return new Attempt
                {
                    Result = new RunResult
                    {
                        Status = UrcProtocol.Status.Failed,
                        Summary = "could not create the script: " + ex.Message,
                    }
                };
            }

            var errors = script.Compile()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (errors.Count > 0) return new Attempt { Diagnostics = errors };

            // Every successful compile loads one assembly that stays resident until the next domain
            // reload — Mono cannot unload an individual assembly, and Unity has one AppDomain.
            UrcEditorState.ReportSnippetLoaded();

            try
            {
                var state = await script.RunAsync(catchException: null);

                // A large top-level string spills in FULL to an artifact rather than being clipped:
                // it is the documented escape hatch for "give me everything", so silently truncating
                // it would close the only door out of the caps.
                if (state.ReturnValue is string text && text.Length > MaxInlineStringChars)
                {
                    var artifact = WriteArtifact(text);
                    return new Attempt
                    {
                        Result = new RunResult
                        {
                            Status = UrcProtocol.Status.Ok,
                            Value = Json.String(text.Substring(0, MaxInlineStringChars) +
                                                $"… <{text.Length} chars — full text in the artifact>"),
                            Truncated = true,
                            ValueArtifact = artifact,
                        }
                    };
                }

                var projected = UrcBoundedJson.Project(state.ReturnValue);

                return new Attempt
                {
                    Result = new RunResult
                    {
                        Status = UrcProtocol.Status.Ok,
                        Value = projected.Value,
                        Truncated = projected.Truncated,
                    }
                };
            }
            catch (Exception ex)
            {
                // One line inline; the full stack goes to the session log, where an agent fetches it
                // only if the summary is not already enough.
                var inner = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException
                    : ex;

                // NOT $"{inner}" — Exception.ToString() embeds the full stack in the MESSAGE, which
                // sails straight past the trimming applied to Unity's own stack field and puts ~25
                // frames of Roslyn submission machinery back into the log. Trim it here instead.
                Debug.LogError($"[urc] snippet threw: {inner.GetType().Name}: {inner.Message}\n" +
                               UrcLog.TrimStack(inner.StackTrace));

                return new Attempt
                {
                    Result = new RunResult
                    {
                        Status = UrcProtocol.Status.Failed,
                        Summary = $"{inner.GetType().Name}: {inner.Message}",
                    }
                };
            }
        }

        /// <summary>
        /// Writes an oversized value to the user-global artifact directory, never into the project —
        /// keeping the zero-footprint guarantee and surviving `git clean -xdf`.
        /// </summary>
        private static string WriteArtifact(string content)
        {
            try
            {
                var dir = UrcPaths.ArtifactDir(UrcEditorState.ProjectPath);
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir,
                    $"exec-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{UrcPaths.StableHash(content).Substring(0, 6)}.txt");

                File.WriteAllText(path, content);
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[urc] could not write the result artifact: {e.Message}");
                return null;
            }
        }

        private static RunResult Failure(IEnumerable<Diagnostic> diagnostics)
        {
            var lines = diagnostics
                .Select(d => $"({d.Location.GetLineSpan().StartLinePosition.Line + 1}) {d.Id}: {d.GetMessage()}")
                .Take(10)
                .ToArray();

            return new RunResult
            {
                Status = UrcProtocol.Status.Failed,
                Summary = "compile failed:\n  " + string.Join("\n  ", lines),
            };
        }

        private static readonly Regex MissingName = new Regex(
            @"The type or namespace name '(?<name>\w+)'|The name '(?<name>\w+)' does not exist",
            RegexOptions.Compiled);

        /// <summary>
        /// Finds namespaces for unresolved names. Only an UNAMBIGUOUS match is applied: if two
        /// namespaces both offer the type, guessing would silently pick the wrong one, so the caller
        /// is told the candidates instead.
        /// </summary>
        private static List<string> ResolveMissingUsings(List<Diagnostic> diagnostics, out string ambiguous)
        {
            ambiguous = null;
            var resolved = new List<string>();

            foreach (var name in diagnostics
                         .Select(d => MissingName.Match(d.GetMessage()))
                         .Where(m => m.Success)
                         .Select(m => m.Groups["name"].Value)
                         .Distinct())
            {
                var namespaces = NamespacesFor(name);
                if (namespaces.Count == 1)
                {
                    if (!resolved.Contains(namespaces[0])) resolved.Add(namespaces[0]);
                }
                else if (namespaces.Count > 1)
                {
                    ambiguous = $"'{name}' is ambiguous — add one of: " +
                                string.Join(", ", namespaces.Select(n => $"using {n};"));
                    return resolved;
                }
            }

            return resolved;
        }

        private static List<string> NamespacesFor(string typeName)
        {
            var namespaces = new List<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }

                foreach (var type in types)
                {
                    if (!type.IsPublic || type.Name != typeName) continue;
                    if (string.IsNullOrEmpty(type.Namespace)) continue;
                    if (!namespaces.Contains(type.Namespace)) namespaces.Add(type.Namespace);
                }
            }

            return namespaces;
        }

        /// <summary>
        /// Unity's editor Mono ships a System.Runtime.Loader facade (the unityjit BCL profile), so
        /// Roslyn's runtime probe finds AssemblyLoadContext and selects its CoreCLR in-memory loader —
        /// which in these netstandard-built scripting DLLs is a stub whose LoadFromStream throws
        /// NotImplementedException. This affects EVERY Roslyn version (verified on 2.10-beta2 and
        /// 4.9.2), so the swap to Roslyn's own DesktopAssemblyLoaderImpl (Assembly.Load(byte[]), fully
        /// supported by Mono) is mandatory, not an optimization.
        ///
        /// It no-ops where Roslyn already chose the desktop loader, and warns loudly if the internals
        /// it reflects over ever change.
        /// </summary>
        private static InteractiveAssemblyLoader GetAssemblyLoader()
        {
            if (_loader != null) return _loader;

            var loader = new InteractiveAssemblyLoader();
            try
            {
                FieldInfo implField = null;
                foreach (var field in typeof(InteractiveAssemblyLoader)
                             .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (field.FieldType.Name.Contains("AssemblyLoaderImpl")) { implField = field; break; }
                }

                var desktopType = typeof(InteractiveAssemblyLoader).Assembly
                    .GetType("Microsoft.CodeAnalysis.Scripting.Hosting.DesktopAssemblyLoaderImpl");

                if (implField == null || desktopType == null)
                {
                    Debug.LogWarning(
                        "[urc] Could not locate Roslyn's desktop assembly loader via reflection (a Roslyn " +
                        "update may have changed its internals). If exec fails with NotImplementedException, " +
                        "revisit UrcCodeRunner.GetAssemblyLoader.");
                }
                else
                {
                    var current = implField.GetValue(loader);
                    if (current == null || current.GetType() != desktopType)
                    {
                        (current as IDisposable)?.Dispose();
                        implField.SetValue(loader, Activator.CreateInstance(
                            desktopType,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new object[] { loader }, null));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[urc] Failed to install Roslyn desktop assembly loader: {e.Message}");
            }

            _loader = loader;
            return _loader;
        }

        private static ScriptOptions GetBaseOptions()
        {
            if (_baseOptions != null) return _baseOptions;

            var scriptAssemblies = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies"));

            var options = ScriptOptions.Default;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;

                    var location = Path.GetFullPath(assembly.Location);

                    if (location.StartsWith(scriptAssemblies, StringComparison.OrdinalIgnoreCase))
                    {
                        // Referencing a ScriptAssemblies dll BY FILE keeps a memory-mapped handle open
                        // inside the editor, which makes Unity's script-compilation pipeline fail to
                        // overwrite it ("Copying the file failed") and then SILENTLY BLOCKS DOMAIN
                        // RELOADS until a GC happens to release Roslyn's weakly-cached metadata.
                        // In-memory images avoid the handle entirely. ~50MB once per domain — the
                        // right trade against a reload that mysteriously never happens.
                        options = options.AddReferences(MetadataReference.CreateFromImage(
                            ImmutableArray.Create(File.ReadAllBytes(location)), filePath: location));
                    }
                    else
                    {
                        // Engine and package DLLs are never rewritten mid-session, so a file-backed
                        // reference is safe here and avoids loading their bytes.
                        options = options.AddReferences(assembly);
                    }
                }
                catch (Exception)
                {
                    // Some assemblies cannot be referenced (unreadable, odd locations). Skip them.
                }
            }

            _baseOptions = options.AddImports(DefaultUsings);
            return _baseOptions;
        }
    }
}
