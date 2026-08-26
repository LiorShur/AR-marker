using System;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    /// <summary>
    /// What somebody pastes into a coordinate field.
    ///
    /// Every case here is something that actually arrives: the format Google
    /// Maps copies, the same thing after a trip through a chat app, a pair
    /// somebody typed by hand, and the handful of ways it can be wrong that
    /// look exactly like the ways it can be right.
    /// </summary>
    internal static class CoordinateTests
    {
        public static void Run(Action<string, bool, string> check)
        {
            Good(check, "-33.924900, 18.424100", -33.9249, 18.4241, "as Google Maps copies it");
            Good(check, "-33.9249,18.4241", -33.9249, 18.4241, "without the space");
            Good(check, "-33.9249 18.4241", -33.9249, 18.4241, "without the comma");
            Good(check, "  -33.9249 ,  18.4241  ", -33.9249, 18.4241, "with stray whitespace");
            Good(check, "51.5007, -0.1246", 51.5007, -0.1246, "a negative longitude");
            Good(check, "0, 0", 0, 0, "null island is a real place");
            Good(check, "-90, 180", -90, 180, "the corners are inclusive");
            Good(check, "1e1, 2e1", 10, 20, "exponent notation, since parsers accept it");
            Good(check, "18.4241, -33.9249, ", 18.4241, -33.9249, "a trailing comma");

            // Worth stating rather than testing around: a pair given the wrong
            // way round is only detectable when one of them is out of range.
            // 18.4241, -33.9249 is the Cape Town coordinate reversed and also a
            // real place in the Atlantic, so nothing here can tell them apart
            // and nothing should pretend to.
            Good(check, "18.4241, -33.9249", 18.4241, -33.9249,
                 "a reversed pair that is also somewhere real");

            Bad(check, "", "nothing at all");
            Bad(check, "-33.9249", "one number is not a pair");
            Bad(check, "-33.9249, 18.4241, 5", "three is not a pair either");
            Bad(check, "somewhere near the tree", "words");
            Bad(check, "91, 18", "a latitude past the pole");
            Bad(check, "-33.9249, 181", "a longitude past the meridian");


            // The one that would be silently wrong rather than loudly wrong: a
            // locale that writes 33,92 would split this into four numbers if it
            // were parsed with the current culture instead of the invariant one.
            Bad(check, "-33,924900 18,424100", "a comma decimal separator");
        }

        private static void Good(Action<string, bool, string> check, string typed,
                                 double lat, double lon, string what)
        {
            bool ok = Coordinates.TryParse(typed, out double gotLat, out double gotLon);
            bool right = ok && Math.Abs(gotLat - lat) < 1e-9 && Math.Abs(gotLon - lon) < 1e-9;

            check($"reads {what}", right,
                  ok ? $"{gotLat}, {gotLon}" : "refused");
        }

        private static void Bad(Action<string, bool, string> check, string typed, string what)
        {
            bool ok = Coordinates.TryParse(typed, out double lat, out double lon);
            check($"refuses {what}", !ok, ok ? $"accepted as {lat}, {lon}" : "refused");
        }
    }
}
