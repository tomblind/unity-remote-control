using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sunblink.Urc.Protocol
{
    /// <summary>
    /// Minimal dependency-free JSON model, parser and writer.
    ///
    /// Three constraints shape this file; none are negotiable:
    ///
    /// 1. REFLECTION-FREE. The CLI is published with NativeAOT, where System.Text.Json's
    ///    reflection path does not work (it needs source-generated contexts), and Unity's Mono
    ///    ships no System.Text.Json at all. A hand-rolled model sidesteps both.
    /// 2. NO DEPENDENCIES. The Unity package must not require Newtonsoft — a project may ship its
    ///    own copy at a different version, and Unity bundles a second one under Plastic.
    /// 3. C# 9 / netstandard2.1. Unity 2021.3 compiles this file as source, so no file-scoped
    ///    namespaces, no `required`, no primary constructors.
    ///
    /// Objects preserve insertion order (a list of pairs, not a dictionary) so wire output is
    /// deterministic and readable in logs. Lookup is linear, which is correct here: protocol
    /// frames have on the order of ten keys.
    /// </summary>
    public sealed class Json
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }

        public Kind ValueKind { get; }

        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<Json> _array;
        private readonly List<KeyValuePair<string, Json>> _object;

        public static readonly Json Null = new Json();
        public static readonly Json True = new Json(true);
        public static readonly Json False = new Json(false);

        private Json() { ValueKind = Kind.Null; }
        private Json(bool value) { ValueKind = Kind.Bool; _bool = value; }
        private Json(double value) { ValueKind = Kind.Number; _number = value; }
        private Json(string value) { ValueKind = Kind.String; _string = value; }
        private Json(List<Json> value) { ValueKind = Kind.Array; _array = value; }
        private Json(List<KeyValuePair<string, Json>> value) { ValueKind = Kind.Object; _object = value; }

        // ---- construction -------------------------------------------------------------------

        public static Json Object() => new Json(new List<KeyValuePair<string, Json>>());
        public static Json Array() => new Json(new List<Json>());
        public static Json Bool(bool v) => v ? True : False;
        public static Json Number(double v) => new Json(v);
        public static Json String(string v) => v == null ? Null : new Json(v);

        public static implicit operator Json(string v) => String(v);
        public static implicit operator Json(bool v) => Bool(v);
        public static implicit operator Json(int v) => new Json(v);
        public static implicit operator Json(long v) => new Json(v);
        public static implicit operator Json(double v) => new Json(v);

        /// <summary>Adds or replaces a key. Returns this, so frames can be built fluently.</summary>
        public Json Set(string key, Json value)
        {
            RequireKind(Kind.Object, nameof(Set));
            if (key == null) throw new ArgumentNullException(nameof(key));
            value = value ?? Null;
            for (var i = 0; i < _object.Count; i++)
            {
                if (_object[i].Key == key)
                {
                    _object[i] = new KeyValuePair<string, Json>(key, value);
                    return this;
                }
            }
            _object.Add(new KeyValuePair<string, Json>(key, value));
            return this;
        }

        /// <summary>Sets the key only when the value is non-null — keeps optional fields off the wire.</summary>
        public Json SetIf(string key, Json value) => value == null || value.ValueKind == Kind.Null ? this : Set(key, value);

        public Json Add(Json value)
        {
            RequireKind(Kind.Array, nameof(Add));
            _array.Add(value ?? Null);
            return this;
        }

        // ---- access -------------------------------------------------------------------------

        /// <summary>Missing keys yield Null rather than throwing, so readers can chain safely.</summary>
        public Json this[string key]
        {
            get
            {
                if (ValueKind != Kind.Object || key == null) return Null;
                for (var i = 0; i < _object.Count; i++)
                {
                    if (_object[i].Key == key) return _object[i].Value;
                }
                return Null;
            }
        }

        public Json this[int index] =>
            ValueKind == Kind.Array && index >= 0 && index < _array.Count ? _array[index] : Null;

        public int Count =>
            ValueKind == Kind.Array ? _array.Count :
            ValueKind == Kind.Object ? _object.Count : 0;

        public bool IsNull => ValueKind == Kind.Null;
        public bool Has(string key) => ValueKind == Kind.Object && !this[key].IsNull;

        public IEnumerable<Json> Items
        {
            get
            {
                if (ValueKind != Kind.Array) yield break;
                for (var i = 0; i < _array.Count; i++) yield return _array[i];
            }
        }

        public IEnumerable<KeyValuePair<string, Json>> Fields
        {
            get
            {
                if (ValueKind != Kind.Object) yield break;
                for (var i = 0; i < _object.Count; i++) yield return _object[i];
            }
        }

        /// <summary>
        /// Numbers render unquoted, matching Newtonsoft's JValue.ToString(), so a value sent as the
        /// JSON string "300" still round-trips through int.TryParse without stray quotes.
        /// </summary>
        public string AsString(string fallback = null)
        {
            switch (ValueKind)
            {
                case Kind.String: return _string;
                case Kind.Number: return FormatNumber(_number);
                case Kind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public bool AsBool(bool fallback = false)
        {
            if (ValueKind == Kind.Bool) return _bool;
            if (ValueKind == Kind.Number) return _number != 0;
            if (ValueKind == Kind.String) return bool.TryParse(_string, out var b) ? b : fallback;
            return fallback;
        }

        public double AsDouble(double fallback = 0)
        {
            if (ValueKind == Kind.Number) return _number;
            if (ValueKind == Kind.String &&
                double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            var d = AsDouble(double.NaN);
            return double.IsNaN(d) || d < int.MinValue || d > int.MaxValue ? fallback : (int)d;
        }

        public long AsLong(long fallback = 0)
        {
            var d = AsDouble(double.NaN);
            return double.IsNaN(d) || d < long.MinValue || d > long.MaxValue ? fallback : (long)d;
        }

        private void RequireKind(Kind kind, string op)
        {
            if (ValueKind != kind)
                throw new InvalidOperationException($"{op} requires a JSON {kind}, but this value is {ValueKind}.");
        }

        // ---- writing ------------------------------------------------------------------------

        public override string ToString()
        {
            var sb = new StringBuilder(256);
            Write(sb);
            return sb.ToString();
        }

        public void Write(StringBuilder sb)
        {
            switch (ValueKind)
            {
                case Kind.Null: sb.Append("null"); break;
                case Kind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case Kind.Number: sb.Append(FormatNumber(_number)); break;
                case Kind.String: WriteString(sb, _string); break;
                case Kind.Array:
                    sb.Append('[');
                    for (var i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case Kind.Object:
                    sb.Append('{');
                    for (var i = 0; i < _object.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _object[i].Key);
                        sb.Append(':');
                        _object[i].Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        /// <summary>
        /// Integral values are written without a decimal point so ids and counts stay ids and counts.
        /// Non-finite values become null — JSON has no NaN/Infinity, and emitting the bare token
        /// would produce a document no conforming parser accepts.
        /// </summary>
        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            if (value == Math.Floor(value) && value >= long.MinValue && value <= long.MaxValue)
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters must be escaped. Everything else — including surrogate
                        // pairs — passes through as UTF-16; the transport is UTF-8 encoded later.
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- parsing ------------------------------------------------------------------------

        public static Json Parse(string text)
        {
            if (text == null) throw new JsonException("Cannot parse null.");
            var pos = 0;
            var result = ParseValue(text, ref pos, 0);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new JsonException($"Trailing content at offset {pos}.");
            return result;
        }

        /// <summary>
        /// Both sides treat an unparseable frame as transient rather than fatal — a torn read or a
        /// stray datagram must not take down a connection.
        /// </summary>
        public static bool TryParse(string text, out Json value)
        {
            try { value = Parse(text); return true; }
            catch (JsonException) { value = Null; return false; }
        }

        // Bounded so a hostile or corrupt document cannot exhaust the stack. Protocol frames nest
        // no more than a handful deep; exec return values are already depth-capped before they get here.
        private const int MaxDepth = 64;

        private static Json ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new JsonException($"Nesting deeper than {MaxDepth} at offset {pos}.");
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new JsonException("Unexpected end of input.");

            var c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depth);
                case '[': return ParseArray(s, ref pos, depth);
                case '"': return String(ParseString(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return True;
                case 'f': Expect(s, ref pos, "false"); return False;
                case 'n': Expect(s, ref pos, "null"); return Null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static Json ParseObject(string s, ref int pos, int depth)
        {
            pos++; // '{'
            var result = Object();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new JsonException($"Expected a key at offset {pos}.");
                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new JsonException($"Expected ':' at offset {pos}.");
                pos++;
                result.Set(key, ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonException("Unterminated object.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return result; }
                throw new JsonException($"Expected ',' or '}}' at offset {pos}.");
            }
        }

        private static Json ParseArray(string s, ref int pos, int depth)
        {
            pos++; // '['
            var result = Array();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonException("Unterminated array.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return result; }
                throw new JsonException($"Expected ',' or ']' at offset {pos}.");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new JsonException("Unterminated string.");
                var c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) throw new JsonException("Unterminated escape.");
                var esc = s[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new JsonException("Truncated \\u escape.");
                        var hex = s.Substring(pos, 4);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw new JsonException($"Bad \\u escape '{hex}' at offset {pos}.");
                        pos += 4;
                        sb.Append((char)code);
                        break;
                    default: throw new JsonException($"Unknown escape '\\{esc}' at offset {pos - 1}.");
                }
            }
        }

        private static Json ParseNumber(string s, ref int pos)
        {
            var start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length)
            {
                var c = s[pos];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') pos++;
                else break;
            }
            var text = s.Substring(start, pos - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new JsonException($"Invalid number '{text}' at offset {start}.");
            return new Json(value);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new JsonException($"Expected '{literal}' at offset {pos}.");
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }
}
