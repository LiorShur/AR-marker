#!/usr/bin/env node
//
// Put a placement in the database from a pair of coordinates.
//
// The app places things by aiming at them, which is the right way when you are
// standing there and hopeless when you are not. This is the other way: a
// latitude and longitude copied out of a map, and something waiting there when
// somebody arrives.
//
//   node scripts/place.mjs --lat -33.9249 --lon 18.4241 --scene beacon \
//                          --label "Table Mountain" --project markerone1965 \
//                          --key AIza...
//
// The project and key can come from the environment instead:
//
//   MARKERONE_PROJECT=markerone1965 MARKERONE_KEY=AIza... node scripts/place.mjs --lat … --lon …
//
// Getting coordinates from Google Maps: right-click the spot, and the first
// item in the menu is "lat, lon" — clicking it copies both.
//
// No altitude is asked for, and none is used. Vertical position comes from
// groundOffset, which is metres above the floor of whatever session is looking
// — so zero means "on the ground wherever the ground turns out to be", which
// is the only sensible answer when the height of a point on a map is unknown.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const geo = (await import(join(here, '..', 'spatial', 'geo.js'))).default
         ?? (await import(join(here, '..', 'spatial', 'geo.js')));

const IDENTITY = 'https://identitytoolkit.googleapis.com/v1';
const FIRESTORE = 'https://firestore.googleapis.com/v1';

function args(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) { continue; }
    const key = argv[i].slice(2);
    const value = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : 'true';
    out[key] = value;
  }
  return out;
}

function fail(why) {
  console.error(why);
  process.exit(1);
}

const a = args(process.argv);

const project = a.project || process.env.MARKERONE_PROJECT;
const key = a.key || process.env.MARKERONE_KEY;
const lat = Number(a.lat);
const lon = Number(a.lon);
const scene = a.scene || 'beacon';
const label = (a.label || '').slice(0, 40);
const heading = Number(a.heading || 0);
const scale = Number(a.scale || 1);

if (!project || !key) { fail('need --project and --key (or MARKERONE_PROJECT / MARKERONE_KEY)'); }
if (!Number.isFinite(lat) || lat < -90 || lat > 90) { fail('need a --lat between -90 and 90'); }
if (!Number.isFinite(lon) || lon < -180 || lon > 180) { fail('need a --lon between -180 and 180'); }

// Anonymous, like every other client. The rules ask who wrote a placement, not
// who is allowed to; anyone can mint one of these, which is why the write is
// validated by shape rather than by trust.
const signIn = await fetch(`${IDENTITY}/accounts:signUp?key=${encodeURIComponent(key)}`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ returnSecureToken: true })
});

if (!signIn.ok) { fail(`could not sign in: ${signIn.status} ${await signIn.text()}`); }
const who = await signIn.json();

const quaternion = geo.headingToQuaternion(heading);

// Firestore types every value on the wire, and gets the number cases wrong if
// you let it guess: an integer-valued double comes back as an integerValue and
// reading it without care turns 1.0 into a string.
const num = (v) => ({ doubleValue: v });
const str = (v) => ({ stringValue: v });

const document = {
  fields: {
    geopose: { mapValue: { fields: {
      position: { mapValue: { fields: {
        lat: num(lat), lon: num(lon), h: num(0)
      } } },
      quaternion: { mapValue: { fields: {
        x: num(quaternion.x), y: num(quaternion.y),
        z: num(quaternion.z), w: num(quaternion.w)
      } } }
    } } },
    geohash: str(geo.geohash(lat, lon, 10)),
    scene: str(scene),
    scale: num(scale),
    groundOffset: num(0),
    label: str(label),
    // Says how this got here, so a placement dropped from a map is
    // distinguishable from one somebody stood in front of and aimed at. The
    // accuracy is whatever the map was worth, which is not a number anyone
    // knows, so it is left at zero rather than invented.
    fix: { mapValue: { fields: {
      provider: str('map'), positionM: num(0), headingDeg: num(0)
    } } },
    visibility: str('public'),
    owner: str(who.localId),
    createdAt: str(new Date().toISOString())
  }
};

const write = await fetch(
  `${FIRESTORE}/projects/${project}/databases/(default)/documents/placements`,
  {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      authorization: `Bearer ${who.idToken}`
    },
    body: JSON.stringify(document)
  }
);

if (!write.ok) { fail(`could not place: ${write.status} ${await write.text()}`); }

const saved = await write.json();
const id = saved.name.split('/').pop();

console.log(`placed ${scene}${label ? ` · ${label}` : ''} at ${lat}, ${lon}`);
console.log(`  id     ${id}`);
console.log(`  owner  ${who.localId}`);
console.log(`  geohash ${geo.geohash(lat, lon, 10)}`);
console.log();
console.log('The owner is a fresh anonymous identity, so the app cannot delete this.');
console.log('Correct it in the app to claim it, or delete it as an admin.');
