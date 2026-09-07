using System.Collections.Generic;

namespace MarkerOne.Core
{
    /// <summary>
    /// A starting set of checks, drawn from ISO 21542.
    ///
    /// ══ READ THIS BEFORE RELYING ON A SINGLE FIGURE BELOW ══
    ///
    /// These numbers are a working draft and are NOT verified against the
    /// published text. ISO 21542 is sold by ISO, not published openly, so these
    /// were written from secondary knowledge and every one of them needs
    /// checking against a copy of the standard before this tool is used to say
    /// anything about a real building. Several are likely to be close and
    /// wrong, which is the worst kind: a figure that is out by 50 mm produces a
    /// confident sentence about a door that passes when it fails.
    ///
    /// They are here so the mechanism can be built and tested against something
    /// shaped like the real thing. Replace them, do not trust them.
    ///
    /// Israel does not use ISO 21542 directly. The governing standard is
    /// SI 1918, incorporated by reference into the accessibility regulations
    /// under the Equal Rights for Persons with Disabilities Law, 1998. ISO is
    /// the nearer relative of the two candidates — metric, European lineage —
    /// but an inspector applies SI 1918. Swapping these figures for those is
    /// the whole of what this file is for.
    /// </summary>
    public static class Standards
    {
        public const string Draft =
            "DRAFT figures, unverified against the published standard. " +
            "Check every one before relying on it.";

        /// <summary>
        /// An accessible sanitary facility, which is the densest set of
        /// requirements in a small enough space to survey in one visit — and so
        /// the right thing to build the tool around.
        /// </summary>
        public static IReadOnlyList<AccessCheck> Toilet() => new List<AccessCheck>
        {
            new AccessCheck
            {
                Id = "door-clear",
                Name = "Door clear width",
                How = Measured.Between,
                AtLeastM = 0.800,
                Cite = "ISO 21542 — clear opening width (draft)",
                Aim = "Open the door fully. Measure the actual gap: from the " +
                      "face of the open leaf to the opposite stop, not frame to frame.",
                Remedy = "Rehang on offset hinges to recover the leaf thickness, " +
                         "or widen the opening."
            },
            new AccessCheck
            {
                Id = "turning",
                Name = "Turning space",
                How = Measured.Fits,
                AtLeastM = 1.500,
                Cite = "ISO 21542 — wheelchair turning circle (draft)",
                Aim = "A circle is drawn on the floor. Walk round it and see " +
                      "whether it is clear of the door swing, the fittings and the bin.",
                Remedy = "Usually the bin, the door swing or an outward-opening " +
                         "cubicle door rather than the room being too small."
            },
            new AccessCheck
            {
                Id = "wc-height",
                Name = "WC seat height",
                How = Measured.Height,
                AtLeastM = 0.450,
                AtMostM = 0.480,
                Cite = "ISO 21542 — seat height above finished floor (draft)",
                Aim = "The top of the seat, not the rim of the pan.",
                Remedy = "A raised seat, or a pan set on a plinth."
            },
            new AccessCheck
            {
                Id = "transfer",
                Name = "Transfer space beside the WC",
                How = Measured.Between,
                AtLeastM = 0.900,
                Cite = "ISO 21542 — clear transfer space to one side (draft)",
                Aim = "From the centreline of the pan to the nearest obstruction " +
                      "on the transfer side.",
                Remedy = "Move the pan, or take out what is in the way — this is " +
                         "the one that most often fails and most often is a bin."
            },
            new AccessCheck
            {
                Id = "grab-rail",
                Name = "Grab rail height",
                How = Measured.Height,
                AtLeastM = 0.700,
                AtMostM = 0.800,
                Cite = "ISO 21542 — horizontal grab bar above finished floor (draft)",
                Aim = "The top of the rail.",
                Remedy = "Refit at the cited height. Cheap, and usually the " +
                         "single most useful thing in the room."
            },
            new AccessCheck
            {
                Id = "basin-rim",
                Name = "Basin rim height",
                How = Measured.Height,
                AtMostM = 0.800,
                Cite = "ISO 21542 — washbasin rim above finished floor (draft)",
                Aim = "The top of the rim at its front edge.",
                Remedy = "Lower the basin, and check the knee clearance beneath " +
                         "it has not been lost to a pedestal or a trap."
            },
            new AccessCheck
            {
                Id = "controls",
                Name = "Controls and switches",
                How = Measured.Height,
                AtLeastM = 0.800,
                AtMostM = 1.100,
                Cite = "ISO 21542 — operable controls above finished floor (draft)",
                Aim = "The centre of the switch, the flush, or the dispenser.",
                Remedy = "Relocate within the band. Applies to the flush and the " +
                         "paper as much as to the light switch."
            },
            new AccessCheck
            {
                Id = "alarm-cord",
                Name = "Emergency alarm, reachable from the floor",
                How = Measured.Height,
                AtMostM = 0.100,
                Cite = "ISO 21542 — alarm reachable from the floor (draft)",
                Aim = "The bottom of the pull cord.",
                Remedy = "Lengthen the cord. It exists for somebody who has " +
                         "fallen, so a cord tied up out of the way is the same " +
                         "as no cord."
            }
        };
    }
}
