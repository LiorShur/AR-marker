# Marker One

A level-1 WebAR proof of concept. Point a phone camera at the Hiro marker and a
tracked shard appears on it, holding position on the marker as you move around.
No app store, no SDK, no backend, no accounts.

Everything is vendored locally — A-Frame, AR.js, the pattern file and the camera
calibration file all ship in this folder. Nothing is fetched from a CDN at
runtime, so once the service worker installs, the app works with the network off.

## Run it

Camera access requires a secure context. `localhost` counts; a LAN IP does not.

```bash
# desktop smoke test only — you still need HTTPS to test on a phone
npx serve .
```

To get it on a phone, deploy. Firebase Hosting, since you already have the CLI:

```bash
firebase init hosting     # public directory: .   |   single-page app: No
firebase deploy --only hosting
```

Then open the deployed URL on the phone, tap **Start camera**, and point it at
the marker — printed, or displayed on a laptop screen from `marker.html`.

## What's in here

| File | Role |
|---|---|
| `index.html` | Capability gate and the AR stage |
| `app.js` | Checks, scene construction, tracking-state HUD, teardown |
| `app.css` | All styling |
| `marker.html` | The Hiro marker, full bleed, for printing or a second screen |
| `sw.js` | Precaches every asset; offline after first load |
| `vendor/` | A-Frame 1.5.0 and AR.js 3.4.8 (marker build) |
| `data/` | Hiro pattern file, ARToolKit camera calibration, marker image |

## Tuning

The scene is a template string near the top of `app.js`. Marker space is one
unit per marker width, Y up, origin at the marker centre — so `position="0 0.6 0"`
floats an object roughly six-tenths of a marker width above it.

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

## If it doesn't work

**Black screen after granting the camera.** Almost always the calibration file
failing to load. Check the network tab for `data/camera_para.dat` — it must be
served as a binary download, not rewritten to `index.html`. This is why the
Firebase Hosting rewrite for single-page apps must be off.

**Object appears then flies off.** Glare on the marker, or the black border
partly out of frame. The tracker finds the border first and the glyph second, so
a clipped border loses the pose even when the glyph is clearly visible.

**Works on Android, black on iOS.** iOS Safari needs the page fully reloaded
after a permission change, and it will not open the camera inside an in-app
browser view (Instagram, LinkedIn and similar). Test in Safari proper.

**Nothing appears and there's no error.** Confirm you're pointing at the *Hiro*
marker specifically — an arbitrary black-bordered square won't match.

## Where this goes next

The interesting swap is `data/patt.hiro` for a custom pattern, which is a
15-minute change: generate a `.patt` from your own image with the AR.js marker
training tool, drop it in `data/`, and repoint the `url` attribute. Everything
else stays.
