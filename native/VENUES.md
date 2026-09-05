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

## When nothing is detected

The panel says which of these it is rather than leaving it to guesswork — a
scene with no tracked-image manager, a manager with no library, and a camera
pointed at a blank wall all behave identically otherwise.

If it says the library is missing, the order matters: **Make venue markers**
first, then **Set up scene**, because setup hands the library to the manager and
cannot hand over one that does not exist yet.

If a marker flickers into view and vanishes, that is ARKit detecting an image
and then not following it. It is fixed in the app now — image tracking has to be
asked for explicitly, and the default is detect-once — but the same symptom can
come from a marker that is too small in frame, too glossy, or lit so unevenly
that half of it is blown out. Fill more of the frame and try again before
suspecting anything else.

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

## Building indoors

**Build on** works in a venue exactly as it does outdoors — the same faces, the
same gaps, the same badge on a piece. A structure in here stores both a venue
pose and an offset from its parent, so it keeps its shape if the thing
underneath it is ever moved, and still has somewhere to stand if that thing is
removed.

## Walking between venues

Point the camera at a marker belonging to a venue this device has never heard of
and it goes there: the name is looked up, the venue loads, the frame pins.
Walking from one hall to the next needs nothing but pointing the phone at the
marker by the door.

A name is looked up once. One that nobody has recorded is remembered as such, so
an organizer standing in front of a fresh marker is not asking the store about
it every frame.

Moving takes deliberateness, which matters at a doorway where markers from both
sides are in view at once. A marker belonging to the current venue means you are
still in it, whatever else the camera can also see; the stranger has to stay in
view for a second and a half before it counts; and nothing is a stranger until
the current venue's own markers have loaded.

"In view" is measured rather than asked: within twelve metres and roughly in the
picture. A tracked image is never removed once found — it keeps reporting a pose
from the next room, the next floor, and after an hour in a pocket — so asking
tracking state instead answers "was one ever seen", which is true forever and
means walking into another room changes nothing at all.

The practical consequence: turn to face the new room's marker, with the old one
behind you, and it switches. Standing in a doorway looking at both keeps you in
the one you came from, which is the right answer for a doorway.

Changing venue also takes an actual sighting rather than a marker that merely
lies ahead. Nothing in this app recognises a room — there is no scene matching
of any kind, and the only thing that can identify a place is a printed image. But
ARKit keeps the anchor of every marker it has ever found, for the whole session,
so walking back towards where one used to be satisfies the geometry without the
camera having seen anything. That is what a room appearing to change itself with
no marker in front of the phone actually was.

## Nothing is drawn until the venue is pinned

Loading a venue finishes before pinning it does, so for a moment after arriving
the new room's contents would otherwise be drawn against the old room's frame:
the right objects in the wrong place, floating, which reads as the app being
confused about which room you are in. So a venue shows nothing until one of its
own markers has said where it is, and the panel says as much while you are
pointing the camera around looking for one. Without the last of those, switching
made every marker unfamiliar for a moment — including the one just walked away
from — and a single step through a door became a loop.

## The two worlds, and the switch between them

**Mode: venues** — markers decide. The default, and what somebody walking a
building wants.

**Mode: outdoors** — markers are ignored entirely and everything is placed on
the Earth, whatever the camera can see.

The button is in the venue panel, and it is a separate thing from **Leave**.
Leave puts down the current venue while staying in venue mode, so the next
marker picks one up again; the mode switch is how you stop venues happening at
all. Both are needed, because a venue is remembered across launches and a marker
can now enter one without being asked.

Whichever world is current, the placement bar says so on the button that acts:
**Place in <name>** rather than **Place**.

## What a venue cannot do yet

Venue objects can be aimed at and built on, but not moved or deleted from the
app. Correcting a mistake means clearing it from the Firestore console.

Worth knowing before an event rather than during one.

Worth knowing before an event rather than during one.
