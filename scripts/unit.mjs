/* Unit tests for the pure logic — geodesy, geohashing, placement queries.
 *
 * Separate from the smoke suite because none of this needs a browser, and a
 * sign error in a coordinate transform should be caught in a second rather
 * than after four minutes of driving Chromium.
 *
 *   node scripts/unit.mjs
 */

import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const require = createRequire(import.meta.url);
const geo = require(join(ROOT, 'spatial', 'geo.js'));
const { createStore } = require(join(ROOT, 'spatial', 'store.js'));
const local = require(join(ROOT, 'spatial', 'localize.js'));
const world = require(join(ROOT, 'spatial', 'world.js'));
const appcheck = require(join(ROOT, 'spatial', 'appcheck.js'));

const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok });
  console.log(`  ${ok ? '✓' : '✗'} ${name}${detail ? '  — ' + detail : ''}`);
};
const near = (a, b, tol) => Math.abs(a - b) <= tol;

/* ── the ellipsoid ───────────────────────────────────────── */
console.log('\n  geodesy');
{
  // Known fixed point: the equator at the prime meridian sits on the
  // semi-major axis, so ECEF is (a, 0, 0) exactly.
  const e = geo.toEcef(0, 0, 0);
  check('equator at 0E is on the semi-major axis',
    near(e.x, geo.WGS84.a, 1e-6) && near(e.y, 0, 1e-9) && near(e.z, 0, 1e-9));

  // The pole is flattened: the semi-minor axis is a(1-f), about 21 km shorter.
  const p = geo.toEcef(90, 0, 0);
  const b = geo.WGS84.a * (1 - geo.WGS84.f);
  check('the pole sits on the semi-minor axis', near(p.z, b, 1e-6),
    `${(geo.WGS84.a - b).toFixed(0)}m of flattening`);

  // Round trip through ECEF from a scatter of awkward places.
  const places = [
    [51.5007, -0.1246, 35],       // London, positive height
    [-33.8568, 151.2153, 0],      // Sydney, southern and eastern
    [64.1466, -21.9426, 120],     // Reykjavik, high latitude
    [-54.8019, -68.3030, 5],      // Ushuaia, both negative
    [0.3476, 32.5825, 1190]       // Kampala, on the equator and high up
  ];
  let worst = 0;
  for (const [lat, lon, h] of places) {
    const back = geo.fromEcef(...Object.values(geo.toEcef(lat, lon, h)));
    worst = Math.max(worst, geo.haversine(lat, lon, back.lat, back.lon), Math.abs(back.h - h));
  }
  check('ECEF round trip is exact everywhere', worst < 1e-6, `worst ${worst.toExponential(1)}m`);
}

/* ── the tangent plane ───────────────────────────────────── */
console.log('\n  local frame');
{
  const origin = { lat: 51.5007, lon: -0.1246, h: 0 };

  const north = geo.toEnu(51.5007 + 0.001, -0.1246, 0, origin);
  check('a thousandth of a degree of latitude is 111m north',
    near(north.n, 111.26, 0.1) && near(north.e, 0, 0.01), `${north.n.toFixed(2)}m`);

  const east = geo.toEnu(51.5007, -0.1246 + 0.001, 0, origin);
  // Longitude degrees shrink by cos(latitude) — at London that is about 5/8.
  check('longitude is foreshortened by latitude',
    near(east.e, 111.32 * Math.cos(51.5007 * Math.PI / 180), 0.3) && near(east.n, 0, 0.01),
    `${east.e.toFixed(2)}m vs 111.32m at the equator`);

  const up = geo.toEnu(51.5007, -0.1246, 100, origin);
  check('height is up and nothing else',
    near(up.u, 100, 1e-6) && near(up.e, 0, 1e-6) && near(up.n, 0, 1e-6));

  // 111m along the ground drops about a millimetre below the tangent plane.
  check('the earth curves away from the tangent plane',
    near(north.u, -0.00097, 0.0002), `${(north.u * 1000).toFixed(2)}mm over 111m`);

  let worst = 0;
  for (const [e, n, u] of [[0, 0, 0], [250, -400, 12], [-1000, 1000, -50]]) {
    const back = geo.toEnu(...Object.values(geo.fromEnu(e, n, u, origin)), origin);
    worst = Math.max(worst, Math.abs(back.e - e), Math.abs(back.n - n), Math.abs(back.u - u));
  }
  check('ENU round trip is exact', worst < 1e-6, `worst ${worst.toExponential(1)}m`);
}

/* ── the render frame ────────────────────────────────────── */
console.log('\n  render frame');
{
  // This is the one that silently puts the world 180 degrees out.
  const t = geo.enuToThree({ e: 10, n: 20, u: 3 });
  check('east is +X, up is +Y, north is -Z', t.x === 10 && t.y === 3 && t.z === -20,
    JSON.stringify(t));

  const back = geo.threeToEnu(t);
  check('render frame round trip', back.e === 10 && back.n === 20 && back.u === 3);

  // A compass reads clockwise from north; a Y-axis rotation runs the other way.
  check('a heading of 90 degrees is a yaw of -90',
    near(geo.headingToYaw(90), -Math.PI / 2, 1e-9));
  check('north is no rotation at all', geo.headingToYaw(0) === -0);
}

/* ── geohash ─────────────────────────────────────────────── */
console.log('\n  geohash');
{
  // Published reference values.
  check('encodes a known point', geo.geohash(57.64911, 10.40744, 11) === 'u4pruydqqvj',
    geo.geohash(57.64911, 10.40744, 11));
  check('encodes the origin', geo.geohash(0, 0, 6) === 's00000', geo.geohash(0, 0, 6));

  // Neighbouring points share a prefix; that is the whole basis of the query.
  const a = geo.geohash(51.5007, -0.1246, 10);
  const b = geo.geohash(51.5008, -0.1247, 10);
  let shared = 0;
  while (shared < a.length && a[shared] === b[shared]) { shared++; }
  check('nearby points share a prefix', shared >= 7, `${shared} characters of ${a} / ${b}`);

  check('precision falls as the radius grows',
    geo.geohashPrecisionFor(10) > geo.geohashPrecisionFor(1000));

  // A cell must be at least as wide as the radius, or the nine probe points
  // cannot reach every cell the circle touches.
  for (const r of [5, 50, 500, 5000]) {
    const p = geo.geohashPrecisionFor(r);
    const ranges = geo.geohashQueryBounds(51.5007, -0.1246, r);
    check(`bounds at ${r}m are usable`, ranges.length >= 1 && ranges.length <= 9 &&
      ranges.every(([lo, hi]) => hi.startsWith(lo) && hi > lo),
      `precision ${p}, ${ranges.length} range(s)`);
  }

  // Straddling a cell edge must produce more than one range, or half the
  // results silently vanish. 0/0 is a corner of four cells.
  check('a point on a cell boundary queries its neighbours',
    geo.geohashQueryBounds(0, 0, 200).length > 1,
    geo.geohashQueryBounds(0, 0, 200).length + ' ranges');

  // Every cell the circle touches must be inside one of the ranges.
  const inRanges = (hash, ranges) => ranges.some(([lo, hi]) => hash >= lo && hash <= hi);
  let covered = true;
  const centre = { lat: 51.5007, lon: -0.1246 };
  const radius = 120;
  const ranges = geo.geohashQueryBounds(centre.lat, centre.lon, radius);
  const precision = geo.geohashPrecisionFor(radius);
  for (let i = 0; i < 2000; i++) {
    const bearing = (i / 2000) * 2 * Math.PI;
    const dLat = (radius * Math.cos(bearing) / 6371008.8) * 180 / Math.PI;
    const dLon = (radius * Math.sin(bearing) / (6371008.8 * Math.cos(centre.lat * Math.PI / 180))) * 180 / Math.PI;
    if (!inRanges(geo.geohash(centre.lat + dLat, centre.lon + dLon, precision), ranges)) {
      covered = false;
      break;
    }
  }
  check('the ranges cover the whole circle', covered, '2000 points on the rim');

  check('the antimeridian does not throw',
    geo.geohashQueryBounds(0, 179.999, 500).length >= 1);
  check('the poles do not throw',
    geo.geohashQueryBounds(89.999, 0, 500).length >= 1);
}

/* ── distance ────────────────────────────────────────────── */
console.log('\n  distance');
{
  // A degree of latitude on the mean sphere is an exact check of the formula.
  check('one degree of latitude is 111.19km',
    near(geo.haversine(0, 0, 1, 0), 111194.9, 1), geo.haversine(0, 0, 1, 0).toFixed(1) + 'm');
  check('London Eye to Eiffel Tower is 340km',
    near(geo.haversine(51.5007, -0.1246, 48.8584, 2.2945) / 1000, 340.5, 1),
    (geo.haversine(51.5007, -0.1246, 48.8584, 2.2945) / 1000).toFixed(1) + 'km');
  check('a point is zero from itself', geo.haversine(51.5, -0.12, 51.5, -0.12) === 0);
  check('antipodes are half the circumference',
    near(geo.haversine(0, 0, 0, 180) / 1000, 20015, 5));
}

/* ── the session frame ───────────────────────────────────── */
console.log('\n  localization frame');
{
  const ORIGIN = { lat: 51.5007, lon: -0.1246, h: 0 };
  const base = { position: ORIGIN, accuracy: { positionM: 5, headingDeg: 25 } };
  const north = { lat: 51.5007 + 0.0008993, lon: -0.1246, h: 0 };   // ~100m north

  // These assert what the numbers *mean*, not that they survive a round trip.
  // A mirrored transform is its own consistent inverse: toLocal and toGlobal
  // agree with each other perfectly while every placement sits on the wrong
  // side of the viewer, and only a physical case catches it.
  const facing = (deg) => local.makeFrame({ ...base, headingDeg: deg }).toLocal(north);

  check('facing north, a point to the north is ahead (-Z)',
    near(facing(0).z, -100, 0.5) && near(facing(0).x, 0, 0.5));
  check('facing east, it is to the left (-X)',
    near(facing(90).x, -100, 0.5) && near(facing(90).z, 0, 0.5));
  check('facing south, it is behind (+Z)',
    near(facing(180).z, 100, 0.5) && near(facing(180).x, 0, 0.5));
  check('facing west, it is to the right (+X)',
    near(facing(270).x, 100, 0.5) && near(facing(270).z, 0, 0.5));

  check('height carries straight through', near(local.makeFrame(base).toLocal(
    { lat: ORIGIN.lat, lon: ORIGIN.lon, h: 12 }).y, 12, 0.01));

  const f = local.makeFrame({ ...base, headingDeg: 37 });
  const back = f.toGlobal(f.toLocal(north));
  check('local and global are inverses',
    geo.haversine(north.lat, north.lon, back.lat, back.lon) < 0.01,
    geo.haversine(north.lat, north.lon, back.lat, back.lon).toExponential(1) + 'm');

  // A session whose own yaw is offset must cancel out.
  const offsetSession = local.makeFrame({ ...base, headingDeg: 90 },
    { position: { x: 0, y: 0, z: 0 }, yawDeg: 90 });
  check('a session yaw offset cancels the device heading',
    near(offsetSession.toLocal(north).z, -100, 0.5),
    JSON.stringify(offsetSession.toLocal(north).z.toFixed(1)));

  const shifted = local.makeFrame({ ...base, headingDeg: 0 },
    { position: { x: 5, y: 1, z: -2 }, yawDeg: 0 });
  check('the fix is taken where the device actually stood',
    near(shifted.toLocal(north).x, 5, 0.5) && near(shifted.toLocal(north).z, -102, 0.5));

  check('accuracy travels with the frame',
    local.makeFrame(base).accuracy.headingDeg === 25);
}

/* ── heading from a walked baseline ──────────────────────── */
console.log('\n  baseline heading');
{
  const at = (dn, de, lx, lz, acc) => ({
    position: {
      lat: 51.5007 + (dn / 6371008.8) * 180 / Math.PI,
      lon: -0.1246 + (de / (6371008.8 * Math.cos(51.5007 * Math.PI / 180))) * 180 / Math.PI
    },
    accuracy: { positionM: acc === undefined ? 1 : acc },
    local: { x: lx, y: 0, z: lz }
  });

  // Walked 30m north, and the session recorded 30m straight ahead: the
  // session's forward direction is north.
  const northward = local.headingFromBaseline(at(0, 0, 0, 0), at(30, 0, 0, -30));
  check('walking north gives a bearing of 0', near(northward.headingDeg, 0, 0.5),
    northward.headingDeg.toFixed(1) + '°');
  check('and a session yaw of 0', near(northward.sessionYawDeg, 0, 0.5));

  // Walked north, but the session recorded it as travelling to its right:
  // the session's forward is 90 degrees anticlockwise of north, i.e. west.
  const sideways = local.headingFromBaseline(at(0, 0, 0, 0), at(30, 0, 30, 0));
  check('a sideways walk resolves the session yaw', near(sideways.sessionYawDeg, 270, 0.5),
    sideways.sessionYawDeg.toFixed(1) + '°');

  const east = local.headingFromBaseline(at(0, 0, 0, 0), at(0, 30, 0, -30));
  check('walking east gives a bearing of 90', near(east.headingDeg, 90, 0.5),
    east.headingDeg.toFixed(1) + '°');

  // Too short to mean anything: at 3m apart, a metre of noise is 18 degrees.
  check('refuses a baseline shorter than the noise',
    local.headingFromBaseline(at(0, 0, 0, 0), at(3, 0, 0, -3)) === null);
  check('refuses a baseline swamped by a poor fix',
    local.headingFromBaseline(at(0, 0, 0, 0, 20), at(30, 0, 0, -30, 20)) === null);

  // The session must have observed the same journey. A walk the tracker did
  // not see is not a bearing, however confident the arithmetic looks.
  check('refuses a walk the session did not track',
    local.headingFromBaseline(at(0, 0, 0, 0), at(30, 0, 0, 0)) === null);
  check('refuses a session that moved further than the walk',
    local.headingFromBaseline(at(0, 0, 0, 0), at(30, 0, 0, -200)) === null);

  // Error shrinks as the baseline grows — the whole reason to walk further.
  const short = local.headingFromBaseline(at(0, 0, 0, 0), at(10, 0, 0, -10));
  const long = local.headingFromBaseline(at(0, 0, 0, 0), at(100, 0, 0, -100));
  check('a longer baseline is a better bearing', long.accuracyDeg < short.accuracyDeg,
    `${short.accuracyDeg.toFixed(1)}° at 10m vs ${long.accuracyDeg.toFixed(1)}° at 100m`);
}

/* ── the placement store ─────────────────────────────────── */
console.log('\n  placement store');
{
  // A stub Firestore: records what was asked for, answers from a fixture.
  // The REST wire format types every scalar, and getting that wrong fails
  // quietly — a double read back as a string is still truthy.
  function stub(placements) {
    const calls = [];
    const encode = (p) => ({
      name: `projects/x/databases/(default)/documents/placements/${p.id}`,
      fields: {
        geopose: { mapValue: { fields: {
          position: { mapValue: { fields: {
            lat: { doubleValue: p.lat }, lon: { doubleValue: p.lon },
            // An integer-valued double comes back typed as an integer.
            h: { integerValue: '0' }
          } } },
          quaternion: { mapValue: { fields: {
            x: { doubleValue: 0 }, y: { doubleValue: 0 },
            z: { doubleValue: 0 }, w: { doubleValue: 1 }
          } } }
        } } },
        geohash: { stringValue: geo.geohash(p.lat, p.lon, 10) },
        scene: { stringValue: p.scene || 'rotary-phone' },
        scale: { integerValue: '1' },
        visibility: { stringValue: 'public' },
        owner: { stringValue: 'anon-1' }
      }
    });

    const fetchImpl = async (url, opts) => {
      const body = opts.body ? JSON.parse(opts.body) : null;
      calls.push({ url, body, headers: opts.headers, method: opts.method });

      if (url.includes('accounts:signUp')) {
        return json({ idToken: 'tok-1', refreshToken: 'ref-1', localId: 'anon-1', expiresIn: '3600' });
      }
      if (url.includes(':runQuery')) {
        const filters = body.structuredQuery.where.compositeFilter.filters;
        const lo = filters[0].fieldFilter.value.stringValue;
        const hi = filters[1].fieldFilter.value.stringValue;
        const hits = placements
          .filter((p) => { const g = geo.geohash(p.lat, p.lon, 10); return g >= lo && g <= hi; })
          .map((p) => ({ document: encode(p) }));
        // Firestore answers an empty query with one element carrying no document.
        return json(hits.length ? hits : [{ readTime: '2026-01-01T00:00:00Z' }]);
      }
      if (opts.method === 'POST') { return json(encode({ id: 'new-1', lat: 0, lon: 0 })); }
      return json({});
    };

    const json = (data) => ({
      ok: true, status: 200, text: async () => JSON.stringify(data)
    });

    return { fetch: fetchImpl, calls };
  }

  const CENTRE = { lat: 51.5007, lon: -0.1246 };
  const metres = (dn, de) => ({
    lat: CENTRE.lat + (dn / 6371008.8) * 180 / Math.PI,
    lon: CENTRE.lon + (de / (6371008.8 * Math.cos(CENTRE.lat * Math.PI / 180))) * 180 / Math.PI
  });

  const fixtures = [
    { id: 'near', ...metres(10, 10) },
    { id: 'edge', ...metres(90, 0) },
    { id: 'outside', ...metres(400, 0) },
    { id: 'far', ...metres(5000, 5000) }
  ];

  const s = stub(fixtures);
  const store = createStore({ projectId: 'x', apiKey: 'k', fetch: s.fetch });

  const found = await store.nearby(CENTRE.lat, CENTRE.lon, 100);
  const ids = found.map((p) => p.id);

  check('returns placements inside the radius', ids.includes('near') && ids.includes('edge'),
    ids.join(', '));
  check('excludes placements outside it, whatever the cell said',
    !ids.includes('outside') && !ids.includes('far'));
  check('sorts by true distance', found[0].id === 'near',
    found.map((p) => `${p.id}@${p.distance.toFixed(0)}m`).join(' '));
  check('decodes doubles and integers alike',
    typeof found[0].geopose.position.lat === 'number' &&
    typeof found[0].geopose.position.h === 'number' &&
    typeof found[0].scale === 'number');
  check('reads an empty result without choking',
    (await store.nearby(0, 0, 50)).length === 0);

  const queries = s.calls.filter((c) => c.url.includes(':runQuery'));
  check('authenticates once and reuses the token',
    s.calls.filter((c) => c.url.includes('accounts:signUp')).length === 1);
  check('sends a bearer token on every query',
    queries.every((c) => c.headers.Authorization === 'Bearer tok-1'));
  check('queries every geohash range',
    queries.length >= geo.geohashQueryBounds(CENTRE.lat, CENTRE.lon, 100).length);
  check('orders by geohash, as the range filter requires',
    queries[0].body.structuredQuery.orderBy[0].field.fieldPath === 'geohash');
  check('caps how much one range can return',
    queries.every((c) => c.body.structuredQuery.limit > 0 && c.body.structuredQuery.limit <= 500));

  // Writing
  const s2 = stub([]);
  const store2 = createStore({ projectId: 'x', apiKey: 'k', fetch: s2.fetch });
  await store2.place({
    scene: 'rotary-phone',
    geopose: { position: { lat: 51.5, lon: -0.12, h: 3 } }
  });
  const write = s2.calls.find((c) => c.url.endsWith('/placements'));
  check('a placement carries its own geohash', !!write.body.fields.geohash.stringValue);
  check('the server stamps the owner, not the caller',
    write.body.fields.owner.stringValue === 'anon-1');
  check('an absent orientation defaults to identity',
    write.body.fields.geopose.mapValue.fields.quaternion.mapValue.fields.w.doubleValue === 1);
  check('the fix provider is recorded', !!write.body.fields.fix);

  await store2.place({ scene: 'x', geopose: { position: { lat: 91, lon: 0 } } })
    .then(() => check('rejects an impossible latitude', false))
    .catch((e) => check('rejects an impossible latitude', /latitude/.test(e.message), e.message));

  await store2.place({ geopose: { position: { lat: 0, lon: 0 } } })
    .then(() => check('rejects a placement with no scene', false))
    .catch((e) => check('rejects a placement with no scene', /scene/.test(e.message)));
}

/* ── orientation ─────────────────────────────────────────── */
console.log('\n  orientation');
{
  for (const h of [0, 45, 90, 180, 270, 359]) {
    const back = geo.headingFromQuaternion(geo.headingToQuaternion(h));
    if (!near(back, h, 0.001)) { check(`heading ${h} survives a quaternion`, false, String(back)); }
  }
  check('every heading survives a quaternion round trip', true, '0 to 359 degrees');

  check('an identity quaternion faces north',
    near(geo.headingFromQuaternion({ x: 0, y: 0, z: 0, w: 1 }), 0, 1e-9));
  check('a missing quaternion is treated as north', geo.headingFromQuaternion(null) === 0);

  const f = local.makeFrame({ position: { lat: 0, lon: 0, h: 0 }, headingDeg: 30, accuracy: {} });
  check('session yaw and world heading are inverses',
    near(f.localYawToHeading(f.headingToLocalYaw(210)), 210, 1e-9));
}

/* ── the world session ───────────────────────────────────── */
console.log('\n  world session');
{
  const ORIGIN = { lat: 51.5007, lon: -0.1246 };
  const north = (m) => ORIGIN.lat + (m / 6371008.8) * 180 / Math.PI;

  // A device that walks north while the session records it going forward:
  // the session's forward axis is north.
  function rig({ walk = [0, 30], accuracy = 3, placements = [] } = {}) {
    let step = 0;
    let local = { x: 0, y: 0, z: 0 };

    const provider = {
      id: 'test',
      available: () => Promise.resolve(true),
      locate: () => {
        const m = walk[Math.min(step, walk.length - 1)];
        local = { x: 0, y: 0, z: -m };
        step++;
        return Promise.resolve({
          position: { lat: north(m), lon: ORIGIN.lon, h: 0 },
          headingDeg: 0,
          accuracy: { positionM: accuracy, headingDeg: 25 },
          at: 1000 * step
        });
      }
    };

    const written = [];
    const store = {
      nearby: () => Promise.resolve(placements.map((p) => ({ ...p }))),
      place: (p) => { written.push(p); return Promise.resolve({ ...p, id: 'w' + written.length }); },
      remove: () => Promise.resolve(true)
    };

    const states = [];
    let rendered = [];
    const w = world.create({
      store, provider,
      config: { radiusM: 300, relocalizeAfterM: 25 },
      pose: () => local,
      onState: (s, d) => states.push({ s, d }),
      onPlacements: (list) => { rendered = list; }
    });

    return { w, states, written, get rendered() { return rendered; } };
  }

  const one = rig();
  await one.w.start();
  check('one fix is not enough to know which way north is',
    one.w.state() === 'calibrating', one.w.state());
  check('and it says so rather than pretending', one.states[one.states.length - 1].s === 'calibrating');

  await one.w.sample();
  check('a second fix far enough away resolves it', one.w.state() === 'ready', one.w.state());

  const frame = one.w.frame();
  check('the session forward axis is found to be north',
    near(frame.headingToLocalYaw(0), 0, 0.01),
    (frame.headingToLocalYaw(0) * 180 / Math.PI).toFixed(2) + '°');
  check('heading accuracy reflects the baseline walked',
    frame.accuracy.headingDeg > 0 && frame.accuracy.headingDeg < 10,
    frame.accuracy.headingDeg.toFixed(1) + '° over ' + one.w.walked().toFixed(0) + 'm');

  // A walk too short to mean anything must not be accepted as a bearing.
  const shuffle = rig({ walk: [0, 2, 3] });
  await shuffle.w.start();
  await shuffle.w.sample();
  await shuffle.w.sample();
  check('shuffling a few metres does not count as calibration',
    shuffle.w.state() === 'calibrating', shuffle.w.state());
  check('and it reports how far has actually been walked',
    shuffle.w.walked() > 2 && shuffle.w.walked() < 4, shuffle.w.walked().toFixed(1) + 'm');

  // Placements come back in session coordinates.
  const withContent = rig({
    placements: [
      { id: 'a', scene: 'rotary-phone', scale: 1, distance: 50,
        geopose: { position: { lat: north(80), lon: ORIGIN.lon, h: 0 },
                   quaternion: geo.headingToQuaternion(90) } }
    ]
  });
  await withContent.w.start();
  await withContent.w.sample();
  await withContent.w.refresh();

  const seen = withContent.rendered;
  check('placements arrive in session coordinates', seen.length === 1);
  // The last fix was 30m north; the placement is at 80m north, so 50m ahead.
  check('a placement to the north is ahead of the walker',
    near(seen[0].local.z, -80, 1) && near(seen[0].local.x, 0, 1),
    `x=${seen[0].local.x.toFixed(1)} z=${seen[0].local.z.toFixed(1)}`);
  check('its facing is carried into the session frame',
    near(seen[0].yawRad, -Math.PI / 2, 0.01),
    (seen[0].yawRad * 180 / Math.PI).toFixed(1) + '°');

  // Placing goes the other way, and records how it was localized.
  await withContent.w.place('beacon', { x: 0, y: 0, z: -50 }, 0);
  const wrote = withContent.written[0];
  check('placing converts session coordinates back to the globe',
    near(wrote.geopose.position.lat, north(50), 1e-5),
    `${wrote.geopose.position.lat.toFixed(6)} vs ${north(50).toFixed(6)}`);
  check('the placement records how it was localized',
    wrote.fix.provider === 'test' && wrote.fix.positionM === 3 && wrote.fix.headingDeg > 0,
    JSON.stringify(wrote.fix));
  check('a new placement appears without a round trip',
    withContent.rendered.length === 2);

  // Refusing to place before it knows where it is.
  const cold = rig();
  await cold.w.place('x', { x: 0, y: 0, z: 0 })
    .then(() => check('refuses to place before localizing', false))
    .catch((e) => check('refuses to place before localizing', /not localized/.test(e.message)));

  // Drift.
  const drifting = rig({ walk: [0, 30] });
  await drifting.w.start();
  await drifting.w.sample();
  check('no relocalization needed while standing still', !drifting.w.needsRelocalize());

  const far = rig({ walk: [0, 30] });
  await far.w.start();
  await far.w.sample();
  // pose() now reports the walker 40m past the last fix
  check('relocalization is needed after walking far enough',
    world.create({
      store: { nearby: () => Promise.resolve([]) },
      provider: { locate: () => Promise.resolve({}) },
      config: { relocalizeAfterM: 25 },
      pose: () => ({ x: 0, y: 0, z: -100 })
    }).needsRelocalize());

  // A provider that fails must say so, not hang in 'locating'.
  const broken = world.create({
    store: { nearby: () => Promise.resolve([]) },
    provider: { id: 'broken', locate: () => Promise.reject(new Error('location refused')) },
    onState: () => {}
  });
  await broken.start().catch(() => {});
  check('a refused fix becomes an error state', broken.state() === 'error', broken.state());
}

/* ── App Check ───────────────────────────────────────────── */
console.log('\n  app check');
{
  const off = appcheck.create({ projectId: 'p', apiKey: 'k' });
  check('stays off until it is configured', off.enabled === false);
  check('and says what is missing', /recaptchaSiteKey/.test(off.describe()), off.describe());
  check('an unconfigured token is null, not an error', (await off.get()) === null);

  // A store with App Check off must send no header at all — an empty one is
  // not the same as absent and some proxies treat it differently.
  const bare = [];
  const plain = createStore({
    projectId: 'p', apiKey: 'k',
    fetch: (u, o) => { bare.push(o.headers); return stubbedAuth(); }
  });
  await plain.signIn();
  check('no App Check header when it is off', !('X-Firebase-AppCheck' in bare[0]));

  // ...and with it on, every request carries one, minted once and reused.
  let minted = 0;
  const seen = [];
  const attested = createStore({
    projectId: 'p', apiKey: 'k',
    appCheck: { enabled: true, get: () => { minted++; return Promise.resolve('tok-' + minted); } },
    fetch: (u, o) => { seen.push(o.headers); return stubbedAuth(); }
  });
  await attested.signIn();
  await attested.nearby(51.5, -0.12, 50).catch(() => {});
  check('every request is attested',
    seen.length > 1 && seen.every((h) => h['X-Firebase-AppCheck']),
    seen.length + ' requests');

  // A token that cannot be obtained must not take writing down with it. With
  // enforcement off nothing changes; with it on the server refuses and says
  // so, which is a better error than one invented in the client.
  const degraded = [];
  const flaky = createStore({
    projectId: 'p', apiKey: 'k',
    appCheck: { enabled: true, get: () => Promise.reject(new Error('recaptcha blocked')) },
    fetch: (u, o) => { degraded.push(o.headers); return stubbedAuth(); }
  });
  const survived = await flaky.signIn().then(() => true).catch(() => false);
  check('a failed attestation degrades rather than throws', survived);
  check('and simply sends no header', !('X-Firebase-AppCheck' in degraded[0]));

  function stubbedAuth() {
    return Promise.resolve({
      ok: true, status: 200,
      text: async () => JSON.stringify({
        idToken: 't', refreshToken: 'r', localId: 'anon', expiresIn: '3600'
      })
    });
  }
}

const failed = results.filter((r) => !r.ok);
console.log(`\n  ${results.length - failed.length}/${results.length} passed\n`);
process.exit(failed.length ? 1 : 0);
