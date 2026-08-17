# Marker One

A level-1 WebAR proof of concept. Point a phone camera at the Hiro marker and a
tracked rotary phone appears on it, holding position on the marker as you move
around. No app store, no SDK, no backend, no accounts.

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

The suite drives a real Chromium against a real server with a **synthetic camera
feed of the Hiro marker**, generated on the fly and handed to Chrome's fake
capture device. It asserts the things that actually break: the gate grading the
device, the model rendering, the tracker locking on, the overlay registering on
the marker to within a few percent, the shutter compositing a real photo, and
the whole app coming up with the network switched off. Screenshots land in
`screenshots/`.

## What's in here

| File | Role |
|---|---|
| `index.html` | Capability gate and the AR stage |
| `app.js` | Checks, lazy vendor loading, both scenes, HUD, capture, teardown |
| `app.css` | All styling |
| `marker.html` | The Hiro marker, full bleed, for printing or a second screen |
| `sw.js` | Precaches every asset; offline after first load |
| `firebase.json` | Hosting config — MIME types, cache headers, and no SPA rewrite |
| `scripts/serve.mjs` | Local HTTPS server, no dependencies |
| `scripts/smoke.mjs` | End-to-end tests against a synthetic camera |
| `vendor/` | A-Frame 1.5.0 and AR.js 3.4.8 (marker build) |
| `data/` | Hiro pattern file, ARToolKit camera calibration, marker image |

## Tuning

The scene is a set of template strings near the top of `app.js`. `CONTENT` is
shared by both modes, so anything added there shows up on the marker and in
preview. Marker space is one unit per marker width, Y up, origin at the marker
centre — so `position="0 0.6 0"` floats an object roughly six-tenths of a marker
width above it.

Two attributes worth knowing:

- `smooth-count` (default 8 here) — frames averaged before the pose updates.
  Raise it for a steadier object that lags; lower it for a responsive one that
  jitters.
- `patternRatio: 0.5` in the `arjs` attribute — the fraction of the marker taken
  up by the black border. Only change this if you swap in a custom marker
  generated at a different ratio.

Marker attributes are **kebab-case** (`smooth-count`, not `smoothCount`).
Attributes inside a component string — `geometry`, `material`, `animation` — are
camelCase. Getting this backwards fails silently.

## Three things that are load-bearing and not obvious

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

## Where this goes next

The interesting swap is `data/patt.hiro` for a custom pattern, which is a
15-minute change: generate a `.patt` from your own image with the AR.js marker
training tool, drop it in `data/`, and repoint the `url` attribute. Everything
else stays.

Beyond that, in rough order of payoff per hour: NFT image tracking so any
printed image works as the marker, a WebXR hit-test path for markerless
placement on modern Android, and a content manifest so the model on the marker
is data rather than a hard-coded asset id.
