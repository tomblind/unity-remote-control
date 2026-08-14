using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// A caller-supplied collection of helpers, compiled into a real assembly on demand and kept
    /// resident for the rest of the domain.
    ///
    /// This exists because of one measurement: compiling a snippet costs about 225ms almost
    /// REGARDLESS OF ITS SIZE — an 81-line submission and a one-liner cost the same, because the
    /// price is creating a compilation and binding ~200 metadata references, not processing source.
    /// So moving helpers out of the snippet saves nothing on its own; what saves is not building a
    /// fresh compilation per call. A submission chained off a cached root reuses that work and
    /// costs ~34ms, a 6x improvement that no amount of source trimming could reach.
    ///
    /// Hence the two halves here, both required:
    ///   - the helpers are emitted as an ASSEMBLY, so they are ordinary C# (real classes,
    ///     namespaces, extension methods, IDE support) rather than script-flavoured source, and a
    ///     compile error in them is reported against their own file and line;
    ///   - a ROOT submission referencing it is compiled once, and every snippet becomes a
    ///     continuation of that root.
    ///
    /// Nothing is written to disk and nothing goes into Assets/, so a library needs no git exclusion
    /// and cannot break the project's build — a broken helper fails the call that named it, and the
    /// editor keeps working.
    ///
    /// Invalidation is by CONTENT HASH, not by lifecycle: edit a helper and the next call rebuilds,
    /// with no domain reload. Like the other caches this dies with the domain, which is correct —
    /// the emitted assembly cannot be unloaded, so its lifetime is the domain's whether we like it
    /// or not.
    /// </summary>
    internal static class UrcLibrary
    {
        internal sealed class Handle
        {
            public string Key;

            /// <summary>Base options plus a reference to the emitted assembly.</summary>
            public ScriptOptions Options;

            /// <summary>Compiled once. Every snippet is a continuation of this.</summary>
            public Script<object> Root;

            public int SourceCount;
            public int Bytes;
        }

        private static readonly Dictionary<string, Handle> _libraries =
            new Dictionary<string, Handle>(StringComparer.Ordinal);

        public static void InvalidateAll() => _libraries.Clear();

        /// <summary>How many distinct library versions this domain has loaded, for the editor window.</summary>
        public static int LoadedCount => _libraries.Count;

        /// <summary>
        /// Returns the resident library for these sources, building it if this exact content has not
        /// been seen. <paramref name="error"/> is set only for a genuine compile failure, which is
        /// the caller's to report — it is a fault in the library, not in the snippet that named it.
        /// </summary>
        public static bool TryGetOrBuild(List<KeyValuePair<string, string>> sources,
            ScriptOptions baseOptions, InteractiveAssemblyLoader loader,
            out Handle handle, out string error)
        {
            handle = null;
            error = null;

            if (sources == null || sources.Count == 0) return true;   // nothing asked for

            var key = KeyFor(sources);
            if (_libraries.TryGetValue(key, out handle)) return true;

            // One tree per source WITH ITS PATH SET, so diagnostics carry the file they came from.
            // This is why a library beats a combined snippet for anything sizeable: errors land on
            // "scene.cs line 12" rather than on a line number in a concatenation nobody wrote.
            var trees = sources
                .Select(s => CSharpSyntaxTree.ParseText(s.Value, path: s.Key))
                .Cast<SyntaxTree>()
                .ToArray();

            // Named per content hash: two versions of a library can be resident at once (an edit
            // mid-session leaves the old one loaded, since Mono cannot unload), and identical names
            // would make which one a snippet binds to ambiguous.
            var compilation = CSharpCompilation.Create(
                "UrcLib_" + key,
                trees,
                baseOptions.MetadataReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            byte[] image;
            using (var stream = new System.IO.MemoryStream())
            {
                var emitted = compilation.Emit(stream);
                if (!emitted.Success)
                {
                    error = Describe(emitted.Diagnostics);
                    return false;
                }
                image = stream.ToArray();
            }

            try
            {
                // Load so snippets can CALL it, reference so they can COMPILE against it. Both are
                // needed and they are separate things.
                Assembly.Load(image);

                var options = baseOptions.AddReferences(
                    MetadataReference.CreateFromImage(ImmutableArray.Create(image)));

                // An empty root: it exists purely to be something to chain from, so it carries no
                // declarations of its own. globalsType is set HERE because a chain binds globals at
                // its root — that is what keeps --arg working inside a continuation (verified).
                var root = CSharpScript.Create<object>("", options,
                    globalsType: typeof(UrcGlobals), assemblyLoader: loader);

                var rootErrors = root.Compile()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (rootErrors.Count > 0)
                {
                    error = "the library root failed to compile:\n  " + Describe(rootErrors);
                    return false;
                }

                handle = new Handle
                {
                    Key = key,
                    Options = options,
                    Root = root,
                    SourceCount = sources.Count,
                    Bytes = image.Length,
                };

                _libraries[key] = handle;
                UrcEditorState.ReportSnippetLoaded();
                return true;
            }
            catch (Exception ex)
            {
                error = $"could not load the library assembly: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Content hash over names and bodies. Names are included because moving a method between
        /// two files changes nothing about the combined text but does change which file a diagnostic
        /// points at.
        /// </summary>
        private static string KeyFor(List<KeyValuePair<string, string>> sources)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var source in sources)
                builder.Append(source.Key).Append('\u0000').Append(source.Value).Append('\u0001');

            return UrcPaths.StableHash(builder.ToString());
        }

        private static string Describe(IEnumerable<Diagnostic> diagnostics)
        {
            var lines = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var span = d.Location.GetLineSpan();
                    var file = string.IsNullOrEmpty(span.Path) ? "?" : span.Path;
                    return $"{file}({span.StartLinePosition.Line + 1}) {d.Id}: {d.GetMessage()}";
                })
                .Distinct()
                .Take(10)
                .ToArray();

            return string.Join("\n  ", lines);
        }
    }
}
