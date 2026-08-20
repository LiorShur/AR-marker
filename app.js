/* Marker One — level-1 WebAR proof of concept.
   Everything below runs client-side. There is no backend. */

(function () {
  'use strict';

  /* Bumped with the ?v= on every asset in index.html and the cache name in
     sw.js, and checked against them by scripts/check-precache.mjs. Its only
     job is to answer "am I running the code I just deployed", which during a
     week of deploy-and-walk-outside is a question worth being able to answer
     in one glance rather than by bisecting behaviour. */
  var BUILD = '9';

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
  var picker     = document.getElementById('picker');
  var xrBtn      = document.getElementById('xr');
  var worldBtn   = document.getElementById('world');
  var listBtn    = document.getElementById('list');
  var listCount  = document.getElementById('list-count');
  var nearbyEl   = document.getElementById('nearby');
  var nearbyList = document.getElementById('nearby-list');
  var nearbyEmpty = document.getElementById('nearby-empty');
  var nearbyPlace = document.getElementById('nearby-place');
  var overlayEl  = document.getElementById('overlay');
  var buildEl    = document.getElementById('build');
  var sheetLink  = document.getElementById('sheet');

  var mode = null;      // 'ar' | 'preview' | null
  var wakeLock = null;

  var params = new URLSearchParams(location.search);
  var chosenTarget = params.get('target');
  var chosenScene = params.get('scene');

  /* ── capability checks ──────────────────────────────────────
     Three real checks, drawn as three arcs. Any failure explains
     itself rather than leaving a dead button. */

  function hasWebGL() {
    try {
      if (!window.WebGLRenderingContext) { return false; }

      var canvas = document.createElement('canvas');
      var gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
      if (!gl) { return false; }

      // Hand it straight back. A browser allows only a handful of live
      // contexts — as few as four on a modest phone — and holding one for the
      // rest of the session to answer a question already answered is a
      // context the AR view then cannot have.
      var lose = gl.getExtension('WEBGL_lose_context');
      if (lose) { lose.loseContext(); }
      return true;
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
  //
  // The NFT build is used for both tracking modes: it registers a-marker as
  // well as a-nft and is 245 bytes larger than the marker-only build, so
  // shipping two would cost 1.6 MB to save nothing.
  function loadVendor() {
    trackWorkers();
    if (!vendorReady.ar) {
      vendorReady.ar = loadCore()
        .then(function () { return loadScript('vendor/aframe-ar-nft.js'); })
        .catch(function (err) { vendorReady.ar = null; throw err; });
    }
    return vendorReady.ar;
  }

  /* ── content ────────────────────────────────────────────────
     What the marker carries lives in content.json, not here. A scene is
     a list of typed layers; this module turns each into A-Frame markup.
     Marker space is one unit per marker width, Y up, origin at the marker
     centre, so every number in the manifest is a fraction of the printed
     marker's width and stays right whatever size it is printed at. */

  // Enough of the manifest to run if content.json is missing or malformed —
  // the app is offline-first, and a failed fetch must not be fatal.
  var FALLBACK = {
    assets: { phone: 'assets/rotary-phone.glb' },
    'default': 'hiro',
    targets: [{
      id: 'hiro', label: 'Hiro marker', tracking: 'pattern',
      pattern: 'data/patt.hiro', sheet: 'marker.html', scene: 'fallback',
      smooth: { count: 8, tolerance: 0.01, threshold: 4 }
    }],
    scenes: {
      fallback: {
        label: 'Rotary phone',
        lights: [{ type: 'ambient', color: '#8F8CC0', intensity: 0.85 },
                 { type: 'directional', color: '#FFFFFF', intensity: 0.75, position: [1, 2, 1] }],
        layers: [{ type: 'model', asset: 'phone', position: [0, 0.18, 0], scale: 0.35, spin: 14000 }]
      }
    }
  };

  var manifest = null;

  function loadManifest() {
    if (manifest) { return Promise.resolve(manifest); }
    return fetch('content.json')
      .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error(r.status)); })
      .catch(function () { return FALLBACK; })
      .then(function (m) {
        manifest = (m && m.targets && m.scenes) ? m : FALLBACK;
        return manifest;
      });
  }

  function pickTarget(m, id) {
    var wanted = id || m['default'];
    for (var i = 0; i < m.targets.length; i++) {
      if (m.targets[i].id === wanted) { return m.targets[i]; }
    }
    return m.targets[0];
  }

  // A target says which scene it carries by default; the picker and the
  // ?scene= parameter override that without touching the manifest.
  function pickScene(m, target, id) {
    return m.scenes[id] || m.scenes[target.scene] || m.scenes[Object.keys(m.scenes)[0]];
  }

  /* ── layer compiler ─────────────────────────────────────────
     A small vocabulary of named recipes, plus a raw `entity` escape hatch
     for anything the vocabulary does not cover. */

  var LAYERS = {
    // the object's footprint, a ring flat on the marker
    halo: function (l) {
      return el({
        position: xyz(l.position || [0, 0.012, 0]),
        rotation: '-90 0 0',
        geometry: 'primitive: ring; radiusInner: ' + num(l.innerRadius, 0.62) +
                  '; radiusOuter: ' + num(l.outerRadius, 0.74) + '; segmentsTheta: 64',
        material: flat(l, 0.55) + '; side: double',
        animation: l.breathe ? 'property: scale; to: 1.14 1.14 1.14; dir: alternate; loop: true; dur: ' +
                   l.breathe + '; easing: easeInOutSine' : null
      });
    },

    ring: function (l) {
      return el({
        position: xyz(l.position || [0, 0.6, 0]),
        rotation: xyz(l.rotation || [72, 0, 18]),
        geometry: 'primitive: torus; radius: ' + num(l.radius, 0.62) +
                  '; radiusTubular: ' + num(l.thickness, 0.008) +
                  '; segmentsRadial: 12; segmentsTubular: 64',
        material: flat(l, 0.75),
        animation: l.spin ? 'property: rotation; to: ' + spinTo(l.rotation || [72, 0, 18]) +
                   '; loop: true; dur: ' + l.spin + '; easing: linear' : null
      });
    },

    shell: function (l) {
      return el({
        position: xyz(l.position || [0, 0.6, 0]),
        geometry: 'primitive: octahedron; radius: ' + num(l.radius, 0.52),
        material: flat(l, 0.32) + '; wireframe: true',
        animation: l.spin ? 'property: rotation; to: 0 -360 0; loop: true; dur: ' +
                   l.spin + '; easing: linear' : null
      });
    },

    // several small spheres on one shared pivot, so they orbit together
    motes: function (l) {
      var kids = (l.items || []).map(function (m) {
        return el({
          position: xyz(m.position),
          geometry: 'primitive: sphere; radius: ' + num(m.radius, 0.03) +
                    '; segmentsWidth: 16; segmentsHeight: 12',
          material: flat(m, 0.9)
        });
      }).join('\n');
      return el({
        position: xyz(l.position || [0, 0.6, 0]),
        animation: l.spin ? 'property: rotation; to: 0 360 0; loop: true; dur: ' +
                   l.spin + '; easing: linear' : null
      }, kids);
    },

    model: function (l, m) {
      if (!m.assets || !m.assets[l.asset]) { return ''; }
      var scale = num(l.scale, 1);
      return el({
        id: 'shard',
        position: xyz(l.position || [0, 0, 0]),
        scale: scale + ' ' + scale + ' ' + scale,
        'gltf-model': '#asset-' + l.asset,
        animation: l.spin ? 'property: rotation; to: 0 360 0; loop: true; dur: ' +
                   l.spin + '; easing: linear' : null
      });
    },

    // anything the recipes above do not cover: attributes, verbatim
    entity: function (l) { return el(l.attributes || {}); }
  };

  function buildScene(m, def) {
    return (def.layers || []).map(function (l) {
      var make = LAYERS[l.type];
      return make ? make(l, m) : '';
    }).join('\n');
  }

  function buildLights(def) {
    return (def.lights || []).map(function (l) {
      var light = 'type: ' + (l.type || 'ambient') +
                  '; color: ' + (l.color || '#FFFFFF') +
                  '; intensity: ' + num(l.intensity, 1);
      if (l.distance) { light += '; distance: ' + l.distance; }
      return el({ light: light, position: l.position ? xyz(l.position) : null });
    }).join('\n');
  }

  // Only the assets this scene actually references — the beacon scene loads
  // no model at all, and should not pay for one.
  function buildAssets(m, def) {
    var used = {};
    (def.layers || []).forEach(function (l) { if (l.asset) { used[l.asset] = true; } });
    var items = Object.keys(used).map(function (id) {
      return '<a-asset-item id="asset-' + id + '" src="' + m.assets[id] + '"></a-asset-item>';
    });
    return '<a-assets timeout="30000">\n' + items.join('\n') + '\n</a-assets>';
  }

  /* ── markup helpers ─────────────────────────────────────── */

  function el(attrs, children) {
    var out = '<a-entity';
    Object.keys(attrs).forEach(function (k) {
      if (attrs[k] === null || attrs[k] === undefined) { return; }
      out += ' ' + k + '="' + String(attrs[k]).replace(/"/g, '&quot;') + '"';
    });
    return children ? out + '>\n' + children + '\n</a-entity>' : out + '></a-entity>';
  }

  function flat(l, fallbackOpacity) {
    var o = num(l.opacity, fallbackOpacity);
    return 'color: ' + (l.color || '#FFFFFF') + '; shader: flat; opacity: ' + o +
           '; transparent: ' + (o < 1);
  }

  function xyz(v) {
    return Array.isArray(v) ? v.join(' ') : String(v);
  }

  function num(v, fallback) {
    return typeof v === 'number' && isFinite(v) ? v : fallback;
  }

  // Spin a tilted ring about its own Y without losing the tilt.
  function spinTo(rotation) {
    return rotation[0] + ' 360 ' + rotation[2];
  }

  /* ── scenes ─────────────────────────────────────────────────
     Built on demand so the camera permission prompt is tied to a tap,
     not to page load. */

  // The model ships meshopt-compressed: 2.29 MB of geometry and textures down
  // to 894 KB. A-Frame injects this path as a classic script and waits on the
  // window.MeshoptDecoder it registers. Draco would compress a little harder
  // and cost a 300 KB WASM blob to do it; this decoder is 29 KB with its WASM
  // inlined, which matters more when the whole point is offline-first.
  var MESHOPT = 'gltf-model="meshoptDecoderPath: vendor/meshopt_decoder.js"';

  // preserveDrawingBuffer is deliberately absent: A-Frame does not forward it
  // to the WebGLRenderer constructor, so setting it here would only look like
  // it worked. capture() re-renders on demand instead.
  var RENDERER = 'renderer="antialias: true; alpha: true; colorManagement: true; ' +
                 'logarithmicDepthBuffer: true"';

  // Pattern markers and natural-feature targets carry the same content but
  // sit in different coordinate systems. A pattern marker is one unit per
  // marker width, origin at the centre, Y up. An NFT target is in the source
  // image's pixels, origin at its top-left corner, Y down the page. `space`
  // in the manifest reconciles the two, so the scene layers never have to
  // know which kind of target they landed on.
  function trackedTarget(m, target, def) {
    var content = buildScene(m, def);

    if (target.tracking === 'nft') {
      // AR.js reports natural-feature poses in millimetres, with the origin at
      // the target's bottom-left corner: X across it, Z down it, Y out of the
      // page. Our layers assume one unit per target width, centred, Y up — so
      // the wrapper carries the whole difference, worked out from the trained
      // image's own dimensions rather than restated as a magic matrix.
      var size = target.size || {};
      var dpi = num(size.dpi, 72);
      var wide = num(size.width, 1000) / dpi * 25.4;
      var deep = num(size.height, 1414) / dpi * 25.4;
      var k = wide * num(target.contentScale, 1);
      var sp = {
        position: [wide / 2, 0, -deep / 2],
        rotation: [0, 0, 0]
      };
      return [
        '  <a-nft id="marker" type="nft" url="' + target.descriptors + '"',
        '     smooth="true" smooth-count="' + num((target.smooth || {}).count, 10) + '"',
        '     smooth-tolerance="' + num((target.smooth || {}).tolerance, 0.01) + '"',
        '     smooth-threshold="' + num((target.smooth || {}).threshold, 5) + '">',
        '    <a-entity id="space" position="' + xyz(sp.position) + '"',
        '      rotation="' + xyz(sp.rotation) + '"',
        '      scale="' + k + ' ' + k + ' ' + k + '">',
        content,
        '    </a-entity>',
        '  </a-nft>'
      ].join('\n');
    }

    var smooth = target.smooth || {};
    return [
      // Note: marker attributes are kebab-case. smoothCount would silently
      // not map, and the object would jitter with no obvious cause.
      '  <a-marker id="marker" type="pattern" url="' + target.pattern + '"',
      '     smooth="true" smooth-count="' + num(smooth.count, 8) + '"',
      '     smooth-tolerance="' + num(smooth.tolerance, 0.01) + '"',
      '     smooth-threshold="' + num(smooth.threshold, 4) + '">',
      content,
      '  </a-marker>'
    ].join('\n');
  }

  function arScene(m, target, def) {
    return [
      '<a-scene id="scene" embedded',
      '  vr-mode-ui="enabled: false"',
      '  device-orientation-permission-ui="enabled: false"',
      '  ' + MESHOPT,
      '  ' + RENDERER,
      '  arjs="sourceType: webcam; detectionMode: mono; patternRatio: 0.5; trackingMethod: best;',
      '        cameraParametersUrl: data/camera_para.dat; debugUIEnabled: false;',
      '        sourceWidth: 1280; sourceHeight: 960; displayWidth: 1280; displayHeight: 960">',
      buildAssets(m, def),
      buildLights(def),
      trackedTarget(m, target, def),
      '  <a-entity camera look-controls="enabled: false" wasd-controls="enabled: false"></a-entity>',
      '</a-scene>'
    ].join('\n');
  }

  /* Markerless placement. No AR.js at all: WebXR gives us the camera, the
     pose and a hit test against real surfaces, and A-Frame's built-in
     ar-hit-test draws the reticle and anchors the target where the user taps.

     Marker space is one unit per marker width; room space is one unit per
     metre. Without roomScale the object arrives on the floor at the size of
     a small car. */
  function xrScene(m, def) {
    var scale = num(def.roomScale, 0.3);
    return [
      '<a-scene id="scene"',
      '  vr-mode-ui="enabled: false"',
      '  ar-mode-ui="enabled: false"',
      '  device-orientation-permission-ui="enabled: false"',
      '  ' + MESHOPT,
      '  ' + RENDERER,
      '  webxr="requiredFeatures: local-floor, hit-test; optionalFeatures: dom-overlay; overlayElement: #overlay"',
      '  ar-hit-test="target: #placeable; type: map">',
      buildAssets(m, def),
      buildLights(def),
      // ar-hit-test reveals and anchors the target itself on tap, so it
      // starts invisible rather than hovering at the origin.
      '  <a-entity id="placeable" visible="false"',
      '    scale="' + scale + ' ' + scale + ' ' + scale + '">',
      buildScene(m, def),
      '  </a-entity>',
      '  <a-entity camera></a-entity>',
      '</a-scene>'
    ].join('\n');
  }

  /* Placements in the world. The same WebXR session as markerless mode, but
     the content comes from the store rather than from a tap, and a tap adds
     to it. The reticle is what ar-hit-test drives; #placements is filled by
     the controller and is otherwise none of the scene's business. */
  function worldScene(m) {
    var assets = {};
    Object.keys(m.scenes).forEach(function (id) {
      (m.scenes[id].layers || []).forEach(function (l) { if (l.asset) { assets[l.asset] = true; } });
    });
    var items = Object.keys(assets).map(function (id) {
      return '<a-asset-item id="asset-' + id + '" src="' + m.assets[id] + '"></a-asset-item>';
    });

    return [
      '<a-scene id="scene"',
      '  vr-mode-ui="enabled: false"',
      '  ar-mode-ui="enabled: false"',
      '  device-orientation-permission-ui="enabled: false"',
      '  ' + MESHOPT,
      '  ' + RENDERER,
      '  webxr="requiredFeatures: local-floor, hit-test; optionalFeatures: dom-overlay; overlayElement: #overlay"',
      '  ar-hit-test="target: #reticle; type: map">',
      // Every scene's assets, because any of them may turn up nearby.
      '  <a-assets timeout="30000">' + items.join('\n') + '</a-assets>',
      buildLights(m.scenes[Object.keys(m.scenes)[0]]),
      '  <a-entity id="reticle" visible="false"',
      '    geometry="primitive: ring; radiusInner: 0.12; radiusOuter: 0.16; segmentsTheta: 48"',
      '    rotation="-90 0 0"',
      '    material="color: #6BE3E8; shader: flat; opacity: 0.8; transparent: true; side: double">',
      '  </a-entity>',
      '  <a-entity id="placements"></a-entity>',
      '  <a-entity camera></a-entity>',
      '</a-scene>'
    ].join('\n');
  }

  // Same layers, no camera feed and no tracker. The marker itself is laid in
  // as a floor plane so the preview reads as the real thing at rest.
  function previewScene(m, def) {
    return [
      '<a-scene id="scene" embedded',
      '  vr-mode-ui="enabled: false"',
      '  device-orientation-permission-ui="enabled: false"',
      '  ' + MESHOPT,
      '  ' + RENDERER + '>',
      buildAssets(m, def).replace('</a-assets>',
        '<img id="markerimg" src="data/marker-hiro.png">\n</a-assets>'),
      '  <a-sky color="#0B0A14"></a-sky>',
      '  <a-entity geometry="primitive: plane; width: 1; height: 1" rotation="-90 0 0"',
      '    material="src: #markerimg; shader: flat; side: double"></a-entity>',
      '  <a-entity geometry="primitive: plane; width: 12; height: 12" rotation="-90 0 0" position="0 -0.002 0"',
      '    material="color: #17162B; shader: flat"></a-entity>',
      buildLights(def),
      buildScene(m, def),
      '  <a-entity id="rig" orbit="radius: 2.6; theta: 22; phi: 24; target: 0 0.5 0">',
      '    <a-entity camera="fov: 55" look-controls="enabled: false" wasd-controls="enabled: false"></a-entity>',
      '  </a-entity>',
      '</a-scene>'
    ].join('\n');
  }

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

  /* ── markerless mode ────────────────────────────────────────
     Offered only where it actually works. isSessionSupported is async and
     absent on iOS entirely, so the button starts hidden and appears if the
     answer comes back yes — never the other way round, which would flash a
     control most visitors cannot use. */

  function probeXR() {
    if (!navigator.xr || !navigator.xr.isSessionSupported) { return; }
    navigator.xr.isSessionSupported('immersive-ar')
      .then(function (ok) {
        if (!ok) { return; }
        xrBtn.hidden = false;
        if (window.SpatialConfig && window.SpatialConfig.enabled) { worldBtn.hidden = false; }
      })
      .catch(function () { /* treat any failure as unsupported */ });
  }

  function startXR() {
    busy(xrBtn, 'Loading engine…');
    fault.hidden = true;

    Promise.all([loadCore(), loadManifest()])
      .then(function () {
        mode = 'xr';
        enterStage();
        setState('seeking', 'Point at the floor');

        var target = pickTarget(manifest, chosenTarget);
        captureWindowListeners();
        slot.innerHTML = xrScene(manifest, pickScene(manifest, target, chosenScene));

        var scene = document.getElementById('scene');
        scene.addEventListener('ar-hit-test-achieved', function () {
          setState('seeking', 'Tap to place');
        });
        scene.addEventListener('ar-hit-test-select', function () {
          setState('locked', 'Placed');
        });
        // The headset or the system back gesture can end the session without
        // going through our own Stop button.
        scene.addEventListener('exit-vr', function () { if (mode === 'xr') { stop(); } });

        idle(xrBtn);
        return sceneReady(scene).then(function () { return scene.enterAR(); });
      })
      .then(keepAwake)
      .catch(onStartError);
  }

  function sceneReady(scene) {
    if (scene.hasLoaded) { return Promise.resolve(); }
    return new Promise(function (resolve) {
      scene.addEventListener('loaded', resolve, { once: true });
    });
  }

  /* ── the world ──────────────────────────────────────────────
     Content anchored to places rather than to printed targets. Offered only
     when a project is configured and the device can hold a WebXR session,
     because without both there is nothing to anchor to. */

  var world = null;
  var store = null;
  var worldTimer = null;
  var rendered = {};        // placement id -> element, so models are not reloaded

  function worldAvailable() {
    return !!(window.SpatialConfig && window.SpatialConfig.enabled &&
              window.SpatialStore && window.SpatialWorld && !xrBtn.hidden);
  }

  function startWorld() {
    busy(worldBtn, 'Loading engine…');
    fault.hidden = true;

    Promise.all([loadCore(), loadManifest()])
      .then(function () {
        busy(worldBtn, 'Checking location…');
        return warmLocation();
      })
      .then(function () {
        mode = 'world';
        enterStage();
        setState('seeking', 'Finding you…');
        captureWindowListeners();
        slot.innerHTML = worldScene(manifest);

        var scene = document.getElementById('scene');
        scene.addEventListener('ar-hit-test-select', onPlaceHere);
        scene.addEventListener('exit-vr', function () { if (mode === 'world') { stop(); } });

        var appCheck = window.SpatialAppCheck
          ? window.SpatialAppCheck.create(window.SpatialConfig)
          : null;
        store = SpatialStore.createStore(Object.assign({}, window.SpatialConfig, {
          appCheck: appCheck
        }));
        world = SpatialWorld.create({
          store: store,
          provider: SpatialLocalize.gpsProvider(),
          compass: SpatialLocalize.compass(),
          config: window.SpatialConfig,
          pose: sessionPose,
          onState: onWorldState,
          onPlacements: renderPlacements
        });

        idle(worldBtn);
        return sceneReady(scene).then(function () { return scene.enterAR(); });
      })
      .then(function () {
        keepAwake();
        world.start().catch(function () { /* onWorldState already reported it */ });
        // Fixes keep arriving: first to find north, then to correct the drift
        // that WebXR tracking accumulates without ever mentioning it.
        worldTimer = setInterval(function () {
          if (mode !== 'world' || !world) { return; }
          if (world.state() !== 'ready' || world.needsRelocalize()) {
            world.sample().then(function (state) {
              if (state === 'ready') { world.refresh(); }
            }).catch(function () {});
          }
        }, 4000);
      })
      .catch(onStartError);
  }

  /* Ask for location while the page is still an ordinary page.
     Two reasons. A permission prompt raised inside an immersive WebXR session
     is at best awkward and at worst never shown at all, so the session would
     start and then quietly fail to locate. And a refusal is worth finding out
     about before the camera is running, not after. A cold receiver that has
     not fixed yet is not a refusal — the session can start and the fix can
     arrive a moment later. */
  function warmLocation() {
    if (!navigator.geolocation) {
      return Promise.reject(new Error('This browser has no geolocation.'));
    }

    return new Promise(function (resolve, reject) {
      navigator.geolocation.getCurrentPosition(
        function () { resolve(true); },
        function (err) {
          if (err && (err.code === 1 || /permissions policy/i.test(err.message || ''))) {
            var e = new Error(locationMessage(err));
            e.isLocation = true;
            reject(e);
            return;
          }
          resolve(false);
        },
        { enableHighAccuracy: true, timeout: 12000, maximumAge: 60000 }
      );
    });
  }

  function locationMessage(err) {
    if (/permissions policy|disabled in this document/i.test((err && err.message) || '')) {
      return 'Geolocation is blocked by this page\'s permissions policy, so the ' +
             'browser will not even ask. This is a server header, not a setting on your phone.';
    }
    return 'Location was refused. Allow it for this site, then reload. ' +
           'On Android, check the device location toggle too — the site permission ' +
           'alone is not enough.';
  }

  // Where the device is in the session's own frame. The controller needs this
  // paired with each fix — a position on the globe is only half of a bearing.
  function sessionPose() {
    var scene = document.getElementById('scene');
    if (!scene || !scene.camera) { return { x: 0, y: 0, z: 0 }; }
    var v = new AFRAME.THREE.Vector3();
    scene.camera.getWorldPosition(v);
    return { x: v.x, y: v.y, z: v.z };
  }

  function onWorldState(next, detail) {
    if (next === 'locating') { setState('seeking', 'Finding you…'); }
    else if (next === 'calibrating') {
      // The honest version of the compass problem: a magnetometer is tens of
      // degrees out, so the bearing comes from the walk instead.
      var walked = Math.round(detail.walked || 0);
      setState('seeking', 'Walk a few metres — ' + walked + 'm so far');
    } else if (next === 'ready') {
      setState('locked', fixLabel(detail.accuracy));
    } else if (next === 'error') {
      setState('seeking', detail.message || 'Cannot find you');
    }
    if (!nearbyEl.hidden) { renderNearby(); }
  }

  function fixLabel(accuracy) {
    if (!accuracy) { return 'Located'; }
    // The suffix is the point. A compass bearing is usable and is not a good
    // one, and the difference has to be visible without being looked up.
    var from = accuracy.headingFrom === 'compass' ? ' compass' : '';
    return '±' + Math.round(accuracy.positionM) + 'm · ±' +
           Math.round(accuracy.headingDeg) + '°' + from;
  }

  /* Placements are diffed rather than rebuilt. Re-creating the entities on
     every refresh would drop and refetch every model, which is both slow and
     visible. */
  function renderPlacements(list) {
    var host = document.getElementById('placements');
    if (!host) { return; }

    var seen = {};
    list.forEach(function (p) {
      seen[p.id] = true;
      var el = rendered[p.id];

      if (!el) {
        var def = manifest.scenes[p.scene];
        if (!def) { return; }
        var scale = num(def.roomScale, 0.3) * num(p.scale, 1);

        el = document.createElement('a-entity');
        el.setAttribute('scale', scale + ' ' + scale + ' ' + scale);
        el.innerHTML = buildScene(manifest, def);
        host.appendChild(el);
        rendered[p.id] = el;
      }

      el.setAttribute('position', p.local.x + ' ' + p.local.y + ' ' + p.local.z);
      el.setAttribute('rotation', '0 ' + (p.yawRad * 180 / Math.PI) + ' 0');
    });

    Object.keys(rendered).forEach(function (id) {
      if (seen[id]) { return; }
      if (rendered[id].parentNode) { rendered[id].parentNode.removeChild(rendered[id]); }
      delete rendered[id];
    });

    setNearby(list);

    if (world && world.state() === 'ready') {
      var frame = world.frame();
      setState('locked', list.length + ' nearby · ' + fixLabel(frame && frame.accuracy));
    }
  }

  function onPlaceHere(e) {
    if (!world || world.state() !== 'ready') {
      setState('seeking', 'Not located yet — keep walking');
      return;
    }

    var at = e.detail && e.detail.position;
    if (!at) { return; }

    // ar-hit-test anchors its target on select, which would leave the reticle
    // sitting on the ground. It is an aiming mark, not the content.
    var reticle = document.getElementById('reticle');
    if (reticle) { reticle.setAttribute('visible', false); }

    // Face whatever the user is facing, so a placed object reads the right way
    // round to the person who put it there.
    var scene = document.getElementById('scene');
    var forward = new AFRAME.THREE.Vector3();
    scene.camera.getWorldDirection(forward);
    var yaw = Math.atan2(-forward.x, -forward.z);

    setState('locked', 'Placing…');
    world.place(chosenScene || pickTarget(manifest, chosenTarget).scene,
      { x: at.x, y: at.y, z: at.z }, yaw)
      .then(function () { flash(); })
      .catch(function (err) {
        setState('locked', 'Could not save: ' + (err.message || 'refused'));
      });
  }

  /* ── what is around you ─────────────────────────────────────
     Placements only appear once you are located and looking at them, which
     leaves an empty field and a broken app looking identical. This is a
     list of everything within range with a distance and an arrow that turns
     as you do, an empty state that says which of the two it is, and the
     controls that would otherwise force a trip back to the gate: what to
     place next, and how to remove something you got wrong. */

  var nearby = [];
  var arrowTimer = null;

  function openNearby() {
    nearbyEl.hidden = false;
    listBtn.setAttribute('aria-expanded', 'true');
    renderNearby();
    // The arrows are only meaningful if they follow the phone.
    clearInterval(arrowTimer);
    arrowTimer = setInterval(updateArrows, 200);
  }

  function closeNearby() {
    nearbyEl.hidden = true;
    listBtn.setAttribute('aria-expanded', 'false');
    clearInterval(arrowTimer);
    arrowTimer = null;
  }

  function toggleNearby() {
    if (nearbyEl.hidden) { openNearby(); } else { closeNearby(); }
  }

  function setNearby(list) {
    nearby = list;
    listBtn.hidden = mode !== 'world';
    listCount.textContent = String(list.length);
    if (!nearbyEl.hidden) { renderNearby(); }
  }

  function renderNearby() {
    renderPlaceChooser();
    nearbyList.innerHTML = '';

    if (!world || world.state() !== 'ready') {
      // The distinction that matters: not located yet is not the same as
      // located and alone.
      nearbyEmpty.textContent = world && world.state() === 'calibrating'
        ? 'No compass reading, so the bearing has to come from a walk. ' +
          'Head off in a straight line for twenty metres or so.'
        : 'Finding you. Nothing can be listed until there is a position to list it against.';
      return;
    }

    if (!nearby.length) {
      var radius = (window.SpatialConfig && window.SpatialConfig.radiusM) || 300;
      nearbyEmpty.textContent = 'Nothing placed within ' + radius + ' m. ' +
        'Point at the ground and tap to leave the first thing here.';
      return;
    }

    var frame = world.frame();
    nearbyEmpty.textContent = frame && frame.accuracy.headingFrom === 'compass'
      ? 'Bearing is from the compass, so everything here may be twenty degrees ' +
        'out. Walk twenty metres in a straight line and it will correct itself.'
      : '';

    var mine = store && store.uid();

    nearby.forEach(function (p) {
      var row = document.createElement('li');

      var dir = document.createElement('span');
      dir.className = 'nearby__dir';
      dir.dataset.id = p.id;
      dir.textContent = '\u25B2';
      row.appendChild(dir);

      var what = document.createElement('span');
      what.className = 'nearby__what';
      var def = manifest && manifest.scenes[p.scene];
      what.textContent = (def && def.label) || p.scene;
      row.appendChild(what);

      var far = document.createElement('span');
      far.className = 'nearby__far';
      far.dataset.id = p.id;
      far.textContent = describeDistance(p);
      row.appendChild(far);

      // Only what you put there. Someone else's placement is not yours to
      // remove, and the rules would refuse it anyway.
      if (p.owner && mine && p.owner === mine) {
        var drop = document.createElement('button');
        drop.type = 'button';
        drop.className = 'nearby__drop';
        drop.textContent = 'Remove';
        drop.addEventListener('click', function () {
          drop.disabled = true;
          drop.textContent = '…';
          world.remove(p.id).catch(function (err) {
            drop.disabled = false;
            drop.textContent = 'Remove';
            nearbyEmpty.textContent = 'Could not remove that: ' + (err.message || 'refused');
          });
        });
        row.appendChild(drop);
      }

      nearbyList.appendChild(row);
    });

    updateArrows();
  }

  // Switching what you place used to mean leaving the session, going back to
  // the gate and starting again.
  function renderPlaceChooser() {
    if (!manifest) { return; }
    var ids = Object.keys(manifest.scenes);
    if (ids.length < 2) { nearbyPlace.innerHTML = ''; return; }
    if (nearbyPlace.children.length === ids.length) { markChosen(); return; }

    nearbyPlace.innerHTML = '';
    ids.forEach(function (id) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'chip';
      b.dataset.value = id;
      b.textContent = 'Place ' + ((manifest.scenes[id].label || id).toLowerCase());
      b.addEventListener('click', function () {
        chosenScene = id;
        markChosen();
      });
      nearbyPlace.appendChild(b);
    });
    markChosen();
  }

  function markChosen() {
    Array.prototype.forEach.call(nearbyPlace.children, function (c) {
      c.setAttribute('aria-pressed', String(c.dataset.value === chosenScene));
    });
  }

  function describeDistance(p) {
    var here = sessionPose();
    var d = Math.hypot(p.local.x - here.x, p.local.z - here.z);
    return d < 10 ? d.toFixed(1) + ' m' : Math.round(d) + ' m';
  }

  /* An arrow per row, pointing where the thing actually is relative to where
     the phone is pointing. Recomputed rather than stored, because both terms
     change every time the user moves. */
  function updateArrows() {
    if (nearbyEl.hidden || !nearby.length) { return; }

    var scene = document.getElementById('scene');
    if (!scene || !scene.camera) { return; }

    var here = sessionPose();
    var facing = new AFRAME.THREE.Vector3();
    scene.camera.getWorldDirection(facing);
    var yaw = Math.atan2(-facing.x, -facing.z);

    nearby.forEach(function (p) {
      var dx = p.local.x - here.x;
      var dz = p.local.z - here.z;
      var bearing = Math.atan2(dx, -dz);        // 0 is straight ahead in session terms
      var relative = bearing - yaw;

      var arrow = nearbyEl.querySelector('.nearby__dir[data-id="' + cssEscape(p.id) + '"]');
      if (arrow) { arrow.style.transform = 'rotate(' + (relative * 180 / Math.PI) + 'deg)'; }

      var far = nearbyEl.querySelector('.nearby__far[data-id="' + cssEscape(p.id) + '"]');
      if (far) { far.textContent = describeDistance(p); }
    });
  }

  function cssEscape(value) {
    return String(value).replace(/["\\]/g, '\\$&');
  }

  /* ── scene picker ───────────────────────────────────────────
     Rendered from the manifest, and only when there is a choice to make.
     One scene means no picker rather than a control that does nothing. */

  function renderPicker(m) {
    if (!chosenTarget || !findTarget(m, chosenTarget)) {
      chosenTarget = pickTarget(m, null).id;
    }
    if (!chosenScene || !m.scenes[chosenScene]) {
      chosenScene = pickTarget(m, chosenTarget).scene;
    }

    picker.innerHTML = '';

    chipRow('Track', m.targets.map(function (t) {
      return { id: t.id, label: t.label || t.id };
    }), function () { return chosenTarget; }, function (id) {
      chosenTarget = id;
      // A target names the scene it carries; following it keeps the two in
      // step unless the visitor then picks a scene explicitly.
      var next = findTarget(m, id);
      if (next && m.scenes[next.scene]) { chosenScene = next.scene; syncRows(); }
      updateSheet(m);
    });

    chipRow('Carries', Object.keys(m.scenes).map(function (id) {
      return { id: id, label: m.scenes[id].label || id };
    }), function () { return chosenScene; }, function (id) { chosenScene = id; });

    updateSheet(m);
    picker.hidden = picker.children.length === 0;
  }

  // One row per axis of choice, and none at all when there is nothing to
  // choose — a control that cannot change anything is worse than no control.
  function chipRow(label, options, current, onPick) {
    if (options.length < 2) { return; }

    var row = document.createElement('div');
    row.className = 'picker__row';

    var name = document.createElement('span');
    name.className = 'picker__label';
    name.textContent = label;
    row.appendChild(name);

    options.forEach(function (opt) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'chip';
      b.dataset.value = opt.id;
      b.textContent = opt.label;
      b.setAttribute('aria-pressed', String(opt.id === current()));
      b.addEventListener('click', function () {
        onPick(opt.id);
        Array.prototype.forEach.call(row.querySelectorAll('.chip'), function (c) {
          c.setAttribute('aria-pressed', String(c === b));
        });
      });
      row.appendChild(b);
    });

    picker.appendChild(row);
  }

  function syncRows() {
    Array.prototype.forEach.call(picker.querySelectorAll('.picker__row'), function (row, i) {
      var want = i === 0 ? chosenTarget : chosenScene;
      Array.prototype.forEach.call(row.querySelectorAll('.chip'), function (c) {
        c.setAttribute('aria-pressed', String(c.dataset.value === want));
      });
    });
  }

  // The "open the marker" link has to follow the target, or it hands out the
  // wrong sheet to print.
  function updateSheet(m) {
    var t = findTarget(m, chosenTarget);
    if (!t || !sheetLink) { return; }
    sheetLink.href = t.sheet || 'marker.html';
    sheetLink.textContent = 'Open the ' + (t.tracking === 'nft' ? 'poster' : 'marker');
  }

  function findTarget(m, id) {
    for (var i = 0; i < m.targets.length; i++) {
      if (m.targets[i].id === id) { return m.targets[i]; }
    }
    return null;
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
    Promise.all([loadVendor(), loadManifest()])
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
        var target = pickTarget(manifest, chosenTarget);
        captureWindowListeners();
        slot.innerHTML = arScene(manifest, target, pickScene(manifest, target, chosenScene));

        var marker = document.getElementById('marker');
        marker.addEventListener('markerFound', function () { setState('locked'); });
        marker.addEventListener('markerLost',  function () { setState('seeking'); });

        if (target.tracking === 'nft') { kickNFT(); }

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

    Promise.all([loadCore(), loadManifest()])
      .then(function () {
        mode = 'preview';
        enterStage();
        setState('preview', 'Preview — drag to orbit');
        captureWindowListeners();
        slot.innerHTML = previewScene(manifest,
          pickScene(manifest, pickTarget(manifest, chosenTarget), chosenScene));
        idle(previewBtn);
      })
      .catch(onStartError);
  }

  /* ── natural-feature start-up ───────────────────────────────
     AR.js's NFT path defers all of its setup until a window-level
     `arjs-video-loaded` event. It only subscribes to that event once the
     ARToolkit controller exists — and the controller cannot exist until
     camera_para.dat has been fetched and parsed, which is comfortably after
     the video became ready and the event already fired. The listener is
     therefore registered against an event that has been and gone, and the
     descriptors are never even requested: no error, no lock, nothing.

     Re-dispatching it once the controller is up is enough. The event carries
     the video element it measures, so it has to be dispatched with the same
     detail shape AR.js uses. */

  var nftKick = null;

  function kickNFT() {
    var tries = 0;
    clearInterval(nftKick);
    nftKick = setInterval(function () {
      if (mode !== 'ar' || ++tries > 150) { clearInterval(nftKick); return; }

      var scene = document.getElementById('scene');
      var sys = scene && scene.systems && scene.systems.arjs;
      var ctx = sys && sys._arSession && sys._arSession.arContext;
      var video = document.querySelector('#arjs-video');
      if (!ctx || !ctx.arController || !video || !video.videoWidth) { return; }

      clearInterval(nftKick);
      nftKick = null;
      // One beat, so the anchor's own listener is attached before the event
      // it is waiting for arrives.
      setTimeout(function () { dispatchVideoLoaded(video); }, 250);
    }, 200);
  }

  /* AR.js reads the video element's *CSS* box when the event fires and then
     uses those two numbers as the source rectangle of every frame it grabs
     thereafter — drawImage(video, 0, 0, clientWidth, clientHeight, …). Our
     video is letterboxed to cover the screen, so its CSS box has nothing to
     do with its pixel size, and AR.js ends up matching against a crop of one
     corner of the frame with the rest black. Show the element at its true
     size for the length of the dispatch — listeners run synchronously, so
     this is a single frame's worth of lie — and it reads the whole picture. */
  function dispatchVideoLoaded(video) {
    var was = {
      width: video.style.width,
      height: video.style.height,
      marginLeft: video.style.marginLeft,
      marginTop: video.style.marginTop
    };

    video.style.width = video.videoWidth + 'px';
    video.style.height = video.videoHeight + 'px';
    video.style.marginLeft = '0px';
    video.style.marginTop = '0px';
    void video.offsetWidth;                       // force the box to settle

    window.dispatchEvent(new CustomEvent('arjs-video-loaded', {
      detail: { component: video }
    }));

    Object.keys(was).forEach(function (k) { video.style[k] = was[k]; });
    applyFeedSize();
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
    overlayEl.hidden = false;
    listBtn.hidden = mode !== 'world';
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
    } else if (err && err.isLocation) {
      msg = err.message;
    } else if (mode === 'xr' || mode === 'world' || (err && /XR|session/i.test(err.message || ''))) {
      msg = 'This device would not start a WebXR session. Marker mode and preview both still work.';
    } else if (err && /Failed to load/.test(err.message || '')) {
      msg = err.message + ' — check the connection and reload.';
    }
    stop();
    fault.textContent = msg;
    fault.hidden = false;
  }

  function stop() {
    releaseWake();
    closeNearby();
    listBtn.hidden = true;
    nearby = [];
    store = null;
    clearInterval(worldTimer);
    worldTimer = null;
    if (world) { world.reset(); world = null; }
    rendered = {};
    clearInterval(nftKick);
    nftKick = null;
    syncTimers.forEach(clearTimeout);
    syncTimers = [];
    window.removeEventListener('resize', syncFeed);
    window.removeEventListener('orientationchange', syncFeed);

    var scene = document.getElementById('scene');

    // The gate comes back now, not when the teardown finishes. Disposing a
    // renderer and a WASM heap takes long enough to feel like a hang if it
    // happens between the tap and the screen changing.
    mode = null;
    stage.hidden = true;
    overlayEl.hidden = true;
    gate.hidden = false;
    document.body.classList.remove('is-running');
    idle(startBtn);
    idle(previewBtn);
    idle(xrBtn);
    idle(worldBtn);

    // An immersive session has to be ended before its renderer is pulled
    // apart, and exitVR is asynchronous. Tearing down underneath it leaves
    // the compositor holding a session that no longer has anything to draw.
    Promise.resolve()
      .then(function () {
        if (scene && scene.is && scene.is('ar-mode')) { return scene.exitVR(); }
      })
      .catch(function () { /* it may already be gone */ })
      .then(function () { teardown(scene); });
  }

  /* ── teardown ───────────────────────────────────────────────
     The expensive half, and the reason this app used to wedge a phone after
     a session or two. Nothing here is optional:

     AR.js allocates an ARToolKit context with its own WASM heap, tens of
     megabytes of it, and a camera source. Neither is freed by removing the
     scene from the page.

     three.js keeps every geometry, material and texture on the GPU until
     something disposes them.

     And the WebGL context itself outlives renderer.dispose(): three frees its
     own resources but leaves the context alive until the garbage collector
     gets round to the canvas. Desktop browsers allow about sixteen live
     contexts and phones far fewer, so on a phone the third session finds
     none available — which presents as the browser locking up rather than as
     anything resembling an error. forceContextLoss releases it now. */

  function teardown(scene) {
    if (!scene) { return; }

    // First, before anything can fire. AR.js registers two window resize
    // handlers per session and offers no way to remove them; once its source
    // is disposed they dereference a null domElement and throw. They survive
    // the scene, so every session leaves two more behind, and every rotation
    // of the phone afterwards fires the lot.
    releaseWindowListeners();

    disposeARjs(scene);
    stopCameraTracks();

    if (scene.parentNode) { scene.parentNode.removeChild(scene); }
    slot.innerHTML = '';

    disposeSceneGraph(scene);
    disposeRenderer(scene);
    terminateWorkers();
  }

  function disposeARjs(scene) {
    try {
      var system = scene.systems && scene.systems.arjs;
      var session = system && system._arSession;
      if (!session) { return; }
      // Order matters: the context owns the marker controls and the
      // ARToolKit heap; the source owns the camera.
      if (session.arContext && session.arContext.dispose) { session.arContext.dispose(); }
      if (session.arSource && session.arSource.dispose) { session.arSource.dispose(); }
    } catch (e) { /* best effort — a half-built session may have neither */ }
  }

  function stopCameraTracks() {
    Array.prototype.forEach.call(document.querySelectorAll('video'), function (video) {
      if (video.srcObject) {
        video.srcObject.getTracks().forEach(function (t) { t.stop(); });
        video.srcObject = null;
      }
      if (video.parentNode) { video.parentNode.removeChild(video); }
    });
  }

  function disposeSceneGraph(scene) {
    try {
      scene.object3D.traverse(function (object) {
        if (object.geometry && object.geometry.dispose) { object.geometry.dispose(); }

        var materials = Array.isArray(object.material) ? object.material
          : (object.material ? [object.material] : []);

        materials.forEach(function (material) {
          // Textures are the big ones — the model alone carries three
          // 1024-square maps — and disposing the material does not touch them.
          Object.keys(material).forEach(function (key) {
            var value = material[key];
            if (value && value.isTexture && value.dispose) { value.dispose(); }
          });
          if (material.dispose) { material.dispose(); }
        });
      });
    } catch (e) { /* best effort */ }
  }

  function disposeRenderer(scene) {
    try {
      var renderer = scene.renderer;
      if (!renderer) { return; }
      if (renderer.setAnimationLoop) { renderer.setAnimationLoop(null); }
      renderer.dispose();
      if (renderer.forceContextLoss) { renderer.forceContextLoss(); }
    } catch (e) { /* best effort */ }
  }

  /* Window listeners added while a scene exists belong to that scene.
     A-Frame's own are removed with it; AR.js's are not, and are anonymous, so
     removeEventListener cannot reach them. Recording them as they are added
     is the only handle. */

  var captured = [];
  var realAddEventListener = null;

  function captureWindowListeners() {
    if (realAddEventListener) { return; }

    realAddEventListener = window.addEventListener;
    window.addEventListener = function (type, listener, options) {
      if (type === 'resize' || type === 'orientationchange') {
        captured.push([type, listener, options]);
      }
      return realAddEventListener.call(window, type, listener, options);
    };
  }

  function releaseWindowListeners() {
    if (realAddEventListener) {
      window.addEventListener = realAddEventListener;
      realAddEventListener = null;
    }
    captured.forEach(function (entry) {
      try { window.removeEventListener(entry[0], entry[1], entry[2]); } catch (e) { /* fine */ }
    });
    captured = [];
  }

  /* AR.js starts a worker for natural-feature tracking and offers no way to
     reach it. Left alone it keeps its loop going after the scene is gone —
     grabbing frames from a video that no longer has a stream and shipping a
     320x240 buffer to a worker nobody is listening to, forever. Wrapping the
     constructor is the only handle there is. */

  var workers = [];

  function trackWorkers() {
    if (typeof Worker !== 'function' || Worker.__marker_one) { return; }

    var Real = Worker;
    function Tracked(url, opts) {
      var worker = new Real(url, opts);
      workers.push(worker);
      return worker;
    }
    Tracked.prototype = Real.prototype;
    Tracked.__marker_one = true;
    window.Worker = Tracked;
  }

  function terminateWorkers() {
    workers.forEach(function (worker) {
      try { worker.terminate(); } catch (e) { /* already gone */ }
    });
    workers = [];
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
  xrBtn.addEventListener('click', startXR);
  worldBtn.addEventListener('click', startWorld);
  listBtn.addEventListener('click', toggleNearby);
  document.getElementById('nearby-close').addEventListener('click', closeNearby);
  previewBtn.addEventListener('click', startPreview);
  exitBtn.addEventListener('click', stop);
  shootBtn.addEventListener('click', capture);

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && mode) { stop(); }
  });

  /* Setting App Check up is a five-step trip through two consoles, and the
     failure mode of getting one field wrong is silence. Say plainly, once, on
     every load, what the app thinks it has. */
  function reportSpatial() {
    if (!window.SpatialConfig || typeof console === 'undefined' || !console.info) { return; }

    console.info('Marker One build ' + BUILD);

    if (!window.SpatialConfig.enabled) {
      console.info('Marker One: placements off — no projectId/apiKey in spatial/config.local.js');
      return;
    }

    var appCheck = window.SpatialAppCheck
      ? window.SpatialAppCheck.create(window.SpatialConfig)
      : null;

    console.info('Marker One: placements on for ' + window.SpatialConfig.projectId +
      ' — ' + (appCheck ? appCheck.describe() : 'App Check module not loaded'));
  }

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

  // The manifest is a couple of kilobytes and the picker needs it before any
  // tap, so it is fetched at load — unlike the 3 MB of engine behind it.
  if (buildEl) { buildEl.textContent = BUILD; }
  loadManifest().then(renderPicker);
  probeXR();
  reportSpatial();

  // ?preview on the URL jumps straight in, for a link that needs no tap.
  if (location.search.indexOf('preview') !== -1 && !previewBtn.disabled) {
    startPreview();
  }
})();
