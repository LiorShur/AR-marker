using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    /// <summary>
    /// The hand-written reader in Core, checked against System.Text.Json.
    ///
    /// Core cannot use System.Text.Json — it is not in netstandard2.1, and
    /// adding the package would put a dependency into a library whose whole
    /// point is not having one. So Core has its own. A parser nobody checks is
    /// a parser that silently mangles a coordinate, so this host, which is not
    /// shipped and can reference anything, checks it against the real thing.
    /// </summary>
    internal static class JsonTests
    {
        private static readonly string[] Corpus =
        {
            "{}",
            "[]",
            "null",
            "true",
            "123",
            "-0.5",
            "1e-7",
            "\"\"",
            "{\"a\":1,\"b\":\"two\",\"c\":[1,2,3],\"d\":{\"e\":null}}",
            "[{\"x\":1},{\"x\":2}]",
            "{\"nested\":{\"deep\":{\"deeper\":{\"value\":42}}}}",
            "{\"escapes\":\"quote \\\" backslash \\\\ newline \\n tab \\t\"}",
            "{\"unicode\":\"\\u00e9\\u4e2d\"}",
            "{\"empty\":{},\"emptyArray\":[]}",
            "{\"lat\":51.5006974,\"lon\":-0.1245811,\"h\":-0.0009709084533540135}",
            "{\"big\":1.7976931348623157e308,\"small\":5e-324}",
            "{\"exp\":1.2345678901234567e-15}",
            "  {  \"spaced\"  :  [ 1 , 2 ]  }  ",
            "{\"integerAsString\":\"1\",\"realInteger\":1}"
        };

        public static void Run(Action<string, bool, string> check)
        {
            foreach (string text in Corpus)
            {
                Json mine;
                try { mine = Json.Parse(text); }
                catch (Exception e)
                {
                    check("parses " + Snip(text), false, e.Message);
                    continue;
                }

                using JsonDocument theirs = JsonDocument.Parse(text);
                var problems = new List<string>();
                Compare(mine, theirs.RootElement, "", problems);

                check("matches System.Text.Json on " + Snip(text),
                    problems.Count == 0, string.Join("; ", problems.Take(2)));
            }

            // Round trip: what the writer emits must parse back to the same
            // thing, in both parsers. Coordinates are the reason — a latitude
            // truncated at the seventh decimal has moved by a centimetre.
            foreach (string text in Corpus)
            {
                Json mine = Json.Parse(text);
                string written = mine.ToString();

                var problems = new List<string>();
                try
                {
                    using JsonDocument reread = JsonDocument.Parse(written);
                    Compare(Json.Parse(written), reread.RootElement, "", problems);
                }
                catch (Exception e) { problems.Add(e.Message); }

                check("round trips " + Snip(text), problems.Count == 0,
                    string.Join("; ", problems.Take(2)));
            }

            // Malformed input must be refused rather than half-read.
            string[] broken =
            {
                "{", "[", "{\"a\":}", "{\"a\" 1}", "\"unterminated", "{}{}", "[1,]x"
            };
            int refused = broken.Count(b =>
            {
                try { Json.Parse(b); return false; }
                catch (FormatException) { return true; }
                catch (ArgumentOutOfRangeException) { return true; }
            });
            check("refuses malformed input", refused == broken.Length,
                $"{refused}/{broken.Length}");

            // Missing keys give Null rather than throwing, which is what makes
            // reading Firestore's optional fields bearable.
            Json doc = Json.Parse("{\"a\":{\"b\":1}}");
            check("missing keys are Null, not an exception",
                doc["nope"]["deeper"]["deepest"].AsString == null &&
                Math.Abs(doc["nope"].AsNumber) < 1e-12, "");
        }

        private static void Compare(Json mine, JsonElement theirs, string path, List<string> problems)
        {
            if (problems.Count > 4) { return; }

            switch (theirs.ValueKind)
            {
                case JsonValueKind.Object:
                    if (mine.Type != Json.Kind.Object) { problems.Add($"{path}: not an object"); return; }
                    foreach (JsonProperty p in theirs.EnumerateObject())
                    {
                        if (!mine.Has(p.Name)) { problems.Add($"{path}.{p.Name} missing"); continue; }
                        Compare(mine[p.Name], p.Value, path + "." + p.Name, problems);
                    }
                    if (mine.Count != theirs.EnumerateObject().Count())
                    {
                        problems.Add($"{path}: {mine.Count} keys vs {theirs.EnumerateObject().Count()}");
                    }
                    break;

                case JsonValueKind.Array:
                    if (mine.Type != Json.Kind.Array) { problems.Add($"{path}: not an array"); return; }
                    var items = theirs.EnumerateArray().ToList();
                    if (mine.Count != items.Count)
                    {
                        problems.Add($"{path}: {mine.Count} items vs {items.Count}");
                        return;
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        Compare(mine[i], items[i], $"{path}[{i}]", problems);
                    }
                    break;

                case JsonValueKind.String:
                    if (mine.AsString != theirs.GetString())
                    {
                        problems.Add($"{path}: '{mine.AsString}' vs '{theirs.GetString()}'");
                    }
                    break;

                case JsonValueKind.Number:
                    double want = theirs.GetDouble();
                    // Relative, because the corpus spans 5e-324 to 1.8e308.
                    double tolerance = Math.Max(Math.Abs(want) * 1e-15, 1e-300);
                    if (Math.Abs(mine.AsNumber - want) > tolerance)
                    {
                        problems.Add($"{path}: {mine.AsNumber:R} vs {want:R}");
                    }
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (mine.AsBool != (theirs.ValueKind == JsonValueKind.True))
                    {
                        problems.Add($"{path}: bool mismatch");
                    }
                    break;

                case JsonValueKind.Null:
                    if (mine.Type != Json.Kind.Null) { problems.Add($"{path}: not null"); }
                    break;
            }
        }

        private static string Snip(string text) =>
            text.Length <= 34 ? text.Trim() : text.Substring(0, 31).Trim() + "…";
    }
}
