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

            // A TYPE here means `using static`, which is how Arg/ArgInt/RequireArg stay in scope
            // without a globals object. See UrcGlobals for why a globals object is not an option:
            // it makes Roslyn file-reference our own assembly and blocks package updates.
            "Urc.Editor.UrcGlobals",
        };

        private static ScriptOptions _baseOptions;
        private static InteractiveAssemblyLoader _loader;

        /// <summary>
        /// Compiled snippets, keyed on source plus the usings in effect.
        ///
        /// Reusing the Script skips both Create and Compile, and - the part that matters more than
        /// the milliseconds - skips LOADING ANOTHER ASSEMBLY. Mono cannot unload one, so before this
        /// a skill invoked fifty times left fifty assemblies resident until the next domain reload.
        /// Now it leaves one.
        ///
        /// This only works because parameters travel beside the source (see UrcGlobals): with values
        /// interpolated in, every call had distinct source and the key could never hit.
        ///
        /// Dies with the domain, like the other two caches, which is the correct invalidation.
        /// </summary>
        private static readonly Dictionary<string, Script<object>> _scripts =
            new Dictionary<string, Script<object>>(StringComparer.Ordinal);

        /// <summary>
        /// Usings that auto-resolution had to add for a given snippet, remembered so later calls skip
        /// the failed first compile entirely. Without this a snippet needing resolution would compile
        /// twice on EVERY call - fail, resolve, retry - and the cache would never be reached.
        /// </summary>
        private static readonly Dictionary<string, List<string>> _learnedUsings =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

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

        /// <summary>Drops every cache so the next run rebuilds. They all die with the domain anyway.</summary>
        public static void InvalidateCache()
        {
            _baseOptions = null;
            _loader = null;
            _scripts.Clear();
            _learnedUsings.Clear();
            UrcLibrary.InvalidateAll();
        }

        /// <summary>Base options, exposed so a library builds against the same reference set.</summary>
        internal static ScriptOptions BaseOptions => GetBaseOptions();

        /// <summary>The desktop-swapped loader, shared so a library root loads the way snippets do.</summary>
        internal static InteractiveAssemblyLoader Loader => GetAssemblyLoader();

        /// <summary>
        /// Cache key: the library in effect, then the usings, then the source. All three matter -
        /// the same text compiled with different imports, or against a different library, is a
        /// different program, and reusing a compiled script across libraries would silently call
        /// into the wrong one.
        /// </summary>
        private static string CacheKey(string code, IEnumerable<string> usings, string libraryKey) =>
            (libraryKey ?? "") + "\u0002" +
            (usings == null ? "" : string.Join("\u0001", usings.ToArray())) + "\u0000" + code;

        public static async Task<RunResult> RunAsync(string code, IEnumerable<string> extraUsings,
            Dictionary<string, string> args = null, UrcLibrary.Handle library = null)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new RunResult { Status = UrcProtocol.Status.Failed, Summary = "empty snippet." };

            // A library carries its own options: the same references plus the emitted assembly.
            var options = library != null ? library.Options : GetBaseOptions();
            var usings = new List<string>();
            if (extraUsings != null)
                usings.AddRange(extraUsings.Where(u => !string.IsNullOrWhiteSpace(u)));

            var requestedKey = CacheKey(code, usings, library?.Key);

            // If this snippet previously needed a using resolved, apply it up front rather than
            // repeating the failed compile that discovered it.
            if (_learnedUsings.TryGetValue(requestedKey, out var learned))
                usings.AddRange(learned);

            if (usings.Count > 0) options = options.AddImports(usings);

            var attempt = await TryRun(code, options, args, CacheKey(code, usings, library?.Key), library);
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

            var withResolved = new List<string>(usings);
            withResolved.AddRange(resolved);

            var retry = await TryRun(code, options.AddImports(resolved), args,
                CacheKey(code, withResolved, library?.Key), library);
            if (retry.Diagnostics != null) return Failure(retry.Diagnostics);

            // Remember what it took, so the next call goes straight to the cached compilation.
            _learnedUsings[requestedKey] = resolved;

            retry.Result.AutoUsings = resolved;
            return retry.Result;
        }

        private struct Attempt
        {
            public RunResult Result;
            /// <summary>Non-null only when compilation failed.</summary>
            public List<Diagnostic> Diagnostics;
        }

        private static async Task<Attempt> TryRun(string code, ScriptOptions options,
            Dictionary<string, string> args, string cacheKey, UrcLibrary.Handle library)
        {
            // A hit skips Create, Compile, and the assembly load — the snippet is already resident.
            if (cacheKey != null && _scripts.TryGetValue(cacheKey, out var cachedScript))
                return await Execute(cachedScript, args);

            Script<object> script;
            try
            {
                // globalsType is CONSTANT across every snippet, so it does not disturb source-hash caching
                // — which is the whole point of passing parameters this way rather than baking
                // them into the source.
                // Chained off the library's root when there is one. This is the whole performance
                // story: a continuation reuses the root's compilation instead of building a fresh
                // one, which measured 34ms against 225ms for a standalone submission — and that
                // gap is almost independent of how much source is involved.
                //
                script = library != null
                    ? library.Root.ContinueWith<object>(code, options)
                    : CSharpScript.Create<object>(code, options, assemblyLoader: GetAssemblyLoader());
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
            // Counted only on a genuine compile: a cache hit loads nothing, which is the whole
            // point, and counting it would hide that in the editor window.
            UrcEditorState.ReportSnippetLoaded();

            if (cacheKey != null) _scripts[cacheKey] = script;

            return await Execute(script, args);
        }

        /// <summary>Runs an already-compiled snippet and projects whatever it returned.</summary>
        private static async Task<Attempt> Execute(Script<object> script, Dictionary<string, string> args)
        {
            try
            {
                // Installed immediately before the run, not passed in: the parameters reach the
                // snippet through `using static UrcGlobals`, so there is no globals object.
                UrcGlobals.SetArgs(args);

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
            var loader = GetAssemblyLoader();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;

                    var location = Path.GetFullPath(assembly.Location);

                    // TELL ROSLYN THIS REFERENCE IS ALREADY LOADED, so it never resolves the
                    // identity by opening the file. Without this, running a snippet left a handle on
                    // Library/ScriptAssemblies dlls, and Unity could then no longer overwrite them:
                    // the package's own assembly failed to be replaced with "Copying the file
                    // failed", the domain silently kept running the OLD code, and only restarting
                    // the editor cleared it. Measured: a fresh editor's Urc.Editor.dll is writable
                    // from another process, and after a SINGLE exec it is locked.
                    //
                    // In-memory metadata (below) was never enough on its own — that governs how the
                    // compiler reads the reference, not how the runtime binds it.
                    loader.RegisterDependency(assembly);

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
