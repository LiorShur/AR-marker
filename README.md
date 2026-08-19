# Marker One

A WebAR proof of concept with three ways to put an object in the world: a
printed Hiro marker, a printed poster the tracker reads from its own detail,
and — where the device supports it — a real surface with no printed target at
all. No app store, no SDK, no backend, no accounts.

Everything is vendored locally — A-Frame, AR.js, the pattern file and the camera
calibration file all ship in this folder. Nothing is fetched from a CDN at
runtime, so once the service worker installs, the app works with the network off.

Live: **https://markerone1965.web.app/**

## Run it

Camera access needs a secure context. `localhost` counts; `http://192.168.x.x`
does not — which is exactly the URL you want on a phone. So the dev server
speaks HTTPS with a self-signed certificate:

```bash
npm start                 # https://localhost:8443 and https://<lan-ip>:8443
```

Open the LAN URL on the phone, accept the certificate warning once, tap
**Start camera**, and point it at the marker — printed, or displayed on a
laptop screen from `marker.html`.

```bash
npm run start:http        # plain http, localhost only, no warning
npm run deploy            # firebase deploy --only hosting
```

### The three modes

| Mode | Needs | How it works |
|---|---|---|
| **Start camera** | A printed Hiro square, or `marker.html` on a second screen | ARToolKit pattern tracking. Fast, robust, and the black border must stay in frame. |
| **Place in the room** | A WebXR device — most recent Android phones | WebXR hit test against a real floor or table. Tap to place. The button appears only if the browser reports support; iOS has none. |
| **Preview without camera** | Nothing | The same scene on the same marker plane, drag to orbit. |

Which target the camera looks for is a chip in the gate, or `?target=`. The
poster target (`poster.html`) is tracked from its own texture, so unlike the
Hiro square it can be partly covered and still hold.

### Without a marker, or without a camera

Tap **Preview without camera**, or open `/?preview`. It renders the same scene
on the same marker plane with drag-to-orbit and pinch-to-zoom, using A-Frame
alone — AR.js is never loaded. It is the object, not the tracking, and it is
the fastest way to see what the marker carries.

## Test it

```bash
npm i -D playwright && npx playwright install chromium
npm test
```

Sixty assertions. The suite drives a real Chromium against a real server with
**synthetic camera feeds of the Hiro marker and of the poster**, generated on
the fly and handed to Chrome's fake capture device. It asserts the things that
actually break: the gate grading the device, the manifest compiling, the model
decoding and landing where it should, both trackers locking on, the overlay
registering to within a few percent, the shutter compositing a real photo, the
descriptors matching the image they were trained from, and the whole app coming
up with the network switched off. Screenshots land in `screenshots/`.

Markerless mode is the exception: headless Chromium has no XR device, so the
suite covers the capability gate, the compiled session's wiring and the refusal
path, but the hit test itself needs real hardware.

CI runs the same suite on every push and keeps the screenshots as artefacts.

## What's in here

| File | Role |
|---|---|
| `index.html` | Capability gate and the AR stage |
| `app.js` | Checks, lazy vendor loading, the scene compiler, all three modes, HUD, capture, teardown |
| `content.json` | What the targets carry — assets, targets, scenes |
| `app.css` | All styling |
| `marker.html` | The Hiro marker, full bleed, for printing or a second screen |
| `poster.html` | The natural-feature poster, likewise |
| `sw.js` | Precaches the app; caches the rest on first use |
| `firebase.json` | Hosting config — MIME types, cache headers, and no SPA rewrite |
| `scripts/serve.mjs` | Local HTTPS server, no dependencies |
| `scripts/make-poster.mjs` | Draws the natural-feature poster from a seeded PRNG |
| `scripts/smoke.mjs` | End-to-end tests against a synthetic camera |
| `scripts/check-precache.mjs` | Guards the service worker's offline contract |
| `scripts/unit.mjs` | Fast tests for the pure logic — no browser |
| `spatial/geo.js` | WGS84, ENU, and geohash indexing |
| `spatial/store.js` | Placements over the Firestore REST API |
| `spatial/localize.js` | Localization providers and the frame transform |
| `spatial/config.js` | Firebase project settings — empty means "stay local" |
| `firestore.rules` | Who may write what, assuming the client is a lie |
| `vendor/` | A-Frame 1.5.0, AR.js 3.4.8 (NFT build), meshopt decoder |
| `data/` | Hiro pattern, camera calibration, poster and its descriptors |

## Changing what appears

`content.json`, not `app.js`. A **scene** is a list of typed layers — `halo`,
`ring`, `shell`, `motes`, `model`, and a raw `entity` that takes A-Frame
attributes verbatim for anything else. A **target** binds a tracked thing to
the scene it carries. Every scene works in all three modes.

```json
{
  "assets": { "phone": "assets/rotary-phone.glb" },
  "targets": [
    { "id": "hiro", "tracking": "pattern", "pattern": "data/patt.hiro",
      "scene": "rotary-phone", "smooth": { "count": 8 } }
  ],
  "scenes": {
    "rotary-phone": {
      "roomScale": 0.3,
      "layers": [{ "type": "model", "asset": "phone", "scale": 0.35, "spin": 14000 }]
    }
  }
}
```

Coordinates are **one unit per target width**, Y up, origin at the centre, in
every mode — so `"position": [0, 0.6, 0]` floats an object six-tenths of a
target width above it whatever it is printed at. `roomScale` converts that to
metres for markerless mode; without it the object arrives on the floor at the
size of a small car. `contentScale` on a target fits a scene to a sheet that
isn't square.

A second scene, `beacon`, ships as a worked example: no model at all, its own
lights, and it loads no GLB.

Two tracking parameters worth knowing:

- `smooth.count` (8 here) — frames averaged before the pose updates. Raise it
  for a steadier object that lags; lower it for a responsive one that jitters.
- `patternRatio: 0.5` in the `arjs` attribute — the fraction of the Hiro marker
  taken up by its black border. Only change it if you swap in a custom pattern
  generated at a different ratio.

Marker attributes are **kebab-case** (`smooth-count`, not `smoothCount`).
Attributes inside a component string — `geometry`, `material`, `animation` — are
camelCase. Getting this backwards fails silently.

## Making your own natural-feature target

```bash
node scripts/make-poster.mjs            # or bring your own image
npm i -D @webarkit/nft-marker-creator-app
node node_modules/@webarkit/nft-marker-creator-app/src/NFTMarkerCreator.js -i poster.jpg
```

Copy the resulting `.fset`, `.fset3` and `.iset` into `data/nft/` and add a
target to `content.json` with the trained image's width, height and dpi. The
app derives the placement transform from those three numbers.

Three things will waste your afternoon otherwise:

**Feed the generator a JPEG.** Its PNG path reads the image with the wrong row
stride and trains on a sheared, tripled copy — six hundred healthy-looking
features per level, zero matches, no error. `npm test` extracts level 0 of the
`.iset` and correlates it against the source image, which is the only way this
is visible.

**Feature count is not the metric.** A page of solid shapes on white scores as
highly as a photograph and matches nothing: the matcher describes each feature
by the light and dark around it, and every corner of a graphic describes
identically. What works is continuously varying texture — which is why
`make-poster.mjs` builds the sheet out of multi-octave value noise and lays the
structure over the top.

**Mind the scale.** AR.js matches on a 320×240 downsample of the camera feed
whatever the camera's real resolution, so a target filling the frame is about
200 pixels tall by the time it is matched. Detail finer than ~2% of its width
is gone.

## Five things that are load-bearing and not obvious

**The camera video is not part of the scene.** AR.js appends a plain `<video>`
to `<body>` at `z-index: -2` and draws the AR overlay on a transparent WebGL
canvas above it. Anything opaque stacked in between hides the passthrough, and
the result — an object floating on black — looks exactly like a broken
calibration file. `.stage` therefore has no background of its own.

**The canvas has to be the same box as the video, not the same box as the
viewport.** AR.js letterboxes the feed to cover the screen and gives the camera
a projection matrix built from the 4:3 calibration file. A-Frame sizes an
embedded scene's canvas to its container. On a tall phone those disagree
violently — the overlay renders as a stretched ellipse nowhere near the marker.
`applyFeedSize()` in `app.js` copies the video's box onto the canvas and pins
`<body>` so AR.js cannot shift it.

**`[hidden]` needs `display: none !important`.** `.gate` sets `display: flex`,
which outranks the UA stylesheet's rule for the `hidden` attribute, so the gate
stays on screen underneath the AR view.

**AR.js's NFT path has to be kicked.** It defers its setup until a window-level
`arjs-video-loaded` event, but only subscribes once the ARToolkit controller
exists — which cannot happen until `camera_para.dat` has loaded, comfortably
after that event has fired. Left alone it waits forever for something already
gone, requests no descriptors and reports no error. `kickNFT()` re-dispatches
it once the controller is up.

**AR.js grabs frames using the video's CSS box.** It reads `clientWidth` and
`clientHeight` when that event fires and uses those two numbers as the *source
rectangle* of every frame thereafter. Our video is letterboxed to cover the
screen, so it was matching against a crop of one corner.
`dispatchVideoLoaded()` shows the element at its true pixel size for the length
of the dispatch — listeners run synchronously — and restores it immediately.

## If it doesn't work

**Black screen after granting the camera.** Either the calibration file failed
to load — check the network tab for `data/camera_para.dat`, it must be served as
a binary download and not rewritten to `index.html`, which is why `firebase.json`
declares no rewrites — or something opaque is stacked over the video (see above).

**Object appears then flies off.** Glare on the marker, or the black border
partly out of frame. The tracker finds the border first and the glyph second, so
a clipped border loses the pose even when the glyph is clearly visible.

**Works on Android, black on iOS.** iOS Safari needs the page fully reloaded
after a permission change, and it will not open the camera inside an in-app
browser view (Instagram, LinkedIn and similar). Test in Safari proper.

**Nothing appears and there's no error.** Confirm you're pointing at the *Hiro*
marker specifically — an arbitrary black-bordered square won't match.

**A fix didn't deploy.** Append `?reset` to the URL. It unregisters the service
worker, deletes every cache and reloads clean.

## Deploying

`firebase.json` is checked in and matters more than it looks:

- **No rewrites.** A single-page-app rewrite answers `data/camera_para.dat` with
  `index.html`, ARToolKit fails to parse it, and the AR view is a permanent
  black screen with no error.
- `sw.js`, `index.html` and the manifest are served `no-cache`; `vendor/`,
  `icons/`, the model and the data files are `immutable` for a year.
- Explicit `Content-Type` for `.dat`, `.patt` and `.glb`.

To ship a change to `app.js` or `app.css`, bump the `?v=` on both links in
`index.html` **and** `CACHE` in `sw.js`, together. The worker calls
`skipWaiting()` and `clients.claim()`, so a new version takes over on the next
load rather than the one after.

## Placing things in the world

Optional, and off until `spatial/config.js` names a Firebase project. Empty, the
app is exactly what it was: local `content.json`, no network, no accounts.

The design turns on one idea. **Content is stored in one global frame** —
latitude, longitude, height, and an orientation in the local East-North-Up
frame. **Localization is a pluggable provider** whose only job is to report,
once, where the device is in that frame. From that single observation the
transform between global coordinates and the WebXR session's arbitrary local
metres falls out, and the session's own tracking carries it from there.

Nothing needs continuous global positioning — which is just as well, because
none of the accurate ways of getting it are cheap enough to run per frame.
Localize, compute the transform, render locally, re-localize occasionally to
correct drift.

```
provider.locate() -> { position: {lat, lon, h}, headingDeg, accuracy }
      |
      v
makeFrame(fix, sessionPose) -> { toLocal(geoPosition), toGlobal(localPoint) }
```

That seam is why a GPS fix, a visual positioning fix and an RTK fix are
interchangeable: they differ only in what they put in `accuracy`, which is
recorded on every placement rather than inferred, because a placement made by
GPS and one made by a visual fix are the same shape and nothing like the same
thing.

### Heading is the hard part

Position is not what ruins geolocated AR. Five metres of position error on
something fifty metres away is a few degrees and barely visible; twenty degrees
of heading error puts it in the wrong street. A phone magnetometer is routinely
that bad. Every localization method worth having is worth having mostly because
it fixes heading.

`headingFromBaseline()` is the cheap answer that works anywhere, including
under trees where nothing visual will help: two positions a few metres apart
give a bearing to well under a degree, and the session's own tracking says
which way that was in local terms. It refuses baselines shorter than the
position noise, because at five metres apart a two-metre error is twenty
degrees.

### Setting it up

```bash
firebase deploy --only firestore:rules,firestore:indexes,storage   # npm run deploy:rules
```

Then fill in `projectId` and `apiKey` in `spatial/config.js`. The web API key is
not a secret — it identifies the project and authorises nothing. What protects
the data is `firestore.rules`.

**Anonymous auth is not a security boundary.** Anyone can mint a uid for the
cost of one HTTP request, so the rules treat every write as hostile: shape,
ranges and ownership are all checked server-side. What rules cannot do is
rate-limit, so turn on **App Check** before this is public — it is the only
control that costs an abuser anything.

No Firebase SDK. The modular SDK wants a bundler and every hosted copy is a CDN
request, which this project does not make. The REST API needs neither. What
that costs is snapshot listeners: "someone else just placed something" needs a
poll. When shared sessions need to be live rather than merely shared, that is
the moment to vendor the SDK — not before.

## Weight

| | |
|---|---|
| Gate, first paint | ~40 KB |
| A-Frame | 1.4 MB, on tap |
| AR.js (NFT build, serves both trackers) | 1.7 MB, only for camera modes |
| Model | 894 KB, meshopt-compressed from 2.3 MB |
| Poster descriptors | 1.4 MB, only if you use that target |

Preview mode never loads AR.js at all. The descriptors are cached on first use
rather than precached, so a visitor who never prints the poster never pays for
it.

## Where this goes next

- **Texture compression on the model.** The three 1024² JPEGs are 456 KB of the
  remaining 894 KB. WebP would take perhaps 150 KB off it, at the cost of a
  glTF extension with its own compatibility story.
- **Anchors in markerless mode.** WebXR anchors would hold a placed object
  against drift far better than a one-shot hit test does.
- **Occlusion.** A depth-sensing pass on capable Android devices would let the
  object go behind real furniture, which is most of what separates this from a
  sticker.
- **More than one target at once.** Nothing in the manifest format prevents it;
  the scene compiler currently builds one.
