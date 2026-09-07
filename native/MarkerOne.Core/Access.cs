using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarkerOne.Core
{
    /// <summary>How a requirement is checked against a real room.</summary>
    public enum Measured
    {
        /// <summary>The distance between two points: a clear door width, a
        /// transfer space, the gap beside a fitting.</summary>
        Between,

        /// <summary>One point's height above the floor: a grab rail, a seat, a
        /// switch, a basin rim.</summary>
        Height,

        /// <summary>Nothing is measured. A shape of the required size is drawn
        /// on the floor and the surveyor sees whether it fits — which is what a
        /// turning circle actually asks, and what a tape measure answers
        /// badly.</summary>
        Fits
    }

    /// <summary>
    /// One requirement, as data rather than as code.
    ///
    /// Deliberately inert. A number in here can be read, printed, cited and
    /// corrected by somebody holding the standard, and none of that is true of
    /// a number compiled into a comparison. Which matters more than usual: the
    /// output of this tool is a sentence about a real building, and a threshold
    /// nobody can check is a sentence nobody should trust.
    /// </summary>
    public sealed class AccessCheck
    {
        public string Id;
        public string Name;
        public Measured How;

        /// <summary>Metres. Either bound may be absent — a door has a minimum
        /// width and no maximum, a switch has both.</summary>
        public double? AtLeastM;

        public double? AtMostM;

        /// <summary>Where the figure comes from, printed beside every verdict.
        /// A measurement without a citation is an opinion.</summary>
        public string Cite;

        /// <summary>What to do about it, for the case that matters — a survey
        /// that only says "no" is half a survey.</summary>
        public string Remedy = "";

        /// <summary>What the surveyor is being asked to put the points on.
        /// Ambiguity here is the largest error in the whole exercise: a door
        /// measured to its frame rather than to its open leaf is wrong by the
        /// thickness of the door and nothing in the app can tell.</summary>
        public string Aim = "";
    }

    /// <summary>What a measurement came to, and what the standard says.</summary>
    public sealed class Verdict
    {
        public AccessCheck Check;
        public double MeasuredM;
        public bool Meets;

        /// <summary>How far off, in metres, and zero when it is not off. Always
        /// positive — which side it is out on is in the sentence.</summary>
        public double OutByM;

        public string Says = "";
    }

    /// <summary>
    /// Comparing a room against a written requirement.
    ///
    /// The wording is the product here, not the boolean. "Compliant" is a legal
    /// conclusion about a whole building drawn by somebody qualified to draw
    /// it; what this can honestly say is that it measured 780 mm where the
    /// figure cited is 800 mm, and by how much those differ. So every verdict
    /// carries the measurement, the requirement and the source, and the word
    /// compliant appears nowhere.
    /// </summary>
    public static class Access
    {
        public static Verdict Judge(AccessCheck check, double measuredM)
        {
            if (check == null) { throw new ArgumentNullException(nameof(check)); }

            var verdict = new Verdict { Check = check, MeasuredM = measuredM, Meets = true };

            if (double.IsNaN(measuredM))
            {
                verdict.Meets = false;
                verdict.Says = "not measured";
                return verdict;
            }

            if (check.AtLeastM.HasValue && measuredM < check.AtLeastM.Value)
            {
                verdict.Meets = false;
                verdict.OutByM = check.AtLeastM.Value - measuredM;

                verdict.Says = string.Format(CultureInfo.InvariantCulture,
                    "{0} — {1} under the {2} cited",
                    Mm(measuredM), Mm(verdict.OutByM), Mm(check.AtLeastM.Value));

                return verdict;
            }

            if (check.AtMostM.HasValue && measuredM > check.AtMostM.Value)
            {
                verdict.Meets = false;
                verdict.OutByM = measuredM - check.AtMostM.Value;

                verdict.Says = string.Format(CultureInfo.InvariantCulture,
                    "{0} — {1} over the {2} cited",
                    Mm(measuredM), Mm(verdict.OutByM), Mm(check.AtMostM.Value));

                return verdict;
            }

            verdict.Says = string.Format(CultureInfo.InvariantCulture,
                "{0} — within {1}", Mm(measuredM), Range(check));

            return verdict;
        }

        /// <summary>Millimetres, because that is the unit every one of these
        /// standards is written in and converting in your head at the moment of
        /// reading is how a 1500 becomes a 150.</summary>
        public static string Mm(double metres) =>
            Math.Round(metres * 1000).ToString("0", CultureInfo.InvariantCulture) + " mm";

        public static string Range(AccessCheck check)
        {
            if (check.AtLeastM.HasValue && check.AtMostM.HasValue)
            {
                return Mm(check.AtLeastM.Value) + "–" + Mm(check.AtMostM.Value);
            }

            if (check.AtLeastM.HasValue) { return "at least " + Mm(check.AtLeastM.Value); }
            if (check.AtMostM.HasValue) { return "at most " + Mm(check.AtMostM.Value); }

            return "no figure";
        }
    }
}
