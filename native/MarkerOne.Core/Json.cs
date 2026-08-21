using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MarkerOne.Core
{
    /// <summary>
    /// A small JSON reader and writer.
    ///
    /// System.Text.Json is not in netstandard2.1, and pulling it in as a
    /// package would put a dependency into a Unity library whose entire point
    /// is not having any. Firestore's wire format is simple — typed scalars in
    /// nested objects — and this is enough for it.
    ///
    /// It is checked against System.Text.Json in the conformance run rather
    /// than against its author's confidence.
    /// </summary>
    public sealed class Json
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }

        public Kind Type { get; private set; }
        private bool _bool;
        private double _number;
        private string _string;
        private List<Json> _array;
        private Dictionary<string, Json> _object;

        public static readonly Json Null = new Json { Type = Kind.Null };

        // ── reading ──────────────────────────────────────────────

        public bool AsBool => Type == Kind.Bool && _bool;

        public double AsNumber => Type == Kind.Number ? _number : 0;

        public string AsString => Type == Kind.String ? _string : null;

        public int Count => Type == Kind.Array ? _array.Count
            : Type == Kind.Object ? _object.Count : 0;

        public IEnumerable<Json> Items =>
            Type == Kind.Array ? (IEnumerable<Json>)_array : Array.Empty<Json>();

        public IEnumerable<KeyValuePair<string, Json>> Fields =>
            Type == Kind.Object
                ? (IEnumerable<KeyValuePair<string, Json>>)_object
                : Array.Empty<KeyValuePair<string, Json>>();

        /// <summary>Missing keys and wrong types give Null rather than throwing.
        /// Firestore omits fields freely, and a chain of TryGetProperty for
        /// every read is how the web version got unreadable.</summary>
        public Json this[string key] =>
            Type == Kind.Object && _object.TryGetValue(key, out Json v) ? v : Null;

        public Json this[int index] =>
            Type == Kind.Array && index >= 0 && index < _array.Count ? _array[index] : Null;

        public bool Has(string key) => Type == Kind.Object && _object.ContainsKey(key);

        // ── building ─────────────────────────────────────────────

        public static Json Object() =>
            new Json { Type = Kind.Object, _object = new Dictionary<string, Json>() };

        public static Json Array_() => new Json { Type = Kind.Array, _array = new List<Json>() };

        public static Json Of(string value) =>
            value == null ? Null : new Json { Type = Kind.String, _string = value };

        public static Json Of(double value) => new Json { Type = Kind.Number, _number = value };

        public static Json Of(bool value) => new Json { Type = Kind.Bool, _bool = value };

        public Json Set(string key, Json value)
        {
            if (Type != Kind.Object) { throw new InvalidOperationException("not an object"); }
            _object[key] = value ?? Null;
            return this;
        }

        public Json Set(string key, string value) => Set(key, Of(value));
        public Json Set(string key, double value) => Set(key, Of(value));

        public Json Add(Json value)
        {
            if (Type != Kind.Array) { throw new InvalidOperationException("not an array"); }
            _array.Add(value ?? Null);
            return this;
        }

        // ── parsing ──────────────────────────────────────────────

        public static Json Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) { return Null; }

            int at = 0;
            Json value = ParseValue(text, ref at);
            SkipWhitespace(text, ref at);
            if (at < text.Length)
            {
                throw new FormatException($"trailing characters at {at}");
            }
            return value;
        }

        private static Json ParseValue(string s, ref int at)
        {
            SkipWhitespace(s, ref at);
            if (at >= s.Length) { throw new FormatException("unexpected end of input"); }

            switch (s[at])
            {
                case '{': return ParseObject(s, ref at);
                case '[': return ParseArray(s, ref at);
                case '"': return Of(ParseString(s, ref at));
                case 't': Expect(s, ref at, "true"); return Of(true);
                case 'f': Expect(s, ref at, "false"); return Of(false);
                case 'n': Expect(s, ref at, "null"); return Null;
                default: return Of(ParseNumber(s, ref at));
            }
        }

        private static Json ParseObject(string s, ref int at)
        {
            Json result = Object();
            at++;                                       // {
            SkipWhitespace(s, ref at);

            if (at < s.Length && s[at] == '}') { at++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref at);
                string key = ParseString(s, ref at);
                SkipWhitespace(s, ref at);

                if (at >= s.Length || s[at] != ':') { throw new FormatException($"expected ':' at {at}"); }
                at++;

                result.Set(key, ParseValue(s, ref at));
                SkipWhitespace(s, ref at);

                if (at >= s.Length) { throw new FormatException("unterminated object"); }
                if (s[at] == ',') { at++; continue; }
                if (s[at] == '}') { at++; return result; }
                throw new FormatException($"expected ',' or '}}' at {at}");
            }
        }

        private static Json ParseArray(string s, ref int at)
        {
            Json result = Array_();
            at++;                                       // [
            SkipWhitespace(s, ref at);

            if (at < s.Length && s[at] == ']') { at++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref at));
                SkipWhitespace(s, ref at);

                if (at >= s.Length) { throw new FormatException("unterminated array"); }
                if (s[at] == ',') { at++; continue; }
                if (s[at] == ']') { at++; return result; }
                throw new FormatException($"expected ',' or ']' at {at}");
            }
        }

        private static string ParseString(string s, ref int at)
        {
            if (at >= s.Length || s[at] != '"') { throw new FormatException($"expected string at {at}"); }
            at++;

            var sb = new StringBuilder();
            while (at < s.Length)
            {
                char c = s[at++];
                if (c == '"') { return sb.ToString(); }

                if (c != '\\') { sb.Append(c); continue; }

                if (at >= s.Length) { break; }
                char escape = s[at++];
                switch (escape)
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
                        if (at + 4 > s.Length) { throw new FormatException("truncated \\u escape"); }
                        sb.Append((char)Convert.ToInt32(s.Substring(at, 4), 16));
                        at += 4;
                        break;
                    default: throw new FormatException($"unknown escape \\{escape}");
                }
            }

            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int at)
        {
            int start = at;
            if (at < s.Length && (s[at] == '-' || s[at] == '+')) { at++; }
            while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '.' ||
                                     s[at] == 'e' || s[at] == 'E' ||
                                     s[at] == '-' || s[at] == '+'))
            {
                at++;
            }

            string raw = s.Substring(start, at - start);
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException($"bad number '{raw}' at {start}");
            }
            return value;
        }

        private static void Expect(string s, ref int at, string literal)
        {
            if (at + literal.Length > s.Length ||
                string.CompareOrdinal(s, at, literal, 0, literal.Length) != 0)
            {
                throw new FormatException($"expected '{literal}' at {at}");
            }
            at += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int at)
        {
            while (at < s.Length && (s[at] == ' ' || s[at] == '\t' || s[at] == '\n' || s[at] == '\r'))
            {
                at++;
            }
        }

        // ── writing ──────────────────────────────────────────────

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Type)
            {
                case Kind.Null: sb.Append("null"); break;
                case Kind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case Kind.Number: sb.Append(Number(_number)); break;
                case Kind.String: Escape(sb, _string); break;

                case Kind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) { sb.Append(','); }
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;

                case Kind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, Json> field in _object)
                    {
                        if (!first) { sb.Append(','); }
                        first = false;
                        Escape(sb, field.Key);
                        sb.Append(':');
                        field.Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        /// <summary>"R" round-trips, which matters: these are coordinates, and
        /// a latitude truncated at the seventh decimal has moved by a
        /// centimetre.</summary>
        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) { return "0"; }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Escape(StringBuilder sb, string raw)
        {
            sb.Append('"');
            foreach (char c in raw ?? "")
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
                        if (c < ' ') { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                        else { sb.Append(c); }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
