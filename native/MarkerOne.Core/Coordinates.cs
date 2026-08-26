using System;
using System.Globalization;

namespace MarkerOne.Core
{
    /// <summary>
    /// Reading a coordinate pair out of whatever somebody pasted.
    ///
    /// This lives here rather than beside the field it fills because it is the
    /// kind of thing that is wrong in ways nobody notices: a comma taken for a
    /// decimal separator, a pair silently swapped, a latitude of 91 clamped to
    /// 90 and turned into somewhere real. All three produce a placement in the
    /// wrong country and none of them produces an error.
    /// </summary>
    public static class Coordinates
    {
        /// <summary>
        /// Accepts what Google Maps copies — "-33.924900, 18.424100" — and the
        /// variations that arrive from copying it out of other places: a space
        /// instead of a comma, both, or extra whitespace at either end.
        ///
        /// Refuses rather than repairs. A latitude outside ±90 is a typo or a
        /// swapped pair, and swapping it back for the user would be a guess
        /// about intent, dressed as helpfulness, on the one input where being
        /// wrong is invisible until somebody drives to the wrong place.
        ///
        /// Parsed with the invariant culture, so a phone set to a locale that
        /// writes 33,92 does not read "-33,92 18,42" as four numbers.
        /// </summary>
        public static bool TryParse(string typed, out double lat, out double lon)
        {
            lat = 0;
            lon = 0;

            if (string.IsNullOrEmpty(typed)) { return false; }

            string[] parts = typed.Replace(',', ' ')
                                  .Split(new[] { ' ', '\t', '\n', '\r' },
                                         StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) { return false; }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out lat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out lon))
            {
                lat = 0;
                lon = 0;
                return false;
            }

            if (double.IsNaN(lat) || double.IsNaN(lon) ||
                lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                lat = 0;
                lon = 0;
                return false;
            }

            return true;
        }
    }
}
