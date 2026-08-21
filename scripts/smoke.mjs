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

  console.log('\n  manifest');
  await testManifest();

  console.log('\n  markerless (WebXR)');
  await testXR();

  console.log('\n  natural-feature target');
  await testNFT();

  console.log('\n  descriptor integrity');
  await testDescriptors();

  console.log('\n  placements in the world');
  await testWorld();

  console.log('\n  teardown');
  await testTeardown();
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

  // Without this there is no way to tell a deployed fix from a cached one
  // except by bisecting behaviour on a phone in a field.
  const build = await page.locator('#build').textContent();
  check('the build stamp is on the page', /^\d+$/.test(build.trim()), 'build ' + build);

  // An installed app has no address bar, so a URL-only diagnostic flag cannot
  // be turned on in the place it is most needed.
  const traceSticks = await page.evaluate(async () => {
    localStorage.removeItem('marker-one:trace');
    return true;
  });
  await page.goto(ORIGIN + '/?trace', { waitUntil: 'load' });
  const persisted = await page.evaluate(() => localStorage.getItem('marker-one:trace'));
  check('?trace survives into an installed app', traceSticks && persisted === '1');
  await page.goto(ORIGIN + '/?trace=off', { waitUntil: 'load' });
  check('and can be turned back off',
    !(await page.evaluate(() => localStorage.getItem('marker-one:trace'))));
  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  const count = await page.locator('#dial-count').textContent();
  check('all three capability checks pass', count === '3/3', count);

  check('start button is enabled', await page.locator('#start').isEnabled());

  // The dev server mirrors the hosting headers so that local and deployed are
  // the same environment. They were not, and geolocation was disabled in
  // production by a header no test could see.
  const policy = await page.evaluate(async () => {
    const res = await fetch(location.href, { method: 'GET', cache: 'no-store' });
    return res.headers.get('permissions-policy') || '';
  });
  check('the dev server sends the hosting headers', policy.length > 0, policy);
  check('geolocation is not disabled by policy',
    !/geolocation=\(\)/.test(policy) && /geolocation=\(self\)/.test(policy));
  check('the camera is not disabled by policy', /camera=\(self\)/.test(policy));

  // And the API is genuinely reachable, not merely un-forbidden on paper.
  const reachable = await page.evaluate(() => new Promise((resolve) => {
    if (!navigator.geolocation) { return resolve('absent'); }
    navigator.geolocation.getCurrentPosition(
      () => resolve('ok'),
      (err) => resolve(/permissions policy|disabled in this document/i.test(err.message || '')
        ? 'blocked by policy' : 'ok'),
      { timeout: 3000 }
    );
  }));
  check('geolocation is actually callable', reachable === 'ok', reachable);

  // The picker is rendered from content.json, so its presence proves the
  // manifest was fetched, parsed and understood before any tap. One row of
  // chips per axis of choice: which target to track, and what it carries.
  await page.waitForSelector('#picker .chip', { timeout: 10000 }).catch(() => {});
  const rows = await page.locator('#picker .picker__row').count();
  const targets = await page.locator('#picker .picker__row').nth(0).locator('.chip').allTextContents();
  const scenes = await page.locator('#picker .picker__row').nth(1).locator('.chip').allTextContents();
  check('picker offers both targets', rows === 2 && targets.length === 2, targets.join(' / '));
  check('picker offers both scenes', scenes.length === 2, scenes.join(' / '));
  check('preview button is enabled', await page.locator('#preview').isEnabled());

  const vendorLoaded = await page.evaluate(() => typeof window.AFRAME !== 'undefined');
  check('vendor bundle is NOT loaded on first paint', vendorLoaded === false);

  // The spatial modules are plain scripts with a CommonJS footer so the unit
  // tests can require them. If that footer misfires they register nothing and
  // fail only in a browser — which is the one place the unit suite never looks.
  const spatial = await page.evaluate(() => ({
    geo: typeof window.SpatialGeo,
    store: typeof window.SpatialStore,
    localize: typeof window.SpatialLocalize,
    config: typeof window.SpatialConfig,
    enabled: window.SpatialConfig && window.SpatialConfig.enabled,
    // and the maths survives the trip into a browser
    hash: window.SpatialGeo && window.SpatialGeo.geohash(57.64911, 10.40744, 11)
  }));
  check('the spatial modules register in a browser too',
    spatial.geo === 'object' && spatial.store === 'object' &&
    spatial.localize === 'object' && spatial.config === 'object',
    Object.entries(spatial).map(([k, v]) => `${k}=${v}`).join(' '));
  check('geodesy works the same in the browser', spatial.hash === 'u4pruydqqvj', spatial.hash);
  check('placements stay off until a project is configured', spatial.enabled === false);

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

  /* A deploy has to be visible on the next load, not the one after.
     Navigations used to come from the cache, and assets were matched with
     ignoreSearch — so a bumped ?v= found the previous build's file and the
     whole versioning scheme did nothing. Editing the served file and
     reloading is the only honest test of that. */
  const { readFile, writeFile } = await import('node:fs/promises');
  const indexPath = join(ROOT, 'index.html');
  const original = await readFile(indexPath, 'utf8');

  try {
    // The title, because nothing in app.js rewrites it — the build element
    // is overwritten on load, which makes it useless as a marker for which
    // *document* arrived.
    await writeFile(indexPath, original.replace(
      /<title>[^<]*<\/title>/, '<title>redeployed</title>'));
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });
    const title = await page.title();
    check('a redeploy is visible on the very next load', title === 'redeployed',
      title === 'redeployed' ? 'fresh document' : 'served the cached one: ' + title);
  } finally {
    await writeFile(indexPath, original);
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });
  }

  await close();
}

async function testTeardown() {
  // The bug this pins down: stopping a camera mode used to leave the WebGL
  // context, the ARToolKit heap, AR.js's resize handlers and its worker all
  // alive. Nothing errors, nothing is visible, and after a session or two a
  // phone has no contexts left to give and locks up. It survived this long
  // because on a desktop with sixteen contexts and eight gigabytes it simply
  // does not show.
  const { page, errors, close } = await open();
  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  const readings = [];
  for (let i = 0; i < 3; i++) {
    await page.locator('#start').click();
    await page.waitForFunction(() => document.getElementById('scene')?.hasLoaded,
      null, { timeout: 60000 });
    await page.waitForTimeout(1200);
    await page.evaluate(() => { window.__renderer = document.getElementById('scene').renderer; });

    await page.locator('#exit').click();
    await page.waitForTimeout(1200);

    readings.push(await page.evaluate(async () => {
      const before = window.__renderer ? window.__renderer.info.render.frame : -1;
      await new Promise((r) => setTimeout(r, 700));
      return {
        stillRendering: window.__renderer ? window.__renderer.info.render.frame - before : -1,
        glLive: window.__glLive,
        glMade: window.__glMade,
        canvases: document.querySelectorAll('canvas').length,
        videos: document.querySelectorAll('video').length,
        scenes: document.querySelectorAll('a-scene').length
      };
    }));
  }

  const last = readings[readings.length - 1];
  check('the render loop stops', readings.every((r) => r.stillRendering === 0));
  check('the canvas, video and scene are all gone',
    readings.every((r) => r.canvases === 0 && r.videos === 0 && r.scenes === 0));
  check('a new context is taken each session', last.glMade >= 3, last.glMade + ' created');
  check('and released each time — contexts do not accumulate',
    readings.every((r) => r.glLive === readings[0].glLive),
    readings.map((r) => r.glLive).join(' -> ') + ' live');
  check('teardown throws nothing', errors.length === 0, errors.slice(0, 2).join('; '));

  // The gate has to be usable afterwards, not merely visible. An error
  // overlay or a stuck fixed-position body both leave it looking fine.
  check('the gate is interactive again', await page.locator('#preview').isEnabled());
  await page.locator('#preview').click();
  const recovered = await page.waitForFunction(() => document.getElementById('scene'),
    null, { timeout: 45000 }).then(() => true).catch(() => false);
  check('a mode can be started again after three cycles', recovered);
  await close();

  // Natural-feature mode is the one that starts a worker, and the worker
  // outlives the scene: it keeps grabbing frames from a dead video and
  // shipping buffers to nobody.
  const feed = await makeFakeCamera({
    chromium,
    markerPath: join(ROOT, 'data', 'poster.jpg'),
    out: join(SHOTS, 'fake-poster.y4m'),
    scale: 0.9, width: 1280, height: 960, fit: 'height'
  });
  const nft = await chromium.launch({
    args: [
      '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream',
      `--use-file-for-fake-video-capture=${feed.path}`,
      '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'
    ]
  });
  try {
    const context = await nft.newContext({
      permissions: ['camera'], viewport: { width: 900, height: 700 }
    });
    const page2 = await context.newPage();
    await page2.addInitScript(() => {
      window.__workersMade = 0;
      window.__workersLive = 0;
      const RealWorker = window.Worker;
      function Counted(...args) {
        const worker = new RealWorker(...args);
        window.__workersMade++;
        window.__workersLive++;
        const terminate = worker.terminate.bind(worker);
        worker.terminate = function () { window.__workersLive--; return terminate(); };
        return worker;
      }
      Counted.prototype = RealWorker.prototype;
      window.Worker = Counted;
    });

    await page2.goto(ORIGIN + '/?target=poster', { waitUntil: 'load' });
    await page2.locator('#start').click();
    await page2.waitForFunction(
      () => document.getElementById('state').dataset.state === 'locked',
      null, { timeout: 120000 }
    ).catch(() => {});

    const started = await page2.evaluate(() => window.__workersMade);
    await page2.locator('#exit').click();
    await page2.waitForTimeout(1500);
    const alive = await page2.evaluate(() => window.__workersLive);

    check('natural-feature tracking starts a worker', started > 0, started + ' started');
    check('and it is terminated on exit', alive === 0, alive + ' still running');
    await context.close();
  } finally {
    await nft.close();
  }
}

async function testWorld() {
  // The controller's logic is covered thoroughly by the unit suite. What the
  // browser adds is integration: that the modules see each other's globals,
  // that the mode is gated on both a project and a WebXR device, and that the
  // scene it builds is wired to the hit test.
  const xrStub = `
    Object.defineProperty(navigator, 'xr', {
      configurable: true,
      value: {
        isSessionSupported: (m) => Promise.resolve(m === 'immersive-ar'),
        requestSession: () => new Promise(() => {})
      }
    });`;

  {
    const { page, close } = await open();
    await page.addInitScript(xrStub);
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });
    await page.waitForSelector('#xr:not([hidden])', { timeout: 10000 }).catch(() => {});
    check('world mode is hidden with no project configured',
      await page.locator('#world').isHidden());
    check('and the nearby button with it', await page.locator('#list').isHidden());
    await close();
  }

  const { page, errors, close } = await open();
  await page.addInitScript(xrStub);
  await page.addInitScript(() => {
    // config.local.js is gitignored, so stand in for it the same way it would.
    window.addEventListener('DOMContentLoaded', () => {}, { once: true });
    Object.defineProperty(window, '__stubConfig', { value: true });
  });
  // Applied after config.js defines override(), before app.js reads enabled.
  await page.route('**/spatial/config.local.js', (r) => r.fulfill({
    status: 200,
    contentType: 'text/javascript',
    body: "SpatialConfig.override({ projectId: 'test-project', apiKey: 'test-key' });"
  }));

  await page.goto(ORIGIN + '/', { waitUntil: 'load' });

  // Setting App Check up spans two consoles and four fields; the failure mode
  // of getting one wrong is silence. The app says what it thinks it has.
  const notes = [];
  page.on('console', (m) => { if (m.type() === 'info') { notes.push(m.text()); } });
  await page.reload({ waitUntil: 'load' });
  check('the app reports its own spatial configuration',
    notes.some((n) => /placements on for test-project/.test(n)), notes.join(' | ').slice(0, 90));
  check('and names App Check as off until it is filled in',
    notes.some((n) => /App Check off — missing/.test(n)));

  check('an optional local config is applied when present',
    await page.evaluate(() => window.SpatialConfig.enabled === true &&
      window.SpatialConfig.projectId === 'test-project'));

  const offered = await page.waitForSelector('#world:not([hidden])', { timeout: 10000 })
    .then(() => true).catch(() => false);
  check('world mode appears with a project and a WebXR device', offered);

  if (offered) {
    await page.locator('#world').click();
    await page.waitForFunction(() => document.getElementById('placements'), null, { timeout: 45000 });
    await page.waitForTimeout(400);

    const wiring = await page.evaluate(() => {
      const scene = document.getElementById('scene');
      const hit = scene.getAttribute('ar-hit-test');
      return {
        hitTarget: typeof hit === 'string' ? hit : (hit && hit.target && hit.target.id),
        reticle: !!document.getElementById('reticle'),
        reticleHidden: document.getElementById('reticle').getAttribute('visible') === false,
        container: !!document.getElementById('placements'),
        assets: document.querySelectorAll('a-asset-item').length,
        arjs: typeof window.ARjs === 'undefined'
      };
    });
    check('the hit test drives a reticle', /reticle/.test(String(wiring.hitTarget)));

    // The panel is the answer to "am I in an empty field or is this broken".
    // A headless session cannot walk, so it never localizes — which is
    // exactly the state whose messaging matters most.
    // dom-overlay draws one element and its descendants and nothing else, so
    // anything the user must see mid-session has to live inside it. With it
    // pointed at the HUD, the nearby panel did not exist during a session.
    const overlay = await page.evaluate(() => {
      const scene = document.getElementById('scene');
      const xr = scene.getAttribute('webxr');
      const root = document.getElementById('overlay');
      return {
        target: xr && xr.overlayElement && xr.overlayElement.id,
        holdsHud: !!root.querySelector('#hud'),
        holdsNearby: !!root.querySelector('#nearby'),
        outsideStage: !document.getElementById('stage').contains(root)
      };
    });
    check('dom-overlay points at the overlay root', overlay.target === 'overlay', overlay.target);

    // With dom-overlay, a tap on the interface is delivered to the DOM and to
    // the session as a select. Without this, pressing Stop also fired the hit
    // test — creating an XR anchor at the moment the session was ending.
    const suppressed = await page.evaluate(() => {
      const e = new Event('beforexrselect', { bubbles: true, cancelable: true });
      document.getElementById('exit').dispatchEvent(e);
      return e.defaultPrevented;
    });
    check('a tap on the interface is not also a placement', suppressed);
    check('which holds both the HUD and the nearby panel',
      overlay.holdsHud && overlay.holdsNearby);
    check('and is not inside the stage', overlay.outsideStage);

    check('the nearby button is offered in world mode',
      await page.locator('#list').isVisible());
    await page.locator('#list').click();
    check('the panel opens', await page.locator('#nearby').isVisible());

    const empty = await page.locator('#nearby-empty').textContent();
    check('an unlocated session says so rather than showing an empty list',
      /finding you|walk a few metres/i.test(empty), empty.slice(0, 60));
    check('and shows no list', (await page.locator('#nearby-list li').count()) === 0);

    check('a name can be set for things you leave',
      await page.locator('#nearby-name').isVisible());
    await page.locator('#nearby-name').fill('Lior');
    await page.locator('#nearby-name').dispatchEvent('change');
    const stored = await page.evaluate(() => localStorage.getItem('marker-one:name'));
    check('and it is remembered', stored === 'Lior', String(stored));

    check('remove-all is hidden when nothing here is yours',
      await page.locator('#nearby-drop-all').isHidden());

    const chips = await page.locator('#nearby-place .chip').allTextContents();
    check('what to place next can be changed without leaving', chips.length === 2,
      chips.join(' / '));

    await page.locator('#nearby-close').click();
    check('the panel closes', await page.locator('#nearby').isHidden());
    check('the reticle starts hidden', wiring.reticleHidden);
    check('there is a container for placements', wiring.container);
    check('every scene\'s assets are declared', wiring.assets >= 1, wiring.assets + ' asset(s)');
    check('world mode loads no AR.js', wiring.arjs);
  }

  // The controller, driven directly in the browser with stubs — the walk that
  // resolves north cannot be performed by a headless XR session.
  const ran = await page.evaluate(async () => {
    let walked = 0;
    let local = { x: 0, y: 0, z: 0 };
    const north = (m) => 51.5007 + (m / 6371008.8) * 180 / Math.PI;

    const provider = {
      id: 'stub',
      locate: () => {
        const m = walked;
        local = { x: 0, y: 0, z: -m };
        walked += 30;
        return Promise.resolve({
          position: { lat: north(m), lon: -0.1246, h: 0 },
          headingDeg: 0,
          accuracy: { positionM: 3, headingDeg: 25 }
        });
      }
    };

    const store = {
      nearby: () => Promise.resolve([{
        id: 'p1', scene: 'rotary-phone', scale: 1, distance: 20,
        geopose: {
          position: { lat: north(60), lon: -0.1246, h: 0 },
          quaternion: { x: 0, y: 0, z: 0, w: 1 }
        }
      }]),
      place: (p) => Promise.resolve({ ...p, id: 'p2' })
    };

    const seen = [];
    const w = window.SpatialWorld.create({
      store, provider,
      config: { radiusM: 300, relocalizeAfterM: 25 },
      pose: () => local,
      onState: () => {},
      onPlacements: (list) => seen.push(list)
    });

    await w.start();
    const afterOne = w.state();
    await w.sample();
    const afterTwo = w.state();
    await w.refresh();

    const last = seen[seen.length - 1] || [];
    return { afterOne, afterTwo, count: last.length, z: last[0] && last[0].local.z };
  });

  check('one fix leaves it calibrating in the browser too', ran.afterOne === 'calibrating',
    ran.afterOne);
  check('a walked baseline makes it ready', ran.afterTwo === 'ready', ran.afterTwo);
  check('a placement to the north lands ahead of the walker',
    ran.count === 1 && Math.abs(ran.z + 60) < 1, `z=${ran.z && ran.z.toFixed(1)}`);
  check('world mode throws nothing', errors.length === 0, errors.join('; '));

  await page.screenshot({ path: join(SHOTS, '9-world.png') });
  await close();
}

async function testNFT() {
  const feed = await makeFakeCamera({
    chromium,
    markerPath: join(ROOT, 'data', 'poster.jpg'),
    out: join(SHOTS, 'fake-poster.y4m'),
    scale: 0.9, width: 1280, height: 960, fit: 'height'
  });

  const nft = await chromium.launch({
    args: [
      '--use-fake-device-for-media-stream',
      '--use-fake-ui-for-media-stream',
      `--use-file-for-fake-video-capture=${feed.path}`,
      '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'
    ]
  });

  try {
    const context = await nft.newContext({ permissions: ['camera'], viewport: { width: 900, height: 700 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));

    await page.goto(ORIGIN + '/?target=poster', { waitUntil: 'load' });
    await page.locator('#start').click();

    const locked = await page.waitForFunction(
      () => document.getElementById('state').dataset.state === 'locked',
      null, { timeout: 120000 }
    ).then(() => true).catch(() => false);
    check('tracker locks onto the poster', locked);

    check('the NFT build serves both tracking modes',
      await page.evaluate(() => !!document.querySelector('a-nft') && !!window.AFRAME.primitives.primitives['a-marker']));

    if (locked) {
      // The scene layers are authored in marker space; the poster is tracked
      // in millimetres from its bottom-left corner. If the wrapper transform
      // is wrong the content is behind the paper, or a metre off it.
      const placed = await page.evaluate(() => {
        const THREE = AFRAME.THREE;
        const marker = document.getElementById('marker');
        const shard = document.getElementById('shard');
        const box = new THREE.Box3().setFromObject(shard.object3D);
        const centre = box.getCenter(new THREE.Vector3());
        marker.object3D.worldToLocal(centre);
        return { centre: centre.toArray() };
      });

      // Poster is 1000x1414 px at 72 dpi: 352.8mm x 498.9mm, origin bottom-left.
      const dx = Math.abs(placed.centre[0] - 176.4);
      const dz = Math.abs(placed.centre[2] + 249.4);
      check('content is centred on the poster', dx < 30 && dz < 30,
        `off by ${dx.toFixed(0)}mm x ${dz.toFixed(0)}mm`);
      // NFT space has Y out of the page. A sign error here puts the whole
      // scene behind the paper, tracked perfectly and completely invisible.
      check('content sits in front of the paper, not behind it', placed.centre[1] > 0,
        'centre y=' + placed.centre[1].toFixed(0) + 'mm');

      await page.waitForTimeout(1200);
      await page.screenshot({ path: join(SHOTS, '8-nft.png') });
    }

    check('NFT mode throws nothing', errors.length === 0, errors.join('; '));
    await context.close();
  } finally {
    await nft.close();
  }
}

// The descriptor generator reads PNG input with the wrong row stride and
// trains, without complaint, on a sheared and tripled copy of the image —
// healthy feature counts, no matches, no error anywhere. The only way to
// catch it is to look at what it stored. Level 0 of the .iset is the image
// it actually saw; if that does not resemble the source, the dataset is junk.
async function testDescriptors() {
  const { readFile } = await import('node:fs/promises');
  const iset = await readFile(join(ROOT, 'data', 'nft', 'poster.iset'));

  const start = iset.indexOf(Buffer.from([0xff, 0xd8, 0xff, 0xe0]));
  const end = iset.indexOf(Buffer.from([0xff, 0xd9]), start);
  check('the iset holds a decodable image', start > 0 && end > start);
  if (start < 0 || end < 0) { return; }

  const level0 = iset.subarray(start, end + 2);
  const page = await browser.newPage();
  const stats = await page.evaluate(async ({ trained, source }) => {
    const load = async (b64, mime) => {
      const img = new Image();
      img.src = `data:${mime};base64,${b64}`;
      await img.decode();
      return img;
    };
    const grey = (img, w, h) => {
      const c = document.createElement('canvas');
      c.width = w; c.height = h;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0, w, h);
      const d = ctx.getImageData(0, 0, w, h).data;
      const g = new Float64Array(w * h);
      for (let i = 0; i < g.length; i++) {
        g[i] = 0.299 * d[i * 4] + 0.587 * d[i * 4 + 1] + 0.114 * d[i * 4 + 2];
      }
      return g;
    };

    const a = await load(trained, 'image/jpeg');
    const b = await load(source, 'image/jpeg');
    const W = 96;
    const H = 136;
    const ga = grey(a, W, H);
    const gb = grey(b, W, H);

    // Correlation, not equality: the trainer converts to greyscale and
    // recompresses, so the pixels differ but the picture should not.
    const mean = (g) => g.reduce((s, v) => s + v, 0) / g.length;
    const ma = mean(ga);
    const mb = mean(gb);
    let num = 0;
    let da = 0;
    let db = 0;
    for (let i = 0; i < ga.length; i++) {
      num += (ga[i] - ma) * (gb[i] - mb);
      da += (ga[i] - ma) ** 2;
      db += (gb[i] - mb) ** 2;
    }
    return { dims: [a.naturalWidth, a.naturalHeight], r: num / Math.sqrt(da * db) };
  }, {
    trained: level0.toString('base64'),
    source: (await readFile(join(ROOT, 'data', 'poster.jpg'))).toString('base64')
  });
  await page.close();

  check('the trained image has the source dimensions',
    stats.dims[0] === 1000 && stats.dims[1] === 1414, stats.dims.join('x'));
  check('the trainer saw the poster we gave it', stats.r > 0.9,
    'correlation ' + stats.r.toFixed(3));
}

async function testXR() {
  // Headless Chromium has no XR device, so the session itself cannot be
  // exercised here. What can: that the button is offered only where the
  // browser claims support, that the scene compiles with the right WebXR
  // and hit-test wiring, and that a refused session degrades to a message
  // rather than a dead screen.
  const stub = (requestSession) => `
    Object.defineProperty(navigator, 'xr', {
      configurable: true,
      value: {
        isSessionSupported: (m) => Promise.resolve(m === 'immersive-ar'),
        requestSession: ${requestSession}
      }
    });`;

  {
    const { page, close } = await open();
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });
    await page.waitForTimeout(600);
    check('markerless button is hidden without WebXR', await page.locator('#xr').isHidden());
    await close();
  }

  {
    // A session request that never settles holds the scene up long enough to
    // read it. Rejecting here would tear it down mid-assertion.
    const { page, close } = await open();
    await page.addInitScript(stub('() => new Promise(() => {})'));
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });

    const offered = await page.waitForSelector('#xr:not([hidden])', { timeout: 10000 })
      .then(() => true).catch(() => false);
    check('markerless button appears when the browser supports it', offered);
    if (!offered) { await close(); return; }

    await page.locator('#xr').click();
    await page.waitForFunction(() => document.getElementById('placeable'), null, { timeout: 45000 });
    await page.waitForTimeout(400);

    const w = await page.evaluate(() => {
      const scene = document.getElementById('scene');
      const placeable = document.getElementById('placeable');
      const hit = scene.getAttribute('ar-hit-test');
      return {
        // components parse into objects once initialised, strings before that
        hitTarget: typeof hit === 'string' ? hit : (hit && hit.target && hit.target.id),
        webxr: scene.getAttribute('webxr'),
        arjs: scene.hasAttribute('arjs'),
        arjsGlobal: typeof window.ARjs === 'undefined',
        scale: placeable.object3D.scale.toArray(),
        visible: placeable.object3D.visible,
        layers: placeable.children.length,
        overlay: !!document.getElementById('overlay')
      };
    });

    check('hit test targets the placeable group', /placeable/.test(String(w.hitTarget)),
      String(w.hitTarget));
    const required = (w.webxr && w.webxr.requiredFeatures) || [];
    check('the session asks for hit-test and local-floor',
      required.includes('hit-test') && required.includes('local-floor'), required.join(', '));
    check('the HUD is the dom-overlay element', w.overlay);
    check('markerless mode loads no AR.js', w.arjs === false && w.arjsGlobal);
    check('content is scaled to room size', Math.abs(w.scale[0] - 0.3) < 1e-6, w.scale.join(', '));
    check('content starts hidden until placed', w.visible === false);
    check('the manifest layers are in the placeable group', w.layers === 5, w.layers + ' layers');

    await page.screenshot({ path: join(SHOTS, '7-xr-scene.png') });
    await close();
  }

  {
    const { page, close } = await open();
    await page.addInitScript(stub("() => Promise.reject(new Error('no XR device in this test'))"));
    await page.goto(ORIGIN + '/', { waitUntil: 'load' });
    await page.waitForSelector('#xr:not([hidden])', { timeout: 10000 });
    await page.locator('#xr').click();

    const recovered = await page.waitForFunction(
      () => !document.getElementById('fault').hidden && !document.getElementById('gate').hidden,
      null, { timeout: 45000 }
    ).then(() => true).catch(() => false);
    check('a refused session returns to the gate with a message', recovered,
      (await page.locator('#fault').textContent().catch(() => '')).slice(0, 60));

    const torn = await page.evaluate(() => document.getElementById('scene') === null);
    check('the refused session leaves nothing behind', torn);

    // The two WebXR scenes are the only ones without `embedded`, so A-Frame
    // pins <html> with a-fullscreen: position fixed, body overflow hidden.
    // Teardown removes the scene before A-Frame can undo that, and the gate
    // is then perfectly rendered and completely unreachable.
    const pinned = await page.evaluate(() => {
      document.documentElement.classList.add('a-fullscreen');
      document.documentElement.style.overflow = 'hidden';
      document.body.style.height = '640px';
      return true;
    });
    await page.locator('#xr').click();
    await page.waitForFunction(() => !document.getElementById('gate').hidden,
      null, { timeout: 45000 }).catch(() => {});
    await page.waitForTimeout(1200);

    const chrome = await page.evaluate(() => ({
      full: document.documentElement.classList.contains('a-fullscreen'),
      htmlOverflow: document.documentElement.style.overflow,
      bodyHeight: document.body.style.height,
      scrollable: document.scrollingElement
        ? getComputedStyle(document.documentElement).position !== 'fixed' : true
    }));
    check('teardown un-pins the document', pinned && chrome.full === false);
    check('and clears the inline styles left on it',
      !chrome.htmlOverflow && !chrome.bodyHeight,
      `overflow='${chrome.htmlOverflow}' height='${chrome.bodyHeight}'`);
    check('so the page is not left fixed in place', chrome.scrollable);
    await close();
  }
}

async function testManifest() {
  // A second scene with no model at all: proves layers, lights and assets are
  // all driven by the manifest rather than hard-coded around one GLB.
  const { page, errors, close } = await open();
  await page.goto(ORIGIN + '/?preview&scene=beacon', { waitUntil: 'load' });

  await page.waitForFunction(() => document.getElementById('scene')?.hasLoaded,
    null, { timeout: 45000 });
  await page.waitForTimeout(1500);

  const built = await page.evaluate(() => {
    const scene = document.getElementById('scene');
    let meshes = 0;
    scene.object3D.traverse((o) => { if (o.isMesh) meshes++; });
    return {
      meshes,
      model: !!document.getElementById('shard'),
      assets: document.querySelectorAll('a-asset-item').length,
      lights: document.querySelectorAll('[light]').length
    };
  });

  check('a model-free scene builds', built.meshes > 4, built.meshes + ' meshes');
  check('it loads no model asset', built.model === false && built.assets === 0);
  check('its own lights are used', built.lights === 1, built.lights + ' light(s)');
  check('the beacon scene throws nothing', errors.length === 0, errors.join('; '));
  check('canvas has drawn something', await canvasHasInk(page));

  await page.screenshot({ path: join(SHOTS, '6-beacon.png') });
  await close();

  // A broken manifest must not take the app down with it.
  const { page: p2, close: c2 } = await open();
  await p2.route('**/content.json', (r) => r.fulfill({ status: 500, body: 'nope' }));
  await p2.goto(ORIGIN + '/?preview', { waitUntil: 'load' });
  const survived = await p2.waitForFunction(() => {
    const el = document.getElementById('shard');
    if (!el || !el.object3D) return false;
    let m = 0;
    el.object3D.traverse((o) => { if (o.isMesh) m++; });
    return m > 0;
  }, null, { timeout: 45000 }).then(() => true).catch(() => false);
  check('a failed manifest falls back instead of dying', survived);
  await c2();
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
    permissions: ['camera', 'geolocation'],
    // Somewhere with a known position, so a fix resolves rather than hanging.
    geolocation: { latitude: 51.5007, longitude: -0.1246, accuracy: 5 },
    viewport: { width: 900, height: 700 }
  });
  const page = await context.newPage();
  const errors = [];
  const offsite = [];
  page.on('pageerror', (e) => { errors.push(e.message); console.log('    [page error] ' + (e.stack || e.message)); });
  await page.addInitScript(() => {
    // Live WebGL contexts, and whether AR.js's worker is ever shut down.
    // Both are invisible from the page and both wedge a phone when they leak.
    window.__glLive = 0;
    window.__glMade = 0;
    const getContext = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
      const ctx = getContext.call(this, type, ...rest);
      if (/webgl/.test(type) && ctx) {
        window.__glMade++;
        window.__glLive++;
        this.addEventListener('webglcontextlost', () => { window.__glLive--; }, { once: true });
      }
      return ctx;
    };

    window.__workersMade = 0;
    window.__workersLive = 0;
    const RealWorker = window.Worker;
    function Counted(...args) {
      const worker = new RealWorker(...args);
      window.__workersMade++;
      window.__workersLive++;
      const terminate = worker.terminate.bind(worker);
      worker.terminate = function () { window.__workersLive--; return terminate(); };
      return worker;
    }
    Counted.prototype = RealWorker.prototype;
    window.Worker = Counted;
  });
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
