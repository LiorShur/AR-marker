# Indoors: venues pinned by printed markers

Geospatial does not work indoors. It wants sky and street-level imagery and a
hall has neither, so nothing in this document uses a coordinate. A venue is
pinned by printed paper instead, and everything in it is measured from the
venue's own origin.

## The idea in one paragraph

One marker defines the origin. Every other pose in the venue — including where
the other markers are — is stored relative to that. Scan any marker the venue
knows and the whole place snaps into position, because knowing where one known
thing is is enough to know where all of them are.

## Why more than one marker

A single marker holds a room. Tracking then drifts at roughly **one per cent of
the distance walked**: ten metres out is a centimetre or two, forty metres and
two corners later is half a metre and getting worse.

More markers fix that with no new mechanism. Each one re-pins the frame as you
reach it, so error resets instead of accumulating. A marker every 10–15 m and
one at each doorway is the rule of thumb, and it is the same mechanism for a
room, a hall, or a building full of both.

## Making the markers

**MarkerOne → Make venue markers** writes eight PNGs to
`Assets/MarkerOne/Markers`, builds the reference library, and opens the folder.

Print them at **18 cm across**. The size is declared in the library, and a
marker printed at a different size puts everything in the venue at the wrong
distance and the wrong scale — it will not look broken, it will look subtly
wrong, which is worse.

They are asymmetric binary noise rather than anything decorative, because
detectors work on corners: they want dense, high-contrast, non-repeating detail
and do badly on logos, skies, and anything with large flat areas. Symmetry is
the one to avoid completely — a symmetric marker does not fail, it resolves
confidently at the wrong rotation and puts the venue ninety degrees out. The
solid block in one corner is what makes which-way-up unambiguous.

Substitute your own images if you like; they must be feature-rich, matte rather
than glossy, and mounted flat.

## The organizer walks it once

1. **Venue** on the readout, type a name, **Enter**. A new name starts a venue.
2. Point at the first marker. It appears in the panel — press **Add marker-01**.
   This one defines the origin and is stored at the identity.
3. Place things: the ordinary bar, the ordinary crosshair, **Place**.
4. Walk to the next marker **without losing tracking**, keeping the phone up.
   Press **Add marker-02**. It is measured through the frame the first one
   pinned, which is why the walk matters.
5. Repeat through the building.

The order is the only fiddly part, and it is unavoidable: a marker can only be
recorded relative to a venue frame that is already pinned. Walking to a far room
and starting there produces a second origin rather than a bigger venue, and the
panel refuses it rather than silently doing it.

## Everyone else

Open the app, type the venue name once — it is remembered across launches — and
point the camera at any marker. That is the whole of it.

## Fixing the markers up

Screwed, taped flat, or framed. **If the marker moves, the venue moves**, and
that is the one genuine fragility here. It is a physical problem with a physical
fix: put them where they will not be knocked, and not on a door that opens.

## What is stored

An ordinary placement with three extra fields: `venue`, `at` (the pose in the
venue's frame) and, for the handful that are markers, `marker`. A marker is
stored the same way as everything else because it is the same thing — something
at a known pose in the venue. The only difference is that a camera can find this
one, which is what makes every other pose reachable.

Venue placements are written with a zero geopose, so nothing indoors is ever
returned by a nearby query. A hall full of party decorations has no business
appearing to somebody walking past the building.

## Building structures indoors

There is nothing to do. Everything in a venue is already a child of the venue
root, so everything is already rigid with respect to everything else — the
problem **Build on** exists to solve outdoors, where each placement carries its
own separately-corrected anchor, does not arise in here. Place the pieces where
they go and they stay in that arrangement, and the whole arrangement moves as
one when a marker re-pins the frame.

## What a venue cannot do yet

Venue objects cannot be selected, moved or deleted from the app. Aiming picks
from the outdoor rig's placements, and a venue's live somewhere else — so the
crosshair does not find them and the bar offers nothing for them. Placing works;
correcting a mistake means clearing it from the console.

Worth knowing before an event rather than during one.
