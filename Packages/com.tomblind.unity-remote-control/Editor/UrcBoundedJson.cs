using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Urc.Protocol;

namespace Urc.Editor
{
    /// <summary>
    /// Projects an arbitrary returned object into bounded JSON.
    ///
    /// This exists because results persist in an agent's context for the entire session. An
    /// unbounded projection of, say, a GameObject walks into its transform, its children, their
    /// components, and back up again — tens of thousands of tokens for what the caller meant as
    /// "did it work?". Caps are not a safety net here; they are the normal path.
    ///
    /// Unity objects deliberately collapse to identity rather than being walked: their properties
    /// are mostly engine-side and reading some of them off the wrong context throws.
    /// </summary>
    internal static class UrcBoundedJson
    {
        public const int MaxDepth = 4;
        public const int MaxItems = 32;
        public const int MaxStringChars = 2048;
        public const int MaxTotalChars = 8 * 1024;

        /// <summary>True when anything was elided, so the caller can tell the user where to get the rest.</summary>
        public sealed class Result
        {
            public Json Value;
            public bool Truncated;
        }

        public static Result Project(object value)
        {
            var state = new State();
            var json = Convert(value, 0, state);

            // A top-level string is the documented escape hatch: an author who needs the full shape
            // serializes it themselves. Honour that by not double-capping what they hand back —
            // beyond the total budget, which still applies.
            var text = json.ToString();
            if (text.Length > MaxTotalChars)
            {
                json = Json.String(Clip(text, MaxTotalChars) +
                                   $"… <projection exceeded {MaxTotalChars} chars>");
                state.Truncated = true;
            }

            return new Result { Value = json, Truncated = state.Truncated };
        }

        private sealed class State
        {
            public bool Truncated;
            // Reference identity, not equality: a type with a value-equality Equals must still be
            // detected as the same *instance* when it cycles.
            public readonly HashSet<object> Seen =
                new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        private static Json Convert(object value, int depth, State state)
        {
            if (value == null) return Json.Null;

            switch (value)
            {
                case string s:
                    if (s.Length <= MaxStringChars) return Json.String(s);
                    state.Truncated = true;
                    return Json.String(Clip(s, MaxStringChars) + $"… <{s.Length} chars>");

                case bool b: return Json.Bool(b);
                case char c: return Json.String(c.ToString());

                case sbyte _: case byte _: case short _: case ushort _:
                case int _: case uint _: case long _: case ulong _:
                case float _: case double _: case decimal _:
                    return Json.Number(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

                case Enum e: return Json.String(e.ToString());
                case DateTime dt: return Json.String(dt.ToString("o", CultureInfo.InvariantCulture));
                case Guid g: return Json.String(g.ToString());
            }

            // A Task in value position is reported, never awaited: blocking on .Result here would
            // deadlock against the main thread we are running on.
            if (value is Task)
                return Json.String($"<{Describe(value.GetType())} — not awaited>");

            // Unity objects: identity only. Never walked.
            if (value is UnityEngine.Object unityObject)
            {
                var name = unityObject ? unityObject.name : "<destroyed>";
                return Json.String($"{name} <{Describe(value.GetType())}>");
            }

            if (depth >= MaxDepth)
            {
                state.Truncated = true;
                return Json.String($"<{Describe(value.GetType())} — depth {MaxDepth} reached>");
            }

            if (!state.Seen.Add(value))
                return Json.String($"<cycle: {Describe(value.GetType())}>");

            try
            {
                if (value is IDictionary dictionary) return ConvertDictionary(dictionary, depth, state);
                if (value is IEnumerable enumerable) return ConvertEnumerable(enumerable, depth, state);
                return ConvertObject(value, depth, state);
            }
            finally
            {
                state.Seen.Remove(value);
            }
        }

        private static Json ConvertDictionary(IDictionary dictionary, int depth, State state)
        {
            var result = Json.Object();
            var count = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxItems)
                {
                    state.Truncated = true;
                    result.Set("…", Json.String($"{dictionary.Count - MaxItems} more"));
                    break;
                }
                result.Set(entry.Key?.ToString() ?? "null", Convert(entry.Value, depth + 1, state));
            }

            return result;
        }

        private static Json ConvertEnumerable(IEnumerable enumerable, int depth, State state)
        {
            var result = Json.Array();
            var count = 0;

            foreach (var item in enumerable)
            {
                if (count++ >= MaxItems)
                {
                    state.Truncated = true;
                    // Say how to get everything, rather than silently stopping — a capped list that
                    // looks complete is worse than one that admits it isn't.
                    result.Add(Json.String(
                        "… capped at " + MaxItems + " items; return JsonUtility/your own serialization for the full set"));
                    break;
                }
                result.Add(Convert(item, depth + 1, state));
            }

            return result;
        }

        private static Json ConvertObject(object value, int depth, State state)
        {
            var type = value.GetType();
            var result = Json.Object();
            var count = 0;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.Instance))
            {
                if (count++ >= MaxItems) { state.Truncated = true; break; }
                try { result.Set(field.Name, Convert(field.GetValue(value), depth + 1, state)); }
                catch (Exception) { result.Set(field.Name, Json.String("<unreadable>")); }
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public |
                                                        System.Reflection.BindingFlags.Instance))
            {
                if (count++ >= MaxItems) { state.Truncated = true; break; }
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

                try { result.Set(property.Name, Convert(property.GetValue(value), depth + 1, state)); }
                catch (Exception) { result.Set(property.Name, Json.String("<threw>")); }
            }

            // A type with no readable members would otherwise project as `{}`, which tells the caller
            // nothing about what they actually got back.
            return result.Count == 0 ? Json.String($"<{Describe(type)}>") : result;
        }

        private static string Describe(Type type) => type == null ? "?" : type.Name;

        private static string Clip(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max);

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
