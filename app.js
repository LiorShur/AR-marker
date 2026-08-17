/* Marker One — level-1 WebAR proof of concept.
   Everything below runs client-side. There is no backend. */

(function () {
  'use strict';

  var gate       = document.getElementById('gate');
  var stage      = document.getElementById('stage');
  var slot       = document.getElementById('scene-slot');
  var startBtn   = document.getElementById('start');
  var previewBtn = document.getElementById('preview');
  var exitBtn    = document.getElementById('exit');
  var shootBtn   = document.getElementById('shoot');
  var installBtn = document.getElementById('install');
  var flashEl    = document.getElementById('flash');
  var fault      = document.getElementById('fault');
  var stateEl    = document.getElementById('state');
  var stateTxt   = document.getElementById('state-text');
  var countEl    = document.getElementById('dial-count');

  var mode = null;      // 'ar' | 'preview' | null
  var wakeLock = null;

  /* ── capability checks ──────────────────────────────────────
     Three real checks, drawn as three arcs. Any failure explains
     itself rather than leaving a dead button. */

  function hasWebGL() {
    try {
      var c = document.createElement('canvas');
      return !!(window.WebGLRenderingContext &&
        (c.getContext('webgl') || c.getContext('experimental-webgl')));
    } catch (e) { return false; }
  }

  var checks = [
    {
      id: 'secure',
      arc: 'arc-secure',
      pass: window.isSecureContext === true,
      yes: 'https',
      no: 'http',
      fix: 'Cameras only open on HTTPS or localhost. Serve this over HTTPS and reload. Preview mode still works.'
    },
    {
      id: 'camera',
      arc: 'arc-camera',
      pass: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
      yes: 'ready',
      no: 'missing',
      fix: 'This browser does not expose a camera API. Use Chrome on Android or Safari on iOS. Preview mode still works.'
    },
    {
      id: 'webgl',
      arc: 'arc-webgl',
      pass: hasWebGL(),
      yes: 'ok',
      no: 'blocked',
      fix: 'WebGL is unavailable, usually from a hardware-acceleration setting or a low-power mode. Nothing here can render without it.'
    }
  ];

  var passed = 0;
  var firstFault = null;

  checks.forEach(function (c) {
    var li = document.getElementById('chk-' + c.id);
    var arc = document.getElementById(c.arc);
    var r = parseFloat(arc.getAttribute('r'));
    var circ = 2 * Math.PI * r;

    li.classList.add(c.pass ? 'ok' : 'no');
    li.querySelector('.checks__val').textContent = c.pass ? c.yes : c.no;

    // Fill the arc most of the way round; the gap keeps the three rings readable.
    arc.style.strokeDasharray = (c.pass ? circ * 0.86 : circ * 0.14) + ' ' + circ;
    arc.classList.add(c.pass ? 'is-on' : 'is-off');

    if (c.pass) { passed++; } else if (!firstFault) { firstFault = c.fix; }
  });

  countEl.textContent = passed + '/3';

  if (passed === checks.length) {
    startBtn.disabled = false;
  } else {
    fault.textContent = firstFault;
    fault.hidden = false;
  }

  // Preview needs a GPU and nothing else — no camera, no secure context.
  // It is the fallback that keeps a failed gate from being a dead end.
  previewBtn.disabled = !hasWebGL();

  /* ── vendor bundle ──────────────────────────────────────────
     A-Frame and AR.js together are ~3 MB of parse work. Loading them
     at page load blocks the gate from painting on exactly the slow
     phones this needs to work on. Fetch them on the tap instead —
     AR.js registers against AFRAME, so the order is load-bearing. */

  var vendorReady = { core: null, ar: null };

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = src;
      s.async = false;
      s.onload = resolve;
      s.onerror = function () { reject(new Error('Failed to load ' + src)); };
      document.head.appendChild(s);
    });
  }

  function loadCore() {
    if (!vendorReady.core) {
      vendorReady.core = loadScript('vendor/aframe.min.js')
        .then(registerComponents)
        .catch(function (err) { vendorReady.core = null; throw err; });
    }
    return vendorReady.core;
  }

  // AR.js is loaded only for AR. Left to itself its `arjs` system initialises
  // on any scene and fetches its own camera_para.dat from a GitHub Pages URL —
  // which fails offline, throws, and breaks the promise that nothing here
  // touches a CDN. Preview gets A-Frame and nothing else: half the bytes.
  function loadVendor() {
    if (!vendorReady.ar) {
      vendorReady.ar = loadCore()
        .then(function () { return loadScript('vendor/aframe-ar.js'); })
        .catch(function (err) { vendorReady.ar = null; throw err; });
    }
    return vendorReady.ar;
  }

  /* ── scene content ──────────────────────────────────────────
     Marker space is one unit per marker width, Y up, origin at the
     marker centre. The same subtree is used by both modes, so what
     preview shows is what the marker carries. */

  var CONTENT = [
    // ground halo — reads as the object's footprint on the marker
    '<a-entity position="0 0.012 0" rotation="-90 0 0"',
    '  geometry="primitive: ring; radiusInner: 0.62; radiusOuter: 0.74; segmentsTheta: 64"',
    '  material="color: #6BE3E8; shader: flat; opacity: 0.55; transparent: true; side: double"',
    '  animation="property: scale; to: 1.14 1.14 1.14; dir: alternate; loop: true; dur: 2600; easing: easeInOutSine">',
    '</a-entity>',

    // orbit ring
    '<a-entity position="0 0.6 0" rotation="72 0 18"',
    '  geometry="primitive: torus; radius: 0.62; radiusTubular: 0.008; segmentsRadial: 12; segmentsTubular: 64"',
    '  material="color: #6BE3E8; shader: flat; opacity: 0.75; transparent: true"',
    '  animation="property: rotation; to: 72 360 18; loop: true; dur: 11000; easing: linear">',
    '</a-entity>',

    // wireframe shell
    '<a-entity position="0 0.6 0"',
    '  geometry="primitive: octahedron; radius: 0.52"',
    '  material="color: #A9F0F3; wireframe: true; opacity: 0.32; transparent: true; shader: flat"',
    '  animation="property: rotation; to: 0 -360 0; loop: true; dur: 16000; easing: linear">',
    '</a-entity>',

    // the model — centred on its origin and ~1 unit tall, so at scale 0.35
    // a Y of 0.18 rests it on the marker instead of half-sinking it
    '<a-entity id="shard" position="0 0.18 0" scale="0.35 0.35 0.35"',
    '  gltf-model="#phone"',
    '  animation="property: rotation; to: 0 360 0; loop: true; dur: 14000; easing: linear">',
    '</a-entity>',

    // three motes on a shared pivot
    '<a-entity position="0 0.6 0" animation="property: rotation; to: 0 360 0; loop: true; dur: 6000; easing: linear">',
    '  <a-entity position="0.78 0.06 0" geometry="primitive: sphere; radius: 0.035; segmentsWidth: 16; segmentsHeight: 12" material="color: #FFFFFF; shader: flat; opacity: 0.9; transparent: true"></a-entity>',
    '  <a-entity position="-0.39 -0.14 0.68" geometry="primitive: sphere; radius: 0.028; segmentsWidth: 16; segmentsHeight: 12" material="color: #6BE3E8; shader: flat; opacity: 0.85; transparent: true"></a-entity>',
    '  <a-entity position="-0.39 0.2 -0.68" geometry="primitive: sphere; radius: 0.024; segmentsWidth: 16; segmentsHeight: 12" material="color: #A9F0F3; shader: flat; opacity: 0.8; transparent: true"></a-entity>',
    '</a-entity>'
  ].join('\n');

  var LIGHTS = [
    '<a-entity light="type: ambient; color: #8F8CC0; intensity: 0.85"></a-entity>',
    '<a-entity light="type: directional; color: #FFFFFF; intensity: 0.75" position="1 2 1"></a-entity>',
    '<a-entity light="type: point; color: #6BE3E8; intensity: 0.9; distance: 4" position="0 1.2 0"></a-entity>'
  ].join('\n');

  // preserveDrawingBuffer is deliberately absent: A-Frame does not forward it
  // to the WebGLRenderer constructor, so setting it here would only look like
  // it worked. capture() re-renders on demand instead.
  var RENDERER = 'renderer="antialias: true; alpha: true; colorManagement: true; ' +
                 'logarithmicDepthBuffer: true"';

  var AR_SCENE = [
    '<a-scene id="scene" embedded',
    '  vr-mode-ui="enabled: false"',
    '  device-orientation-permission-ui="enabled: false"',
    '  ' + RENDERER,
    '  arjs="sourceType: webcam; detectionMode: mono; patternRatio: 0.5; trackingMethod: best;',
    '        cameraParametersUrl: data/camera_para.dat; debugUIEnabled: false;',
    '        sourceWidth: 1280; sourceHeight: 960; displayWidth: 1280; displayHeight: 960">',

    '  <a-assets timeout="30000">',
    '    <a-asset-item id="phone" src="assets/rotary-phone.glb"></a-asset-item>',
    '  </a-assets>',

    LIGHTS,

    // Note: marker attributes are kebab-case. smoothCount would silently
    // not map, and the object would jitter with no obvious cause.
    '  <a-marker id="marker" type="pattern" url="data/patt.hiro"',
    '     smooth="true" smooth-count="8" smooth-tolerance="0.01" smooth-threshold="4">',
    CONTENT,
    '  </a-marker>',
    '  <a-entity camera look-controls="enabled: false" wasd-controls="enabled: false"></a-entity>',
    '</a-scene>'
  ].join('\n');

  // Same subtree, no camera feed and no tracker. The marker itself is laid
  // in as a floor plane so the preview reads as the real thing at rest.
  var PREVIEW_SCENE = [
    '<a-scene id="scene" embedded',
    '  vr-mode-ui="enabled: false"',
    '  device-orientation-permission-ui="enabled: false"',
    '  ' + RENDERER + '>',

    '  <a-assets timeout="30000">',
    '    <a-asset-item id="phone" src="assets/rotary-phone.glb"></a-asset-item>',
    '    <img id="markerimg" src="data/marker-hiro.png">',
    '  </a-assets>',

    '  <a-sky color="#0B0A14"></a-sky>',
    '  <a-entity geometry="primitive: plane; width: 1; height: 1" rotation="-90 0 0"',
    '    material="src: #markerimg; shader: flat; side: double"></a-entity>',
    '  <a-entity geometry="primitive: plane; width: 12; height: 12" rotation="-90 0 0" position="0 -0.002 0"',
    '    material="color: #17162B; shader: flat"></a-entity>',

    LIGHTS,
    CONTENT,

    '  <a-entity id="rig" orbit="radius: 2.6; theta: 22; phi: 24; target: 0 0.5 0">',
    '    <a-entity camera="fov: 55" look-controls="enabled: false" wasd-controls="enabled: false"></a-entity>',
    '  </a-entity>',
    '</a-scene>'
  ].join('\n');

  /* ── orbit control (preview only) ───────────────────────────
     A-Frame ships no orbit camera. This is the smallest thing that
     behaves: drag to swing, pinch or wheel to dolly, damped. */

  var componentsRegistered = false;

  function registerComponents() {
    if (componentsRegistered || !window.AFRAME) { return; }
    componentsRegistered = true;

    AFRAME.registerComponent('orbit', {
      schema: {
        radius: { default: 2.6 },
        theta:  { default: 22 },   // elevation, degrees
        phi:    { default: 24 },   // azimuth, degrees
        target: { type: 'vec3', default: { x: 0, y: 0.5, z: 0 } }
      },

      init: function () {
        var d = this.data;
        this.radius = d.radius;
        this.theta = d.theta;
        this.phi = d.phi;
        this.want = { radius: d.radius, theta: d.theta, phi: d.phi };
        this.drag = null;
        this.pinch = null;

        var canvas = this.el.sceneEl.canvas;
        if (!canvas) {
          this.el.sceneEl.addEventListener('render-target-loaded', this.init.bind(this), { once: true });
          return;
        }
        this.canvas = canvas;
        this.onDown = this.onDown.bind(this);
        this.onMove = this.onMove.bind(this);
        this.onUp = this.onUp.bind(this);
        this.onWheel = this.onWheel.bind(this);

        canvas.addEventListener('pointerdown', this.onDown);
        window.addEventListener('pointermove', this.onMove);
        window.addEventListener('pointerup', this.onUp);
        window.addEventListener('pointercancel', this.onUp);
        canvas.addEventListener('wheel', this.onWheel, { passive: false });
        canvas.addEventListener('touchstart', this.onTouch.bind(this), { passive: true });
        canvas.addEventListener('touchmove', this.onTouchMove.bind(this), { passive: false });
        canvas.addEventListener('touchend', function () { this.pinch = null; }.bind(this));

        this.apply();
      },

      remove: function () {
        if (!this.canvas) { return; }
        this.canvas.removeEventListener('pointerdown', this.onDown);
        window.removeEventListener('pointermove', this.onMove);
        window.removeEventListener('pointerup', this.onUp);
        window.removeEventListener('pointercancel', this.onUp);
        this.canvas.removeEventListener('wheel', this.onWheel);
      },

      onDown: function (e) { this.drag = { x: e.clientX, y: e.clientY }; },
      onUp: function () { this.drag = null; },

      onMove: function (e) {
        if (!this.drag) { return; }
        var w = this.want;
        w.phi -= (e.clientX - this.drag.x) * 0.4;
        w.theta = clamp(w.theta + (e.clientY - this.drag.y) * 0.3, -12, 82);
        this.drag = { x: e.clientX, y: e.clientY };
      },

      onWheel: function (e) {
        e.preventDefault();
        this.want.radius = clamp(this.want.radius * (1 + e.deltaY * 0.0012), 1.1, 8);
      },

      onTouch: function (e) {
        if (e.touches.length === 2) { this.pinch = spread(e.touches); }
      },

      onTouchMove: function (e) {
        if (e.touches.length !== 2 || !this.pinch) { return; }
        e.preventDefault();
        var now = spread(e.touches);
        this.want.radius = clamp(this.want.radius * (this.pinch / now), 1.1, 8);
        this.pinch = now;
      },

      tick: function (time, delta) {
        // Critically damped enough to feel weighted without feeling laggy.
        var k = Math.min(1, (delta || 16) / 120);
        var moved = false;
        ['radius', 'theta', 'phi'].forEach(function (key) {
          var diff = this.want[key] - this[key];
          if (Math.abs(diff) > 0.0005) { this[key] += diff * k; moved = true; }
        }, this);
        if (moved) { this.apply(); }
      },

      apply: function () {
        var t = this.data.target;
        var th = this.theta * Math.PI / 180;
        var ph = this.phi * Math.PI / 180;
        var r = this.radius;
        var o = this.el.object3D;
        o.position.set(
          t.x + r * Math.cos(th) * Math.sin(ph),
          t.y + r * Math.sin(th),
          t.z + r * Math.cos(th) * Math.cos(ph)
        );
        o.rotation.order = 'YXZ';
        o.rotation.set(-th, ph, 0);
      }
    });
  }

  function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }

  function spread(touches) {
    var dx = touches[0].clientX - touches[1].clientX;
    var dy = touches[0].clientY - touches[1].clientY;
    return Math.max(1, Math.sqrt(dx * dx + dy * dy));
  }

  /* ── state readout ──────────────────────────────────────── */

  function setState(s, label) {
    stateEl.setAttribute('data-state', s);
    stateTxt.textContent = label || (s === 'locked' ? 'Marker locked' : 'Seeking marker');
  }

  /* ── run ────────────────────────────────────────────────── */

  function busy(btn, text) {
    btn.dataset.label = btn.dataset.label || btn.textContent;
    btn.disabled = true;
    btn.textContent = text;
  }

  function idle(btn) {
    btn.disabled = false;
    if (btn.dataset.label) { btn.textContent = btn.dataset.label; }
  }

  function startAR() {
    busy(startBtn, 'Loading engine…');
    fault.hidden = true;

    // Probe first, then build the scene. Some devices — iOS especially —
    // will not hand the camera to two getUserMedia calls at once, so the
    // probe track is released before AR.js opens its own stream.
    loadVendor()
      .then(function () {
        busy(startBtn, 'Opening camera…');
        return navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
      })
      .then(function (stream) {
        stream.getTracks().forEach(function (t) { t.stop(); });
        return new Promise(function (r) { setTimeout(r, 120); });
      })
      .then(function () {
        mode = 'ar';
        enterStage();
        setState('seeking');
        slot.innerHTML = AR_SCENE;

        var marker = document.getElementById('marker');
        marker.addEventListener('markerFound', function () { setState('locked'); });
        marker.addEventListener('markerLost',  function () { setState('seeking'); });

        var scene = document.getElementById('scene');
        scene.addEventListener('loaded', syncFeed);
        window.addEventListener('resize', syncFeed);
        window.addEventListener('orientationchange', syncFeed);
        syncFeed();

        idle(startBtn);
        keepAwake();
      })
      .catch(onStartError);
  }

  function startPreview() {
    busy(previewBtn, 'Loading engine…');
    fault.hidden = true;

    loadCore()
      .then(function () {
        mode = 'preview';
        enterStage();
        setState('preview', 'Preview — drag to orbit');
        slot.innerHTML = PREVIEW_SCENE;
        idle(previewBtn);
      })
      .catch(onStartError);
  }

  /* ── overlay alignment ──────────────────────────────────────
     AR.js letterboxes the camera <video> to cover the viewport and gives
     the camera a projection matrix built from the 4:3 calibration file.
     A-Frame, meanwhile, sizes an embedded scene's canvas to its container —
     the viewport. On a 4:3 desktop window the mismatch is a few percent; on
     a tall phone it is enormous, and the overlay renders a stretched ellipse
     nowhere near the marker it is tracking.

     The canvas has to be the same box as the video, not the same box as the
     viewport. A-Frame's resize handler is debounced, so this re-runs a
     couple of times after each trigger to land last. */

  var syncTimers = [];

  function syncFeed() {
    syncTimers.forEach(clearTimeout);
    syncTimers = [80, 400, 1000].map(function (ms) { return setTimeout(applyFeedSize, ms); });
    applyFeedSize();
  }

  function applyFeedSize() {
    if (mode !== 'ar') { return; }
    var scene = document.getElementById('scene');
    var video = document.querySelector('video');
    if (!scene || !scene.canvas || !video || !video.videoWidth) { return; }

    var w = parseFloat(video.style.width);
    var h = parseFloat(video.style.height);
    if (!w || !h) { return; }

    // Mirror the video's own box exactly — same size, same margins, same
    // origin. AR.js expresses the letterbox offset as a negative margin, so
    // copying `left` instead of `marginLeft` would apply the offset twice.
    var css = scene.canvas.style;
    css.position = 'absolute';
    css.top = '0px';
    css.left = '0px';
    css.width = video.style.width;
    css.height = video.style.height;
    css.marginLeft = video.style.marginLeft || '0px';
    css.marginTop = video.style.marginTop || '0px';

    // updateStyle: false — the styles above are the authority, and letting
    // three.js rewrite them would undo the letterbox.
    if (scene.renderer) { scene.renderer.setSize(w, h, false); }
  }

  function enterStage() {
    exitBtn.setAttribute('aria-label', mode === 'preview' ? 'Exit preview' : 'Stop camera');
    gate.hidden = true;
    stage.hidden = false;
    document.body.classList.add('is-running');
  }

  function onStartError(err) {
    var name = (err && err.name) || '';
    var msg = 'Camera did not open. Check that no other app or tab is holding it, then reload.';
    if (name === 'NotAllowedError' || name === 'SecurityError') {
      msg = 'Camera access was refused. Allow the camera for this site in your browser settings, then reload.';
    } else if (name === 'NotFoundError' || name === 'OverconstrainedError') {
      msg = 'No rear-facing camera was found on this device. Preview mode still works.';
    } else if (err && /Failed to load/.test(err.message || '')) {
      msg = err.message + ' — check the connection and reload.';
    }
    stop();
    fault.textContent = msg;
    fault.hidden = false;
  }

  function stop() {
    releaseWake();
    syncTimers.forEach(clearTimeout);
    syncTimers = [];
    window.removeEventListener('resize', syncFeed);
    window.removeEventListener('orientationchange', syncFeed);

    // Tear the scene down entirely: A-Frame keeps the video track alive
    // otherwise, and the camera light stays on. Dispose the renderer too —
    // browsers cap live WebGL contexts, and start/stop cycles otherwise
    // leak one each time until the whole page goes black.
    var scene = document.getElementById('scene');
    if (scene) {
      try {
        if (scene.renderer) { scene.renderer.dispose(); }
        if (scene.pause) { scene.pause(); }
      } catch (e) { /* teardown is best-effort */ }
    }

    Array.prototype.forEach.call(document.querySelectorAll('video'), function (video) {
      if (video.srcObject) {
        video.srcObject.getTracks().forEach(function (t) { t.stop(); });
        video.srcObject = null;
      }
      if (video.parentNode) { video.parentNode.removeChild(video); }
    });

    slot.innerHTML = '';
    mode = null;
    stage.hidden = true;
    gate.hidden = false;
    document.body.classList.remove('is-running');
    idle(startBtn);
    idle(previewBtn);
  }

  /* ── photo ──────────────────────────────────────────────────
     The camera feed is a plain <video> behind a transparent WebGL
     canvas, so the composite has to be redone by hand: video first,
     canvas over it, both scaled to the canvas the user is looking at. */

  function capture() {
    var scene = document.getElementById('scene');
    var gl = scene && scene.canvas;
    if (!gl) { return; }

    // Without preserveDrawingBuffer the colour buffer is undefined once the
    // frame has been composited, and reading it back gives an empty image.
    // Re-rendering here puts a known frame in the buffer that survives long
    // enough to be copied, as long as the copy happens in this same task.
    try {
      if (scene.renderer && scene.camera) {
        scene.renderer.render(scene.object3D, scene.camera);
      }
    } catch (e) { /* fall through and copy whatever is there */ }

    var out = document.createElement('canvas');
    out.width = gl.width;
    out.height = gl.height;
    var ctx = out.getContext('2d');

    var video = document.querySelector('video');
    if (video && video.videoWidth) {
      // AR.js letterboxes the feed to cover the viewport; mirror that here
      // or the still comes out framed differently from the live view.
      var vr = video.videoWidth / video.videoHeight;
      var cr = out.width / out.height;
      var w = vr > cr ? out.height * vr : out.width;
      var h = vr > cr ? out.height : out.width / vr;
      ctx.drawImage(video, (out.width - w) / 2, (out.height - h) / 2, w, h);
    } else {
      ctx.fillStyle = '#0B0A14';
      ctx.fillRect(0, 0, out.width, out.height);
    }

    ctx.drawImage(gl, 0, 0, out.width, out.height);

    flash();
    out.toBlob(function (blob) {
      if (!blob) { return; }
      var file = new File([blob], 'marker-one.png', { type: 'image/png' });
      if (navigator.canShare && navigator.canShare({ files: [file] })) {
        navigator.share({ files: [file], title: 'Marker One' }).catch(function () { download(blob); });
      } else {
        download(blob);
      }
    }, 'image/png');
  }

  function download(blob) {
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = 'marker-one.png';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 4000);
  }

  function flash() {
    flashEl.hidden = false;
    flashEl.classList.add('is-on');
    setTimeout(function () {
      flashEl.classList.remove('is-on');
      flashEl.hidden = true;
    }, 280);
  }

  /* ── screen wake ────────────────────────────────────────────
     Holding a phone still at a marker is exactly the posture that
     trips the idle timer. */

  function keepAwake() {
    if (!navigator.wakeLock) { return; }
    navigator.wakeLock.request('screen')
      .then(function (l) { wakeLock = l; })
      .catch(function () { /* not critical */ });
  }

  function releaseWake() {
    if (wakeLock) { wakeLock.release().catch(function () {}); wakeLock = null; }
  }

  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible' && mode === 'ar' && !wakeLock) { keepAwake(); }
  });

  startBtn.addEventListener('click', startAR);
  previewBtn.addEventListener('click', startPreview);
  exitBtn.addEventListener('click', stop);
  shootBtn.addEventListener('click', capture);

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && mode) { stop(); }
  });

  /* ── install prompt ─────────────────────────────────────── */
  var deferred = null;
  window.addEventListener('beforeinstallprompt', function (e) {
    e.preventDefault();
    deferred = e;
    installBtn.hidden = false;
  });
  installBtn.addEventListener('click', function () {
    if (!deferred) return;
    deferred.prompt();
    deferred = null;
    installBtn.hidden = true;
  });

  /* ── service worker ─────────────────────────────────────── */
  // ?reset on the URL tears out the worker and every cache, then reloads
  // clean. The escape hatch for "my fix didn't deploy".
  if (location.search.indexOf('reset') !== -1 && 'serviceWorker' in navigator) {
    navigator.serviceWorker.getRegistrations().then(function (rs) {
      return Promise.all(rs.map(function (r) { return r.unregister(); }));
    }).then(function () {
      return caches.keys().then(function (ks) {
        return Promise.all(ks.map(function (k) { return caches.delete(k); }));
      });
    }).then(function () { location.replace(location.pathname); });
    return;
  }

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', function () {
      navigator.serviceWorker.register('sw.js').catch(function () { /* offline is optional */ });
    });
  }

  // ?preview on the URL jumps straight in, for a link that needs no tap.
  if (location.search.indexOf('preview') !== -1 && !previewBtn.disabled) {
    startPreview();
  }
})();
