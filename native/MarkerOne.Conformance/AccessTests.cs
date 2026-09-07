using System;
using System.Collections.Generic;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    /// <summary>
    /// The one part of the survey tool that can be proved on a build agent.
    ///
    /// Everything visible about it — the crosshair, the ghost circle on the
    /// floor, the two taps that make a measurement — needs a room and a phone.
    /// The arithmetic and the wording do not, and they are what a surveyor
    /// actually reads. A verdict that says "20 mm over" when it is 20 mm under
    /// is worse than a crash.
    /// </summary>
    internal static class AccessTests
    {
        internal static void Run(Action<string, bool, string> check)
        {
            var door = new AccessCheck
            {
                Id = "door", Name = "Door", How = Measured.Between,
                AtLeastM = 0.800, Cite = "test"
            };

            var band = new AccessCheck
            {
                Id = "rail", Name = "Rail", How = Measured.Height,
                AtLeastM = 0.700, AtMostM = 0.800, Cite = "test"
            };

            var ceiling = new AccessCheck
            {
                Id = "basin", Name = "Basin", How = Measured.Height,
                AtMostM = 0.800, Cite = "test"
            };

            // A minimum, on both sides of it and exactly on it.
            Verdict wide = Access.Judge(door, 0.850);
            check("access: over a minimum passes", wide.Meets, wide.Says);

            Verdict exact = Access.Judge(door, 0.800);
            check("access: exactly the minimum passes", exact.Meets, exact.Says);

            Verdict narrow = Access.Judge(door, 0.780);
            check("access: under a minimum fails", !narrow.Meets, narrow.Says);
            check("access: shortfall is the difference",
                  Near(narrow.OutByM, 0.020), narrow.OutByM.ToString());

            // The wording is the product. A surveyor reads this sentence and
            // writes it into a report, so it says which way and by how much.
            check("access: says how far under",
                  narrow.Says == "780 mm — 20 mm under the 800 mm cited", narrow.Says);

            // A band, outside it in both directions.
            Verdict low = Access.Judge(band, 0.650);
            check("access: below a band fails", !low.Meets, low.Says);
            check("access: below a band says under",
                  low.Says.Contains("under"), low.Says);

            Verdict high = Access.Judge(band, 0.850);
            check("access: above a band fails", !high.Meets, high.Says);
            check("access: above a band says over",
                  high.Says.Contains("over"), high.Says);
            check("access: overshoot is the difference",
                  Near(high.OutByM, 0.050), high.OutByM.ToString());

            Verdict inside = Access.Judge(band, 0.750);
            check("access: inside a band passes", inside.Meets, inside.Says);
            check("access: a pass still reports the measurement",
                  inside.Says.StartsWith("750 mm"), inside.Says);

            // A maximum with no minimum. Zero is not sensible in a room but it
            // is not a failure of this check, and inventing a floor for it
            // would fail a basin for being too low.
            Verdict lowBasin = Access.Judge(ceiling, 0.100);
            check("access: a maximum has no floor", lowBasin.Meets, lowBasin.Says);

            Verdict tallBasin = Access.Judge(ceiling, 0.900);
            check("access: over a maximum fails", !tallBasin.Meets, tallBasin.Says);

            // Nothing measured is not the same as measured zero, which would
            // fail every minimum and pass every maximum.
            Verdict none = Access.Judge(door, double.NaN);
            check("access: unmeasured is not a pass", !none.Meets, none.Says);
            check("access: unmeasured says so", none.Says == "not measured", none.Says);

            // Millimetres, rounded, because a survey written in metres to five
            // places is a survey nobody reads.
            check("access: 1.5 m is 1500 mm", Access.Mm(1.5) == "1500 mm", Access.Mm(1.5));
            check("access: rounds to the millimetre",
                  Access.Mm(0.8004) == "800 mm", Access.Mm(0.8004));

            check("access: a band reads as a band",
                  Access.Range(band) == "700 mm–800 mm", Access.Range(band));
            check("access: a minimum reads as one",
                  Access.Range(door) == "at least 800 mm", Access.Range(door));
            check("access: a maximum reads as one",
                  Access.Range(ceiling) == "at most 800 mm", Access.Range(ceiling));

            // The shipped set. Not the figures — those are a draft nobody has
            // checked — but that each one is answerable: a check with no bound
            // at all can never be judged, and one with no citation is an
            // opinion with a number in it.
            IReadOnlyList<AccessCheck> toilet = Standards.Toilet();
            check("access: the toilet set is not empty", toilet.Count > 0, toilet.Count.ToString());

            var ids = new HashSet<string>();
            foreach (AccessCheck one in toilet)
            {
                check("access: " + one.Id + " has a bound",
                      one.AtLeastM.HasValue || one.AtMostM.HasValue, one.Name);

                check("access: " + one.Id + " cites something",
                      !string.IsNullOrEmpty(one.Cite), one.Name);

                check("access: " + one.Id + " says what to aim at",
                      !string.IsNullOrEmpty(one.Aim), one.Name);

                check("access: " + one.Id + " is unique", ids.Add(one.Id), one.Id);

                if (one.AtLeastM.HasValue && one.AtMostM.HasValue)
                {
                    check("access: " + one.Id + " band is the right way round",
                          one.AtLeastM.Value <= one.AtMostM.Value, one.Name);
                }

                // Every figure in there is millimetres expressed in metres. A
                // stray 800 where 0.800 was meant is a door eight hundred
                // metres wide, and it would pass every test but this one.
                if (one.AtLeastM.HasValue)
                {
                    check("access: " + one.Id + " minimum is a plausible size",
                          one.AtLeastM.Value > 0 && one.AtLeastM.Value < 10,
                          one.AtLeastM.Value.ToString());
                }
                if (one.AtMostM.HasValue)
                {
                    check("access: " + one.Id + " maximum is a plausible size",
                          one.AtMostM.Value > 0 && one.AtMostM.Value < 10,
                          one.AtMostM.Value.ToString());
                }
            }

            // The draft warning has to survive being copied about, because the
            // figures will be trusted the moment it stops being visible.
            check("access: the draft is labelled as one",
                  Standards.Draft.Contains("DRAFT") || Standards.Draft.Contains("Check every one"),
                  Standards.Draft);
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
    }
}
