using System;
using System.Collections.Generic;
using System.Globalization;

namespace Urc.Editor
{
    /// <summary>
    /// Parameters passed to a snippet, in scope inside it.
    ///
    /// Roslyn puts every member of the globals type directly in scope, so a snippet just writes
    /// `ArgInt("width", 1280)` with no using, no declaration, and no ceremony.
    ///
    /// WHY THIS EXISTS RATHER THAN STRING INTERPOLATION. Building values into the source seems
    /// simpler until you look at what it costs:
    ///
    ///   - The quoting differs per shell (PowerShell here-strings vs heredocs), and any value with
    ///     slashes or quotes has to survive two levels of escaping.
    ///   - Worse, it makes compiled-snippet caching IMPOSSIBLE, not merely ineffective: every
    ///     distinct parameter value produces distinct source, so a source-hash cache can never hit,
    ///     and each call leaks another assembly that Mono cannot unload until the next reload.
    ///
    /// With parameters out of the source, a reusable snippet is byte-identical on every invocation.
    /// That is the precondition for caching compiled snippets at all.
    ///
    /// PUBLIC by necessity: Roslyn requires the globals type to be accessible from the script's
    /// compilation. Do not narrow it.
    /// </summary>
    public sealed class UrcGlobals
    {
        private readonly Dictionary<string, string> _args;

        public UrcGlobals(Dictionary<string, string> args)
        {
            // Case-insensitive: --arg Width= and --arg width= should not be different parameters.
            _args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null) return;
            foreach (var pair in args) _args[pair.Key] = pair.Value;
        }

        /// <summary>Every parameter, for a snippet that wants to enumerate rather than ask.</summary>
        public IReadOnlyDictionary<string, string> Args => _args;

        public bool HasArg(string name) => name != null && _args.ContainsKey(name);

        public string Arg(string name, string fallback = null) =>
            name != null && _args.TryGetValue(name, out var value) ? value : fallback;

        public int ArgInt(string name, int fallback = 0) =>
            int.TryParse(Arg(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        public long ArgLong(string name, long fallback = 0) =>
            long.TryParse(Arg(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        public float ArgFloat(string name, float fallback = 0) =>
            float.TryParse(Arg(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        /// <summary>
        /// Accepts the spellings a shell actually produces — `--arg debug=true`, `=1`, `=yes`, `=on`
        /// — and treats a bare `--arg debug=` as true, since naming a flag at all signals intent.
        /// </summary>
        public bool ArgBool(string name, bool fallback = false)
        {
            var raw = Arg(name);
            if (raw == null) return fallback;
            if (raw.Length == 0) return true;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "y": case "on": return true;
                case "0": case "false": case "no": case "n": case "off": return false;
                default: return fallback;
            }
        }

        /// <summary>
        /// Fails loudly for a parameter with no sensible default. Better than a silent fallback that
        /// makes a snippet quietly do the wrong thing to the project.
        /// </summary>
        public string RequireArg(string name)
        {
            var value = Arg(name);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"this snippet requires --arg {name}=<value>");
            return value;
        }
    }
}
