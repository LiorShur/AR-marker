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
| **Place in the world** | A WebXR device and a Firebase project | Objects left at a place and found again — by you tomorrow, or by anyone standing there. |
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
| `scripts/check-policy.mjs` | Fails if Permissions-Policy blocks an API in use |
| `scripts/unit.mjs` | Fast tests for the pure logic — no browser |
| `spatial/geo.js` | WGS84, ENU, and geohash indexing |
| `spatial/store.js` | Placements over the Firestore REST API |
| `spatial/localize.js` | Localization providers and the frame transform |
| `spatial/world.js` | The session controller — fixes, calibration, placements |
| `spatial/config.js` | Firebase project settings — empty means "stay local" |
| `spatial/appcheck.js` | App Check tokens over REST, opt-in, v3 or Enterprise |
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

## Seven things that are load-bearing and not obvious

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

**dom-overlay draws one element and nothing else.** During an immersive WebXR
session the page is not rendered; only the element named by `overlayElement`
and its descendants are. Anything the user has to see or touch mid-session must
live inside it, which is why the HUD and the nearby panel share one `#overlay`
root outside the stage rather than sitting wherever the markup was tidiest.

**Exiting a camera mode has to dispose four separate things.** Removing the
scene from the page stops the render loop and nothing else. AR.js keeps an
ARToolKit context with a WASM heap of tens of megabytes, and a worker that goes
on grabbing frames from a video that no longer has a stream. three.js keeps
every geometry, material and texture on the GPU. AR.js also leaves two
anonymous `window` resize handlers per session that dereference a null element
once disposed, so every rotation of the phone afterwards fires all of them. And
`renderer.dispose()` frees three's own resources but leaves the **WebGL context
alive until the garbage collector gets to the canvas** — browsers allow about
sixteen, phones far fewer, so the third session finds none available.

None of it errors. None of it is visible. It presents as the browser locking up
on returning to the gate, and it does not reproduce on a desktop with sixteen
contexts and eight gigabytes. `teardown()` in `app.js` handles all four, and the
suite runs three start/stop cycles asserting that live contexts stay flat, the
worker is terminated, and the gate still works afterwards.

**`geolocation=()` is off, not "ask".** An empty Permissions-Policy allowlist
disables the API outright — the call fails and the browser never shows a
prompt, which to a user is indistinguishable from the feature being broken and
to a reader looks like a sensible lockdown. It shipped that way. The header
lives only in `firebase.json`, where no test could see it, so `scripts/serve.mjs`
now mirrors the hosting headers and `scripts/check-policy.mjs` fails CI if the
policy contradicts an API the code actually calls.

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
which way that was in local terms.

It refuses three things, and each refusal is a real failure seen in the field:
a baseline shorter than the position noise (at five metres apart, two metres of
error is twenty degrees); a baseline swamped by a poor fix; and a walk the
session did not track — tracking lost, or the device moved without the camera
agreeing, as in a vehicle. In that last case the two bearings describe
different journeys and subtracting them yields a confident, meaningless
number.

### Using it

**Place in the world** appears on the gate once a project is configured and the
device can hold a WebXR session. Then:

1. It finds you — a GPS fix, a few metres of accuracy.
2. **It works out which way you are facing**, which is the hard part. If the
   compass answers, that is used straight away and labelled `±25° compass` —
   usable, and not good. Walking twenty metres in a straight line gives a far
   better bearing from the two positions, and it upgrades itself the moment
   that is available. With no compass at all it asks you to walk and says how
   far you have gone.
3. The HUD shows `±5m · ±6°` — position and heading accuracy, both, always,
   with the source when it is the compass. Nearby placements appear where they
   were left.
4. Tap the reticle to leave something — **as many times as you like**, without
   leaving the session. What you placed is written with the accuracy it was
   placed at, so anyone reading it back knows what it is worth.
5. The count button in the HUD opens **Nearby**: everything within range, with
   a distance and an arrow that turns as you do, so you can find what you
   cannot see. It is also where you change what to place next, and remove
   something you got wrong.

The empty state is deliberate. "Nothing placed within 300 m" and "still working
out which way you are facing" are different sentences, because standing in a
field they are otherwise the same experience.

It keeps sampling: to refine the bearing while you walk, and to re-anchor the
frame once you have moved far enough that WebXR's own drift matters.

### Setting it up

```bash
firebase deploy --only firestore:rules,firestore:indexes,storage   # npm run deploy:rules
cp spatial/config.local.example.js spatial/config.local.js         # then fill it in
```

`spatial/config.local.js` is gitignored and loaded after the committed
defaults, so your project settings stay out of version control and pulls stay
clean. It is optional — absent, the app runs entirely locally, and the
on-screen error trap knows to stay quiet about it.

The web API key is not a secret — it identifies the project and authorises
nothing. What protects the data is `firestore.rules`.

**Anonymous auth is not a security boundary.** Anyone can mint a uid for the
cost of one HTTP request, so the rules treat every write as hostile: shape,
ranges and ownership are all checked server-side. What rules cannot do is
rate-limit — that is what App Check is for.

`spatial/appcheck.js` fetches tokens over REST and attaches them as
`X-Firebase-AppCheck`. The three values come from two different consoles:

| Value | Where |
|---|---|
| `recaptchaSiteKey` | The **site key**, called *ID* in the Cloud console. Never the secret — that goes to Firebase. |
| `provider` | `'v3'` for a classic key from **google.com/recaptcha/admin**; `'enterprise'` for one from the **Google Cloud console**, listed there with an *ID* and a type of *Website / Score*. |
| `projectNumber` | Firebase → Project settings → General → *Project number*. The number, not the id. |
| `appId` | Firebase → Project settings → Your apps → the web app → App ID, `1:…:web:…`. Register a web app if you have none. |

The reCAPTCHA **secret** goes into Firebase → App Check → your web app →
reCAPTCHA v3. The **site key** goes in your local config. Swapping them is the
usual mistake.

Then deploy, confirm tokens are arriving in the App Check console, and only
then switch on enforcement for Firestore. In that order a misconfiguration is
visible before it starts refusing writes. Every load logs what the app thinks
it has — `placements on for <project> — App Check on` or exactly which field is
missing.

reCAPTCHA v3 and reCAPTCHA Enterprise are two products with one name, two
scripts and two exchange endpoints. Both are supported; say which you have in
`provider`, because sending one to the other's endpoint fails without saying
anything useful. There is no visible widget either way — v3 and Enterprise
score-based keys are invisible by design, so nothing appearing on screen is not
a symptom.

Two things to know before you enable it. It is the only part of this project
that loads a script from another origin: reCAPTCHA has to run Google's code
from Google's servers, and there is no offline attestation. And once
enforcement is on, a client that cannot get a token cannot write at all — which
is the point, and also why a failure here looks exactly like the database being
down. A token that cannot be minted is logged and the request goes unattested,
so the refusal comes from the server with a reason attached rather than from a
guess made in the client.

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
- **A better localization provider.** The provider interface is the seam:
  Immersal's REST API would give sub-metre position *and* orientation from one
  camera frame, with no walking, and works indoors and in nature. ARCore's
  Geospatial API would beat it outdoors where Street View reaches, but only
  from a native app.
- **Only yaw is applied.** Placements store a full quaternion, as GeoPose does,
  but content standing on the ground only ever uses the heading. Roll and pitch
  survive a round trip and nothing reads them.
