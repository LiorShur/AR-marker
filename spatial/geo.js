/* Geodesy for placing things in the world.
 *
 * Everything the app stores is in one global frame — WGS84 latitude,
 * longitude and height, plus an orientation expressed in the local
 * East-North-Up frame at that point. Whatever localises the device (GPS,
 * a visual positioning service, an RTK receiver, ARCore's Geospatial API)
 * only has to report a pose in that same frame, and content placed by one
 * provider is readable by another.
 *
 * The conversions here are the bridge between that frame and the metres-and-
 * Y-up frame a WebXR session actually renders in.
 *
 * Loadable as a plain script (defines window.SpatialGeo) or required from
 * Node for the tests — the maths is pure and worth testing without a browser.
 */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) { module.exports = api; }
  else { root.SpatialGeo = api; }
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // WGS84
  var A = 6378137.0;                       // semi-major axis, metres
  var F = 1 / 298.257223563;               // flattening
  var E2 = F * (2 - F);                    // first eccentricity squared

  var D2R = Math.PI / 180;
  var R2D = 180 / Math.PI;

  /* ── ellipsoid ───────────────────────────────────────────── */

  // Geodetic to earth-centred, earth-fixed. Height is above the ellipsoid,
  // which is NOT the same as height above sea level — the geoid separation
  // is tens of metres in places. For AR it rarely matters, because content is
  // placed and viewed with the same convention; it matters enormously if you
  // ever mix in an elevation service.
  function toEcef(lat, lon, h) {
    var sinLat = Math.sin(lat * D2R);
    var cosLat = Math.cos(lat * D2R);
    var sinLon = Math.sin(lon * D2R);
    var cosLon = Math.cos(lon * D2R);
    var n = A / Math.sqrt(1 - E2 * sinLat * sinLat);

    return {
      x: (n + h) * cosLat * cosLon,
      y: (n + h) * cosLat * sinLon,
      z: (n * (1 - E2) + h) * sinLat
    };
  }

  // Bowring's method, iterated. Converges in two or three passes for any
  // height a phone will ever be at.
  function fromEcef(x, y, z) {
    var lon = Math.atan2(y, x);
    var p = Math.hypot(x, y);
    var lat = Math.atan2(z, p * (1 - E2));

    var n = A;
    for (var i = 0; i < 5; i++) {
      var sinLat = Math.sin(lat);
      n = A / Math.sqrt(1 - E2 * sinLat * sinLat);
      lat = Math.atan2(z + E2 * n * sinLat, p);
    }

    return {
      lat: lat * R2D,
      lon: lon * R2D,
      h: p / Math.cos(lat) - n
    };
  }

  /* ── local tangent plane ─────────────────────────────────── */

  // ENU is metres east, north and up from an origin on the ellipsoid. Over
  // the few hundred metres an AR session spans it is flat enough to treat as
  // Cartesian, which is what makes it the right bridge to a render frame.
  function toEnu(lat, lon, h, origin) {
    var p = toEcef(lat, lon, h);
    var o = toEcef(origin.lat, origin.lon, origin.h || 0);

    var dx = p.x - o.x;
    var dy = p.y - o.y;
    var dz = p.z - o.z;

    var sinLat = Math.sin(origin.lat * D2R);
    var cosLat = Math.cos(origin.lat * D2R);
    var sinLon = Math.sin(origin.lon * D2R);
    var cosLon = Math.cos(origin.lon * D2R);

    return {
      e: -sinLon * dx + cosLon * dy,
      n: -sinLat * cosLon * dx - sinLat * sinLon * dy + cosLat * dz,
      u: cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz
    };
  }

  function fromEnu(e, n, u, origin) {
    var o = toEcef(origin.lat, origin.lon, origin.h || 0);

    var sinLat = Math.sin(origin.lat * D2R);
    var cosLat = Math.cos(origin.lat * D2R);
    var sinLon = Math.sin(origin.lon * D2R);
    var cosLon = Math.cos(origin.lon * D2R);

    return fromEcef(
      o.x - sinLon * e - sinLat * cosLon * n + cosLat * cosLon * u,
      o.y + cosLon * e - sinLat * sinLon * n + cosLat * sinLon * u,
      o.z + cosLat * n + sinLat * u
    );
  }

  /* ── render frame ────────────────────────────────────────── */

  // WebXR and three.js are Y-up and right-handed with -Z forward. Mapping
  // East to +X and Up to +Y leaves North on -Z. Getting this backwards puts
  // everything in the world 180 degrees out, which looks exactly like a
  // compass error and is not.
  function enuToThree(enu) {
    return { x: enu.e, y: enu.u, z: -enu.n };
  }

  function threeToEnu(v) {
    return { e: v.x, n: -v.z, u: v.y };
  }

  // A yaw measured clockwise from north — which is what every compass, and
  // every geospatial API, reports — as a rotation about the render frame's
  // Y axis, which runs anticlockwise seen from above.
  function headingToYaw(degreesFromNorth) {
    return -degreesFromNorth * D2R;
  }

  /* ── distance ────────────────────────────────────────────── */

  var EARTH_MEAN_R = 6371008.8;

  function haversine(latA, lonA, latB, lonB) {
    var dLat = (latB - latA) * D2R;
    var dLon = (lonB - lonA) * D2R;
    var s = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
            Math.cos(latA * D2R) * Math.cos(latB * D2R) *
            Math.sin(dLon / 2) * Math.sin(dLon / 2);
    return 2 * EARTH_MEAN_R * Math.asin(Math.min(1, Math.sqrt(s)));
  }

  /* ── geohash ─────────────────────────────────────────────── */

  // Firestore has no geospatial index. The standard workaround: interleave
  // latitude and longitude bits into one sortable string, so a box on the
  // globe becomes a handful of string ranges. Precision 10 is roughly a
  // metre, which is finer than anything placing this content will manage.
  var BASE32 = '0123456789bcdefghjkmnpqrstuvwxyz';

  function encode(lat, lon, precision) {
    precision = precision || 10;

    var latRange = [-90, 90];
    var lonRange = [-180, 180];
    var hash = '';
    var bits = 0;
    var bit = 0;
    var even = true;

    while (hash.length < precision) {
      var range = even ? lonRange : latRange;
      var mid = (range[0] + range[1]) / 2;
      var value = even ? lon : lat;

      if (value >= mid) { bits = (bits << 1) + 1; range[0] = mid; }
      else { bits = bits << 1; range[1] = mid; }

      even = !even;
      if (++bit === 5) {
        hash += BASE32[bits];
        bit = 0;
        bits = 0;
      }
    }
    return hash;
  }

  // Metres per unit of geohash precision, alternating because each character
  // splits longitude three times and latitude twice, or the reverse.
  var CELL_WIDTH = [5009400, 1252300, 156500, 39100, 4900, 1220, 152.9, 38.2, 4.8, 1.2, 0.149];

  function precisionFor(radiusM) {
    for (var i = CELL_WIDTH.length - 1; i > 0; i--) {
      if (CELL_WIDTH[i] >= radiusM) { return i; }
    }
    return 1;
  }

  // The ranges that cover a circle. Neighbouring cells are needed because the
  // circle almost never sits inside one — querying only the centre's own cell
  // silently loses anything near its edge, which is most things.
  function queryBounds(lat, lon, radiusM) {
    var precision = precisionFor(radiusM);

    // Degrees of latitude are constant; degrees of longitude shrink towards
    // the poles, and the cosine goes to zero at them.
    var dLat = (radiusM / EARTH_MEAN_R) * R2D;
    var cos = Math.cos(lat * D2R);
    var dLon = Math.abs(cos) < 1e-6 ? 180 : (radiusM / (EARTH_MEAN_R * cos)) * R2D;

    var seen = {};
    var ranges = [];

    for (var i = -1; i <= 1; i++) {
      for (var j = -1; j <= 1; j++) {
        var y = clampLat(lat + i * dLat);
        var x = wrapLon(lon + j * dLon);
        var cell = encode(y, x, precision);
        if (seen[cell]) { continue; }
        seen[cell] = true;
        // '~' sorts above every base32 character, so [cell, cell~] is
        // "every hash starting with this prefix".
        ranges.push([cell, cell + '~']);
      }
    }
    return ranges;
  }

  function clampLat(lat) { return Math.max(-90, Math.min(90, lat)); }

  function wrapLon(lon) {
    while (lon > 180) { lon -= 360; }
    while (lon < -180) { lon += 360; }
    return lon;
  }

  return {
    toEcef: toEcef,
    fromEcef: fromEcef,
    toEnu: toEnu,
    fromEnu: fromEnu,
    enuToThree: enuToThree,
    threeToEnu: threeToEnu,
    headingToYaw: headingToYaw,
    haversine: haversine,
    geohash: encode,
    geohashPrecisionFor: precisionFor,
    geohashQueryBounds: queryBounds,
    WGS84: { a: A, f: F, e2: E2 }
  };
}));
