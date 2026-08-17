/* Marker One — level-1 WebAR proof of concept.
   Everything below runs client-side. There is no backend. */

(function () {
  'use strict';

  var gate     = document.getElementById('gate');
  var stage    = document.getElementById('stage');
  var slot     = document.getElementById('scene-slot');
  var startBtn = document.getElementById('start');
  var exitBtn  = document.getElementById('exit');
  var installBtn = document.getElementById('install');
  var fault    = document.getElementById('fault');
  var stateEl  = document.getElementById('state');
  var stateTxt = document.getElementById('state-text');
  var countEl  = document.getElementById('dial-count');

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
      fix: 'Cameras only open on HTTPS or localhost. Serve this over HTTPS and reload.'
    },
    {
      id: 'camera',
      arc: 'arc-camera',
      pass: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
      yes: 'ready',
      no: 'missing',
      fix: 'This browser does not expose a camera API. Use Chrome on Android or Safari on iOS.'
    },
    {
      id: 'webgl',
      arc: 'arc-webgl',
      pass: hasWebGL(),
      yes: 'ok',
      no: 'blocked',
      fix: 'WebGL is unavailable, usually from a hardware-acceleration setting or a low-power mode.'
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

  /* ── scene ──────────────────────────────────────────────────
     Built on demand so the camera permission prompt is tied to a
     tap, not to page load. */

  var SCENE = [
    '<a-scene id="scene" embedded',
    '  vr-mode-ui="enabled: false"',
    '  device-orientation-permission-ui="enabled: false"',
    '  renderer="antialias: true; alpha: true; colorManagement: true; logarithmicDepthBuffer: true"',
    '  arjs="sourceType: webcam; detectionMode: mono; patternRatio: 0.5; trackingMethod: best;',
    '        cameraParametersUrl: data/camera_para.dat; debugUIEnabled: false;',
    '        sourceWidth: 1280; sourceHeight: 960; displayWidth: 1280; displayHeight: 960">',

    '  <a-assets timeout="30000">',
    '    <a-asset-item id="phone" src="assets/rotary-phone.glb"></a-asset-item>',
    '  </a-assets>',

    '  <a-entity light="type: ambient; color: #8F8CC0; intensity: 0.85"></a-entity>',
    '  <a-entity light="type: directional; color: #FFFFFF; intensity: 0.75" position="1 2 1"></a-entity>',
    '  <a-entity light="type: point; color: #6BE3E8; intensity: 0.9; distance: 4" position="0 1.2 0"></a-entity>',

    // Note: marker attributes are kebab-case. smoothCount would silently
    // not map, and the shard would jitter with no obvious cause.
    '  <a-marker id="marker" type="pattern" url="data/patt.hiro"',
    '     smooth="true" smooth-count="8" smooth-tolerance="0.01" smooth-threshold="4">',

    // ground halo — reads as the object's footprint on the marker
    '    <a-entity position="0 0.012 0" rotation="-90 0 0"',
    '      geometry="primitive: ring; radiusInner: 0.62; radiusOuter: 0.74; segmentsTheta: 64"',
    '      material="color: #6BE3E8; shader: flat; opacity: 0.55; transparent: true; side: double"',
    '      animation="property: scale; to: 1.14 1.14 1.14; dir: alternate; loop: true; dur: 2600; easing: easeInOutSine">',
    '    </a-entity>',

    // orbit ring
    '    <a-entity position="0 0.6 0" rotation="72 0 18"',
    '      geometry="primitive: torus; radius: 0.62; radiusTubular: 0.008; segmentsRadial: 12; segmentsTubular: 64"',
    '      material="color: #6BE3E8; shader: flat; opacity: 0.75; transparent: true"',
    '      animation="property: rotation; to: 72 360 18; loop: true; dur: 11000; easing: linear">',
    '    </a-entity>',

    // wireframe shell
    '    <a-entity position="0 0.6 0"',
    '      geometry="primitive: octahedron; radius: 0.52"',
    '      material="color: #A9F0F3; wireframe: true; opacity: 0.32; transparent: true; shader: flat"',
    '      animation="property: rotation; to: 0 -360 0; loop: true; dur: 16000; easing: linear">',
    '    </a-entity>',

    // the model — centred on its origin and ~1 unit tall, so at scale 0.35
    // a Y of 0.18 rests it on the marker instead of half-sinking it
    '    <a-entity id="shard" position="0 0.18 0" scale="0.35 0.35 0.35"',
    '      gltf-model="#phone"',
    '      animation="property: rotation; to: 0 360 0; loop: true; dur: 14000; easing: linear">',
    '    </a-entity>',

    // three motes on a shared pivot
    '    <a-entity position="0 0.6 0" animation="property: rotation; to: 0 360 0; loop: true; dur: 6000; easing: linear">',
    '      <a-entity position="0.78 0.06 0" geometry="primitive: sphere; radius: 0.035; segmentsWidth: 16; segmentsHeight: 12" material="color: #FFFFFF; shader: flat; opacity: 0.9; transparent: true"></a-entity>',
    '      <a-entity position="-0.39 -0.14 0.68" geometry="primitive: sphere; radius: 0.028; segmentsWidth: 16; segmentsHeight: 12" material="color: #6BE3E8; shader: flat; opacity: 0.85; transparent: true"></a-entity>',
    '      <a-entity position="-0.39 0.2 -0.68" geometry="primitive: sphere; radius: 0.024; segmentsWidth: 16; segmentsHeight: 12" material="color: #A9F0F3; shader: flat; opacity: 0.8; transparent: true"></a-entity>',
    '    </a-entity>',

    '  </a-marker>',
    '  <a-entity camera></a-entity>',
    '</a-scene>'
  ].join('\n');

  function setState(s) {
    stateEl.setAttribute('data-state', s);
    stateTxt.textContent = s === 'locked' ? 'Marker locked' : 'Seeking marker';
  }

  function start() {
    startBtn.disabled = true;
    startBtn.textContent = 'Opening camera…';
    fault.hidden = true;

    // Probe first, then build the scene. Some devices — iOS especially —
    // will not hand the camera to two getUserMedia calls at once, so the
    // probe track is released before AR.js opens its own stream.
    navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } })
      .then(function (stream) {
        stream.getTracks().forEach(function (t) { t.stop(); });
        return new Promise(function (r) { setTimeout(r, 120); });
      })
      .then(function () {
        gate.hidden = true;
        stage.hidden = false;
        setState('seeking');
        slot.innerHTML = SCENE;

        var marker = document.getElementById('marker');
        marker.addEventListener('markerFound', function () { setState('locked'); });
        marker.addEventListener('markerLost',  function () { setState('seeking'); });

        startBtn.textContent = 'Start camera';
      })
      .catch(onCameraError);
  }

  function onCameraError(err) {
    var name = (err && err.name) || '';
    var msg = 'Camera did not open. Check that no other app or tab is holding it, then reload.';
    if (name === 'NotAllowedError') {
      msg = 'Camera access was refused. Allow the camera for this site in your browser settings, then reload.';
    } else if (name === 'NotFoundError') {
      msg = 'No camera was found on this device.';
    }
    stop();
    fault.textContent = msg;
    fault.hidden = false;
    startBtn.disabled = false;
    startBtn.textContent = 'Start camera';
  }

  function stop() {
    // Tear the scene down entirely: A-Frame keeps the video track alive
    // otherwise, and the camera light stays on.
    Array.prototype.forEach.call(document.querySelectorAll('video'), function (video) {
      if (video.srcObject) {
        video.srcObject.getTracks().forEach(function (t) { t.stop(); });
        video.srcObject = null;
      }
      if (video.parentNode) { video.parentNode.removeChild(video); }
    });
    slot.innerHTML = '';
    stage.hidden = true;
    gate.hidden = false;
    startBtn.disabled = false;
    startBtn.textContent = 'Start camera';
  }

  startBtn.addEventListener('click', start);
  exitBtn.addEventListener('click', stop);

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
})();
