/* Where the device is, in the frame the content is stored in.
 *
 * This is the seam the whole thing turns on. Content lives in one global
 * frame — latitude, longitude, height, orientation in East-North-Up. A WebXR
 * session renders in its own local frame, in metres, whose origin is wherever
 * the session happened to start and whose yaw is arbitrary. A provider's only
 * job is to report, once, where the device is in the global frame. From that
 * one observation the transform between the two frames falls out, and WebXR's
 * own tracking carries it from there.
 *
 * That "once" matters. Nothing here needs continuous global positioning —
 * which is fortunate, because none of the accurate ways of getting it are
 * cheap enough to run every frame. Localise, compute the transform, render
 * locally, re-localise occasionally to correct drift.
 *
 * Providers implement:
 *
 *   id        string
 *   label     string for a human
 *   available()  -> Promise<boolean>
 *   locate()     -> Promise<{ position: {lat, lon, h},
 *                             headingDeg,                 // clockwise from north
 *                             accuracy: {positionM, headingDeg} }>
 *
 * The accuracy field is not decoration. A GPS fix and a visual fix are the
 * same shape and nothing like the same thing, and the app has to be able to
 * say which one it got.
 */
(function (root, factory) {
  var api = factory(typeof module === 'object' && module.exports
    ? require('./geo.js')
    : root.SpatialGeo);
  if (typeof module === 'object' && module.exports) { module.exports = api; }
  else { root.SpatialLocalize = api; }
}(typeof self !== 'undefined' ? self : this, function (geo) {
  'use strict';

  /* ── the transform ─────────────────────────────────────────
     Given the device's global pose and its pose in the session's local frame
     at the same instant, produce the mapping from global to local. Content is
     then placed by converting each placement to ENU about the fix, rotating
     into the session's yaw, and offsetting to where the device was standing. */

  function makeFrame(fix, local) {
    local = local || { position: { x: 0, y: 0, z: 0 }, yawDeg: 0 };

    // Global heading of the direction the session calls "forward" (-Z).
    // Everything reduces to this one angle: the session's yaw offset.
    var yaw = geo.headingToYaw(fix.headingDeg - (local.yawDeg || 0));
    var cos = Math.cos(yaw);
    var sin = Math.sin(yaw);

    var origin = {
      lat: fix.position.lat,
      lon: fix.position.lon,
      h: fix.position.h || 0
    };
    var offset = local.position || { x: 0, y: 0, z: 0 };

    return {
      origin: origin,
      yaw: yaw,
      accuracy: fix.accuracy || { positionM: 0, headingDeg: 0 },
      provider: fix.provider || 'unknown',
      at: fix.at || Date.now(),

      // Global -> the session's local metres.
      toLocal: function (position) {
        var enu = geo.toEnu(position.lat, position.lon, position.h || 0, origin);
        var v = geo.enuToThree(enu);
        return {
          x: offset.x + v.x * cos - v.z * sin,
          y: offset.y + v.y,
          z: offset.z + v.x * sin + v.z * cos
        };
      },

      // ...and back, for placing something where the user is standing.
      toGlobal: function (v) {
        var dx = v.x - offset.x;
        var dy = v.y - offset.y;
        var dz = v.z - offset.z;
        var t = geo.threeToEnu({
          x: dx * cos + dz * sin,
          y: dy,
          z: -dx * sin + dz * cos
        });
        return geo.fromEnu(t.e, t.n, t.u, origin);
      },

      // A compass heading in the session's frame, for orienting content that
      // was authored facing a particular way in the world.
      headingToLocalYaw: function (headingDeg) {
        return geo.headingToYaw(headingDeg - (local.yawDeg || 0));
      },

      // The inverse, for recording which way something was facing when the
      // user put it down.
      localYawToHeading: function (yawRad) {
        return ((-yawRad * 180 / Math.PI) + (local.yawDeg || 0) + 360) % 360;
      }
    };
  }

  /* ── GPS and compass ───────────────────────────────────────
     The provider of last resort, and the honest baseline. Position is a few
     metres on a good day; heading is the problem. A phone magnetometer is
     routinely ten to thirty degrees out, and it is heading, not position,
     that ruins geolocated AR — five metres of position error on something
     fifty metres away is barely visible, twenty degrees of heading error puts
     it in the wrong street. Everything better than this is better mostly
     because it fixes heading. */

  function gpsProvider(options) {
    options = options || {};
    var nav = options.navigator || (typeof navigator !== 'undefined' ? navigator : null);
    var timeout = options.timeout || 15000;

    return {
      id: 'gps',
      label: 'GPS and compass',

      available: function () {
        return Promise.resolve(!!(nav && nav.geolocation));
      },

      locate: function () {
        if (!nav || !nav.geolocation) {
          return Promise.reject(new Error('no geolocation on this device'));
        }

        return new Promise(function (resolve, reject) {
          nav.geolocation.getCurrentPosition(function (fix) {
            var c = fix.coords;
            resolve({
              provider: 'gps',
              at: fix.timestamp || Date.now(),
              position: {
                lat: c.latitude,
                lon: c.longitude,
                // GPS altitude is relative to the ellipsoid, which is the
                // convention the rest of this uses. It is also the least
                // reliable number a receiver reports — typically two to three
                // times worse than the horizontal fix.
                h: typeof c.altitude === 'number' ? c.altitude : 0
              },
              // A moving device reports course over ground, which is derived
              // from successive fixes and is far better than the compass.
              // Standing still it is null, and there is nothing to fall back
              // on from here without device orientation events.
              headingDeg: typeof c.heading === 'number' && !isNaN(c.heading) ? c.heading : 0,
              accuracy: {
                positionM: typeof c.accuracy === 'number' ? c.accuracy : 50,
                headingDeg: typeof c.heading === 'number' && !isNaN(c.heading) ? 5 : 25
              }
            });
          }, function (err) {
            var e = new Error(explain(err));
            e.code = err && err.code;
            reject(e);
          }, {
            enableHighAccuracy: true,
            timeout: timeout,
            maximumAge: 0
          });
        });
      }
    };
  }

  // The three ways this fails are three different problems with three
  // different fixes, and "location unavailable" helps with none of them. The
  // permissions-policy case is worth calling out by name: the browser refuses
  // without ever prompting, so it reads to a user as the app being broken.
  function explain(err) {
    var message = (err && err.message) || '';

    if (/permissions policy|disabled in this document/i.test(message)) {
      return 'Geolocation is blocked by this page\'s permissions policy — ' +
             'the browser will not even ask. Check the Permissions-Policy header.';
    }
    if (err && err.code === 1) {
      return 'Location was refused. Allow it for this site in the browser, then reload.';
    }
    if (err && err.code === 2) {
      return 'No position available. Turn on location services on the device — ' +
             'the site permission is not enough on its own.';
    }
    if (err && err.code === 3) {
      return 'Timed out waiting for a fix. Try again outdoors with a view of the sky.';
    }
    return 'Location unavailable: ' + (message || 'refused');
  }

  /* ── heading from a walked baseline ────────────────────────
     The cheapest real fix for the compass problem, and the one that works in
     open country where there is nothing to look at. Two positions a few
     metres apart give a bearing to well under a degree; the session's own
     tracking says which way that was in local terms. The difference is the
     yaw offset, without a magnetometer anywhere in it.

     It needs the user to walk in a straight line, which is a real imposition
     — but it is the only heading source that costs nothing and works under
     trees. */

  function headingFromBaseline(a, b) {
    if (!a || !b) { return null; }

    var separation = geo.haversine(a.position.lat, a.position.lon, b.position.lat, b.position.lon);
    // Too short a baseline and the position noise dominates the bearing: at
    // five metres apart, a two-metre error is twenty degrees of heading.
    var noise = Math.max(a.accuracy.positionM, b.accuracy.positionM);
    if (separation < Math.max(5, noise * 2)) { return null; }

    var enu = geo.toEnu(b.position.lat, b.position.lon, 0, a.position);
    var worldBearing = (Math.atan2(enu.e, enu.n) * 180 / Math.PI + 360) % 360;

    var localBearing = null;
    if (a.local && b.local) {
      var dx = b.local.x - a.local.x;
      var dz = b.local.z - a.local.z;
      var travelled = Math.hypot(dx, dz);

      // The session has to have seen the same walk. If it did not — tracking
      // was lost, or the device moved without the camera agreeing, as in a
      // vehicle — then the two bearings describe different journeys and
      // subtracting them produces a confident, meaningless number.
      if (travelled < separation * 0.5 || travelled > separation * 2) { return null; }

      var t = geo.threeToEnu({ x: dx, y: 0, z: dz });
      localBearing = (Math.atan2(t.e, t.n) * 180 / Math.PI + 360) % 360;
    }

    return {
      headingDeg: worldBearing,
      sessionYawDeg: localBearing === null ? null : ((worldBearing - localBearing + 360) % 360),
      separationM: separation,
      // Bearing error from position noise, small-angle: atan(noise/baseline).
      accuracyDeg: Math.atan2(noise, separation) * 180 / Math.PI
    };
  }

  return {
    makeFrame: makeFrame,
    gpsProvider: gpsProvider,
    headingFromBaseline: headingFromBaseline
  };
}));
