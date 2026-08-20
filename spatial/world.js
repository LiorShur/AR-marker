/* Placements in a live session.
 *
 * Deliberately knows nothing about WebXR, A-Frame or the DOM. It takes a
 * localization provider, a store, and a function that reports where the device
 * is in the session's own frame; it produces a list of placements with local
 * coordinates, and accepts new ones. That separation is what makes any of this
 * testable — an XR session cannot be run headless, but all of the logic that
 * goes wrong can.
 *
 * The states are worth reading as a sentence: it is locating until it has a
 * position, calibrating until it knows which way north is, and only then
 * ready. The middle step is the one nobody expects and the one that decides
 * whether the content lands in the right place.
 */
(function (root, factory) {
  var api = factory(
    typeof module === 'object' && module.exports ? require('./geo.js') : root.SpatialGeo,
    typeof module === 'object' && module.exports ? require('./localize.js') : root.SpatialLocalize
  );
  if (typeof module === 'object' && module.exports) { module.exports = api; }
  else { root.SpatialWorld = api; }
}(typeof self !== 'undefined' ? self : this, function (geo, localize) {
  'use strict';

  var MAX_SAMPLES = 24;

  function create(options) {
    var store = options.store;
    var provider = options.provider;
    var pose = options.pose || function () { return { x: 0, y: 0, z: 0 }; };
    var now = options.now || function () { return Date.now(); };
    var config = options.config || {};

    var radiusM = config.radiusM || 300;
    var relocalizeAfterM = config.relocalizeAfterM || 25;

    var samples = [];
    var fetchedAt = null;        // where the last query was centred
    var frame = null;
    var heading = null;          // the winning baseline
    var compass = options.compass || null;
    var placements = [];
    var state = 'idle';
    var lastFixLocal = null;
    var busy = false;

    function emit(next, detail) {
      state = next;
      if (options.onState) { options.onState(next, detail || {}); }
    }

    /* ── fixes ─────────────────────────────────────────────────
       One sample is a position; two far enough apart are a position and a
       direction. Everything waits on the second. */

    function sample() {
      if (busy) { return Promise.resolve(state); }
      busy = true;

      return provider.locate().then(function (fix) {
        busy = false;

        var here = pose();
        samples.push({
          position: fix.position,
          accuracy: fix.accuracy || { positionM: 50, headingDeg: 90 },
          local: { x: here.x, y: here.y, z: here.z },
          at: fix.at || now()
        });
        if (samples.length > MAX_SAMPLES) { samples.shift(); }

        resolveHeading();
        return state;
      }, function (err) {
        busy = false;
        emit('error', { message: err && err.message });
        throw err;
      });
    }

    // The widest-separated pair wins. Walking in a circle leaves the first and
    // last samples close together, and a short baseline is a bad bearing —
    // at five metres apart, two metres of noise is twenty degrees.
    function resolveHeading() {
      var best = null;

      for (var i = 0; i < samples.length; i++) {
        for (var j = i + 1; j < samples.length; j++) {
          var candidate = localize.headingFromBaseline(samples[i], samples[j]);
          if (!candidate || candidate.sessionYawDeg === null) { continue; }
          if (!best || candidate.separationM > best.separationM) { best = candidate; }
        }
      }

      if (best && (!heading || heading.source === 'compass' ||
                   best.separationM > heading.separationM)) {
        // A walked baseline always beats the compass, and a longer walk beats
        // a shorter one. Both are upgrades and both take effect immediately.
        heading = {
          source: 'baseline',
          sessionYawDeg: best.sessionYawDeg,
          separationM: best.separationM,
          accuracyDeg: best.accuracyDeg
        };
        rebuild();
        return;
      }
      if (heading) { rebuild(); return; }

      // No baseline yet. The compass is a far worse bearing, but it is the
      // only one available standing still or indoors — where the position
      // error is tens of metres and a baseline can never resolve at all.
      // Better to be usable and say how badly than to refuse forever.
      var bearing = compass && compass.heading();
      if (bearing !== null && bearing !== undefined && samples.length) {
        heading = {
          source: 'compass',
          sessionYawDeg: bearing,
          separationM: 0,
          accuracyDeg: compass.accuracyDeg || 25
        };
        rebuild();
        return;
      }

      emit(samples.length ? 'calibrating' : 'locating', {
        samples: samples.length,
        walked: walkedSoFar()
      });
    }

    // The heading comes from the whole walk; the origin comes from the newest
    // fix, because position drifts and the freshest observation is the least
    // wrong one.
    function rebuild() {
      var latest = samples[samples.length - 1];
      lastFixLocal = latest.local;

      frame = localize.makeFrame({
        position: latest.position,
        // In makeFrame this is the world heading of the session's forward
        // axis, not the device's — which is exactly what a baseline measures.
        headingDeg: heading.sessionYawDeg,
        accuracy: {
          positionM: latest.accuracy.positionM,
          headingDeg: heading.accuracyDeg,
          // Which of the two produced this bearing. A twenty-five degree
          // compass fix and a two degree walked one are the same shape and
          // nothing like the same thing.
          headingFrom: heading.source
        },
        provider: provider.id,
        at: latest.at
      }, { position: latest.local, yawDeg: 0 });

      emit('ready', {
        accuracy: frame.accuracy,
        source: heading.source,
        baselineM: heading.separationM
      });

      reproject();
      fetchIfNeeded();
    }

    /* Becoming located is what makes a query possible, so it is also what
       should trigger one. Nothing did: the app polled for fixes only while
       *unlocated*, so once a session settled it never asked the store for
       anything, and a returning visitor saw an empty world. */
    function fetchIfNeeded() {
      if (!frame) { return; }
      var origin = frame.origin;

      // Re-query only when the centre has moved enough for the answer to
      // differ. Relocalizing every twenty-five metres should not mean
      // refetching every twenty-five metres.
      if (fetchedAt && geo.haversine(origin.lat, origin.lon,
          fetchedAt.lat, fetchedAt.lon) < radiusM / 3) {
        return;
      }

      refresh().catch(function (err) {
        emit('error', { message: 'Could not read placements: ' + (err && err.message) });
      });
    }

    // Session tracking drifts, quietly. Once the device has walked far enough
    // for that to matter, the next fix re-anchors the frame.
    function needsRelocalize() {
      if (!frame || !lastFixLocal) { return true; }
      var here = pose();
      return Math.hypot(here.x - lastFixLocal.x, here.z - lastFixLocal.z) > relocalizeAfterM;
    }

    /* ── content ─────────────────────────────────────────────── */

    function refresh() {
      if (!frame) { return Promise.resolve([]); }
      var origin = frame.origin;

      fetchedAt = { lat: origin.lat, lon: origin.lon };

      return store.nearby(origin.lat, origin.lon, radiusM).then(function (found) {
        placements = found;
        return reproject();
      }, function (err) {
        // Let the next attempt try again rather than assuming this centre is
        // done with.
        fetchedAt = null;
        throw err;
      });
    }

    // Local coordinates are derived, never stored — the frame changes every
    // time we re-localize and the placements do not.
    function reproject() {
      if (!frame) { return []; }

      var out = placements.map(function (p) {
        var local = frame.toLocal(p.geopose.position);
        var worldHeading = geo.headingFromQuaternion(p.geopose.quaternion);
        return {
          id: p.id,
          scene: p.scene,
          scale: p.scale,
          distance: p.distance,
          owner: p.owner,
          fix: p.fix,
          local: local,
          yawRad: frame.headingToLocalYaw(worldHeading)
        };
      });

      if (options.onPlacements) { options.onPlacements(out); }
      return out;
    }

    /* ── placing ─────────────────────────────────────────────── */

    function place(scene, localPoint, localYawRad) {
      if (!frame) { return Promise.reject(new Error('not localized yet')); }

      var position = frame.toGlobal(localPoint);
      var headingDeg = frame.localYawToHeading(localYawRad || 0);

      return store.place({
        scene: scene,
        geopose: {
          position: position,
          quaternion: geo.headingToQuaternion(headingDeg)
        },
        scale: 1,
        // Recorded, not inferred. Someone reading this back needs to know it
        // was put down by GPS and a walked bearing, not by a visual fix, and
        // to trust its position accordingly.
        fix: {
          provider: frame.provider,
          positionM: frame.accuracy.positionM,
          headingDeg: frame.accuracy.headingDeg
        }
      }).then(function (saved) {
        saved.distance = 0;
        placements.push(saved);
        reproject();
        return saved;
      });
    }

    function remove(id) {
      return store.remove(id).then(function () {
        placements = placements.filter(function (p) { return p.id !== id; });
        reproject();
        return true;
      });
    }

    function walkedSoFar() {
      if (samples.length < 2) { return 0; }
      var widest = 0;
      for (var i = 0; i < samples.length; i++) {
        for (var j = i + 1; j < samples.length; j++) {
          widest = Math.max(widest, geo.haversine(
            samples[i].position.lat, samples[i].position.lon,
            samples[j].position.lat, samples[j].position.lon));
        }
      }
      return widest;
    }

    function start() {
      emit('locating', {});
      // The compass is started first so that a bearing is already available
      // when the first fix lands — otherwise the first sample resolves to
      // nothing and the user is told to walk when they need not.
      var ready = compass ? compass.start(function () {
        if (!heading) { resolveHeading(); }
      }) : Promise.resolve(false);

      return ready.catch(function () { return false; }).then(sample);
    }

    function reset() {
      if (compass) { compass.stop(); }
      samples = [];
      fetchedAt = null;
      frame = null;
      heading = null;
      placements = [];
      lastFixLocal = null;
      emit('idle', {});
    }

    return {
      start: start,
      sample: sample,
      refresh: refresh,
      reproject: reproject,
      place: place,
      remove: remove,
      reset: reset,
      needsRelocalize: needsRelocalize,
      state: function () { return state; },
      frame: function () { return frame; },
      // How far the user has actually walked, so the interface can ask for
      // more rather than just saying "calibrating" forever.
      walked: walkedSoFar,
      samples: function () { return samples.slice(); }
    };
  }

  return { create: create };
}));
