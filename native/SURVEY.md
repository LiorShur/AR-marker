# Surveying a room against a standard

⚠️ **The figures shipped with this are a draft and are not verified against the
published standard.** ISO 21542 is sold by ISO rather than published openly, so
they were written from secondary knowledge. Several are likely to be close and
wrong, which is the worst kind — a figure out by 50 mm produces a confident
sentence about a door that passes when it fails. They are there so the mechanism
can be built and tested against something shaped like the real thing. Check every
one against a copy of the standard before this says anything about a real
building.

**Israel does not use ISO 21542 directly.** The governing standard is SI 1918,
incorporated by reference into the accessibility regulations under the Equal
Rights for Persons with Disabilities Law, 1998. ISO is the nearer relative of the
usual two candidates — metric, European lineage — but an inspector applies
SI 1918. Swapping the figures is what `native/MarkerOne.Core/Standards.cs` exists
for.

## What it does not say

It never says compliant. That is a legal conclusion about a whole building drawn
by somebody qualified to draw it. What this says is that it measured 780 mm where
the figure cited is 800 mm, and that the difference is 20 mm — with the citation
printed beside it, so the sentence can be checked by whoever reads the report.

## Using it

**Survey** on the readout. Best in **Indoors, this session** mode: accurate for
the hour you are in the room, nothing left behind, no markers to print.

The panel walks one requirement at a time, because the failure mode of a surveyor
with a tape measure is not inaccuracy — it is the check nobody thought to make.
So the list is the product and the measuring is in service of it.

Each requirement says what to aim at, which is the largest error in the whole
exercise: a door measured to its frame rather than to its open leaf is wrong by
the thickness of the door, and nothing in the app can tell.

Three kinds of measurement:

**Between** — aim at one side, **Measure**, aim at the other, **Measure**. A
clear width, a transfer space.

**Height** — aim at it and **Measure** once. The floor is the other end, taken
from the floor plane the app is already tracking for placement.

**Fits** — nothing is measured. A circle of the required diameter is drawn on the
floor; walk round it and answer. Whether a turning circle is clear of a door
swing and a bin is a judgement, and inventing a number for it would be
dishonest.

## The overlay

The requirement is drawn where it is asked for: a bar at the cited height beside
the rail that is there, a circle of the cited diameter on the floor. This is the
part a tape measure cannot do and the reason for using AR at all — a rail 60 mm
too low is an abstraction until a translucent one is hanging where it should be.

It draws through walls and fittings on purpose. A required rail hidden behind the
wall it should be on says nothing.

## Changing the figures

`native/MarkerOne.Core/Standards.cs`. Each check carries its bounds in metres, a
citation, what to aim at, and what to do about it. They are data rather than code
so that somebody holding the standard can read, correct and cite them — none of
which is true of a number compiled into a comparison.

The logic that compares them is in `Access.cs` and is covered by 69 assertions in
the conformance suite: the arithmetic, the wording, and that every shipped check
is answerable at all. That is the half of this tool that can be proved without a
room and a phone, and it is the half a surveyor actually reads.
