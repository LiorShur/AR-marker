/* End-to-end smoke test.
 *
 * Drives a real Chromium against a real server, with a synthetic camera feed
 * of the Hiro marker, and asserts the things that actually break: the gate
 * grading the device, the model rendering, the tracker locking on, and the
 * service worker holding the app up with the network off.
 *
 *   npm i -D playwright && npx playwright install chromium
 *   npm test
 */

import { spawn } from 'node:child_process';
import { mkdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeFakeCamera } from './make-fake-camera.mjs';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const SHOTS = join(ROOT, 'screenshots');
const PORT = 8181;
// The marker fills this fraction of the synthetic feed's short side; the
// registration tolerance is expressed against its on-screen size, not the
// viewport.
const MARKER_FRAC = 0.36;
let MARKER_PX = 0;
const ORIGIN = `http://localhost:${PORT}`;   // localhost is a secure context over plain http

let chromium;
try {
  ({ chromium } = await import('playwright'));
} catch {
  console.error('\nplaywright is not installed. It is a dev-only dependency:\n');
  console.error('  npm i -D playwright && npx playwright install chromium\n');
  process.exit(1);
}

const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok, detail });
  console.log(`  ${ok ? '✓' : '✗'} ${name}${detail ? '  — ' + detail : ''}`);
};

await rm(SHOTS, { recursive: true, force: true });
await mkdir(SHOTS, { recursive: true });

console.log('\n  building the fake camera feed…');
const feed = await makeFakeCamera({
  chromium,
  markerPath: join(ROOT, 'data', 'marker-hiro.png'),
  out: join(SHOTS, 'fake-camera.y4m')
});

console.log('  starting the server…');
const server = spawn(process.execPath, [join(ROOT, 'scripts', 'serve.mjs'), '--http', '--port', String(PORT)], {
  stdio: 'ignore'
});
await waitForServer();

const browser = await chromium.launch({
  args: [
    '--use-fake-device-for-media-stream',
    '--use-fake-ui-for-media-stream',
    `--use-file-for-fake-video-capture=${feed.path}`,
    // Headless has no GPU; SwiftShader is what makes WebGL exist at all here.
    '--use-gl=angle',
    '--use-angle=swiftshader',
    '--enable-unsafe-swiftshader'
  ]
});

try {
  console.log('\n  gate');
  await testGate();

  console.log('\n  preview mode');
  await testPreview();

  console.log('\n  marker tracking');
  await testTracking();

  console.log('\n  offline');
  await testOffline();
} finally {
  await browser.close();
  server.kill();
}

const failed = results.filter((r) => !r.ok);
console.log(`\n  ${results.length - failed.length}/${results.length} passed`);
console.log(`  screenshots in ${SHOTS}\n`);
process.exit(failed.length ? 1 : 0);

/* ── tests ──────────────────────────────────────────────── */

async function testGate() {
  const { page, close } = await open();
  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  check('gate paints before the vendor bundle',
    await page.locator('.title').isVisible());

  const count = await page.locator('#dial-count').textContent();
  check('all three capability checks pass', count === '3/3', count);

  check('start button is enabled', await page.locator('#start').isEnabled());
  check('preview button is enabled', await page.locator('#preview').isEnabled());

  const vendorLoaded = await page.evaluate(() => typeof window.AFRAME !== 'undefined');
  check('vendor bundle is NOT loaded on first paint', vendorLoaded === false);

  await page.screenshot({ path: join(SHOTS, '1-gate.png') });
  await close();
}

async function testPreview() {
  const { page, errors, offsite, close } = await open();
  await page.goto(ORIGIN + '/?preview', { waitUntil: 'load' });

  await page.waitForFunction(() => {
    const s = document.getElementById('scene');
    return s && s.hasLoaded;
  }, null, { timeout: 45000 });

  // Wait for the GLB to attach a mesh, not just for the scene to exist.
  const modelLoaded = await page.waitForFunction(() => {
    const el = document.getElementById('shard');
    if (!el || !el.object3D) return false;
    let meshes = 0;
    el.object3D.traverse((o) => { if (o.isMesh) meshes++; });
    return meshes > 0 && meshes;
  }, null, { timeout: 45000 }).then((h) => h.jsonValue()).catch(() => 0);

  check('preview scene loads', true);
  check('rotary-phone.glb renders meshes', modelLoaded > 0, modelLoaded + ' meshes');

  // The model is meshopt-compressed; without the decoder GLTFLoader throws
  // and the entity stays empty. Check the geometry survived intact and that
  // it still sits where app.js positions it — gltfpack rewrites node
  // transforms, and a silently rescaled model would sink into the marker.
  const geo = await page.evaluate(() => {
    const el = document.getElementById('shard');
    let verts = 0;
    el.object3D.traverse((o) => { if (o.isMesh) verts += o.geometry.attributes.position.count; });
    const box = new AFRAME.THREE.Box3().setFromObject(el.object3D);
    const size = box.getSize(new AFRAME.THREE.Vector3());
    const centre = box.getCenter(new AFRAME.THREE.Vector3());
    return { verts, size: size.toArray(), centre: centre.toArray(), minY: box.min.y };
  });
  check('meshopt geometry decodes fully', geo.verts === 46726, geo.verts + ' vertices');
  check('model is about a marker-third tall', geo.size[1] > 0.25 && geo.size[1] < 0.45,
    'height ' + geo.size[1].toFixed(2));
  check('model rests on the marker', Math.abs(geo.minY) < 0.06, 'base at y=' + geo.minY.toFixed(3));
  check('model is centred on the marker', Math.hypot(geo.centre[0], geo.centre[2]) < 0.08,
    'offset ' + Math.hypot(geo.centre[0], geo.centre[2]).toFixed(3));

  await page.waitForTimeout(1500);
  check('canvas has drawn something', await canvasHasInk(page));

  // Orbit: drag and confirm the camera rig actually moved.
  const before = await rigPosition(page);
  await page.mouse.move(400, 300);
  await page.mouse.down();
  await page.mouse.move(560, 260, { steps: 12 });
  await page.mouse.up();
  await page.waitForTimeout(700);
  const after = await rigPosition(page);
  const moved = before && after && distance(before, after) > 0.15;
  check('drag orbits the camera', moved, moved ? `moved ${distance(before, after).toFixed(2)}` : 'no movement');
  check('preview throws nothing', errors.length === 0, errors.join('; '));
  check('preview fetches nothing off-origin', offsite.length === 0, offsite.join(', '));
  check('preview does not load AR.js', await page.evaluate(() => typeof window.ARjs === 'undefined'));

  await page.screenshot({ path: join(SHOTS, '2-preview.png') });
  await close();
}

async function testTracking() {
  const { page, errors, offsite, close } = await open();
  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  await page.locator('#start').click();

  await page.waitForFunction(() => {
    const s = document.getElementById('scene');
    return s && s.hasLoaded;
  }, null, { timeout: 60000 });
  check('AR scene builds after the tap', true);

  const feedLive = await page.waitForFunction(
    () => { const v = document.querySelector('video'); return !!(v && v.videoWidth > 0); },
    null, { timeout: 30000 }
  ).then(() => true).catch(() => false);
  check('camera feed is live', feedLive);

  const locked = await page.waitForFunction(
    () => document.getElementById('state').dataset.state === 'locked',
    null, { timeout: 60000 }
  ).then(() => true).catch(() => false);
  check('tracker locks onto the Hiro marker', locked);

  if (locked) {
    const visible = await page.evaluate(() => {
      const m = document.getElementById('marker');
      return !!(m && m.object3D && m.object3D.visible);
    });
    check('marker subtree is visible when locked', visible);

    // The camera <video> hangs off <body> at z-index -2. Anything opaque
    // stacked above it hides the passthrough and leaves the object floating
    // on black — which reads exactly like a broken calibration file.
    const feed = await page.evaluate(() => {
      const v = document.querySelector('video');
      const el = document.elementFromPoint(4, 4);
      const chain = [];
      for (let n = el; n && n !== document.documentElement; n = n.parentElement) {
        const cs = getComputedStyle(n);
        chain.push({ tag: n.tagName + (n.id ? '#' + n.id : ''), bg: cs.backgroundColor });
      }
      const opaque = chain.filter((c) => c.bg !== 'rgba(0, 0, 0, 0)' && !/, 0\)$/.test(c.bg));
      const c = document.getElementById('scene').canvas.getBoundingClientRect();
      const r = v.getBoundingClientRect();
      return { opaque, dx: Math.abs(c.width - r.width), dy: Math.abs(c.height - r.height) };
    });
    check('camera passthrough is not covered', feed.opaque.length === 0,
      feed.opaque.map((o) => o.tag + ' ' + o.bg).join(', '));
    check('GL canvas is letterboxed onto the feed', feed.dx < 2 && feed.dy < 2,
      `off by ${feed.dx.toFixed(0)}x${feed.dy.toFixed(0)}px`);

    // The overlay is only as good as its registration: project the marker's
    // own corners and compare with where the marker actually sits in the
    // feed. Residual error is calibration (camera_para.dat is somebody
    // else's webcam), not geometry — but hundreds of pixels is geometry.
    const err = await registrationError(page);
    check('overlay registers on the marker', err !== null && err < MARKER_PX * 0.12,
      err === null ? 'could not measure' : `worst corner off by ${err.toFixed(0)}px`);
    check('AR throws nothing', errors.length === 0, errors.join('; '));
    check('AR fetches nothing off-origin', offsite.length === 0, offsite.join(', '));

    await page.waitForTimeout(1200);
    await page.screenshot({ path: join(SHOTS, '3-tracking.png') });

    // The shutter composites the camera feed and the transparent GL canvas
    // by hand. Nothing else exercises that path, and it fails silently.
    const shot = await grabShutterOutput(page);
    check('the shutter produces a photo', !!shot, shot ? `${shot.bytes} bytes` : 'no download');
    if (shot) {
      check('the photo contains the camera feed', shot.feed > 500, shot.feed + ' feed pixels');
      check('the photo contains the AR overlay', shot.overlay > 100, shot.overlay + ' overlay pixels');
    }
  }

  await close();
}

async function testOffline() {
  const { page, context, close } = await open();
  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  const ready = await page.waitForFunction(
    () => navigator.serviceWorker.ready.then(() => true),
    null, { timeout: 30000 }
  ).then(() => true).catch(() => false);
  check('service worker registers', ready);

  // The precache is ~6 MB; give addAll time to finish before pulling the plug.
  const cached = await page.waitForFunction(async () => {
    const keys = await caches.keys();
    if (!keys.length) return false;
    const c = await caches.open(keys[0]);
    return (await c.keys()).length;
  }, null, { timeout: 60000 }).then((h) => h.jsonValue()).catch(() => 0);
  check('every asset is precached', cached >= 15, cached + ' entries');

  const glb = await page.evaluate(async () => {
    const keys = await caches.keys();
    const c = await caches.open(keys[0]);
    return !!(await c.match('assets/rotary-phone.glb'));
  });
  check('the model is in the precache', glb);

  await context.setOffline(true);
  const res = await page.goto(ORIGIN + '/?preview', { waitUntil: 'load' }).catch(() => null);
  check('page loads with the network off', !!res);

  const offlinePreview = await page.waitForFunction(() => {
    const el = document.getElementById('shard');
    if (!el || !el.object3D) return false;
    let meshes = 0;
    el.object3D.traverse((o) => { if (o.isMesh) meshes++; });
    return meshes > 0;
  }, null, { timeout: 45000 }).then(() => true).catch(() => false);
  check('the model still renders offline', offlinePreview);

  await page.screenshot({ path: join(SHOTS, '4-offline.png') });
  await context.setOffline(false);
  await close();
}

/* ── helpers ────────────────────────────────────────────── */


async function registrationError(page) {
  return page.evaluate(({ frac }) => {
    const video = document.querySelector('video');
    const scene = document.getElementById('scene');
    const marker = document.getElementById('marker');
    if (!video || !scene || !marker) return null;

    const vr = video.getBoundingClientRect();
    const cr = scene.canvas.getBoundingClientRect();
    const THREE = AFRAME.THREE;

    const projected = [[-0.5, 0, -0.5], [0.5, 0, -0.5], [0.5, 0, 0.5], [-0.5, 0, 0.5]].map(([x, y, z]) => {
      const v = new THREE.Vector3(x, y, z);
      marker.object3D.localToWorld(v);
      v.project(scene.camera);
      return [cr.x + (v.x * 0.5 + 0.5) * cr.width, cr.y + (-v.y * 0.5 + 0.5) * cr.height];
    });

    const side = vr.height * frac;         // the feed is 4:3, marker on the short side
    const cx = vr.x + vr.width / 2;
    const cy = vr.y + vr.height / 2;
    const truth = [[cx - side / 2, cy - side / 2], [cx + side / 2, cy - side / 2],
                   [cx + side / 2, cy + side / 2], [cx - side / 2, cy + side / 2]];

    return {
      side,
      worst: Math.max(...projected.map((p, i) => Math.hypot(p[0] - truth[i][0], p[1] - truth[i][1])))
    };
  }, { frac: MARKER_FRAC }).then((r) => {
    if (!r) return null;
    MARKER_PX = r.side;
    return r.worst;
  }).catch(() => null);
}

async function grabShutterOutput(page) {
  const pending = page.waitForEvent('download', { timeout: 15000 }).catch(() => null);
  await page.locator('#shoot').click();
  const download = await pending;
  if (!download) return null;

  const path = join(SHOTS, '5-photo.png');
  await download.saveAs(path);
  const { readFile } = await import('node:fs/promises');
  const buf = await readFile(path);

  // Decode it in a throwaway page — a PNG that is one flat colour means the
  // composite grabbed an empty buffer.
  const probe = await browser.newPage();
  const stats = await probe.evaluate(async (b64) => {
    const img = new Image();
    img.src = 'data:image/png;base64,' + b64;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = 160; c.height = 120;
    const ctx = c.getContext('2d');
    ctx.drawImage(img, 0, 0, c.width, c.height);
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    const seen = new Set();
    let feed = 0, overlay = 0;
    for (let i = 0; i < d.length; i += 4) {
      const r = d[i], g = d[i + 1], b = d[i + 2];
      seen.add(`${r >> 3},${g >> 3},${b >> 3}`);
      // the synthetic feed is a white field; the overlay is teal (#6BE3E8)
      if (r > 230 && g > 230 && b > 230) feed++;
      if (b > 150 && g > 140 && r < g - 40) overlay++;
    }
    return { colours: seen.size, feed, overlay };
  }, buf.toString('base64'));
  await probe.close();

  return { bytes: buf.length, ...stats };
}

async function open() {
  const context = await browser.newContext({
    permissions: ['camera'],
    viewport: { width: 900, height: 700 }
  });
  const page = await context.newPage();
  const errors = [];
  const offsite = [];
  page.on('pageerror', (e) => { errors.push(e.message); console.log('    [page error] ' + (e.stack || e.message)); });
  page.on('request', (r) => {
    const u = new URL(r.url());
    if (u.origin !== ORIGIN && u.protocol !== 'data:' && u.protocol !== 'blob:') { offsite.push(r.url()); }
  });
  return { page, context, errors, offsite, close: () => context.close() };
}

async function rigPosition(page) {
  return page.evaluate(() => {
    const rig = document.getElementById('rig');
    if (!rig || !rig.object3D) return null;
    const p = rig.object3D.position;
    return { x: p.x, y: p.y, z: p.z };
  });
}

function distance(a, b) {
  return Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
}

// A scene that failed to render leaves the canvas uniformly clear. Sampling a
// grid catches "it rendered nothing" without asserting on exact pixels.
async function canvasHasInk(page) {
  return page.evaluate(() => {
    const scene = document.getElementById('scene');
    const src = scene?.canvas;
    if (!src) return false;
    try { scene.renderer.render(scene.object3D, scene.camera); } catch { /* read what is there */ }
    const c = document.createElement('canvas');
    c.width = 120; c.height = 90;
    const ctx = c.getContext('2d');
    ctx.drawImage(src, 0, 0, c.width, c.height);
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    const seen = new Set();
    for (let i = 0; i < d.length; i += 4) {
      seen.add(`${d[i] >> 4},${d[i + 1] >> 4},${d[i + 2] >> 4},${d[i + 3] >> 4}`);
      if (seen.size > 6) return true;
    }
    return false;
  });
}

async function waitForServer() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(ORIGIN + '/index.html');
      if (r.ok) return;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error('server did not start');
}
