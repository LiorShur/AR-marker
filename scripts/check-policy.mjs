/* The Permissions-Policy header must not disable APIs the app actually calls.
 *
 * `geolocation=()` is an empty allowlist. It does not mean "ask the user" — it
 * means the API is off, the call fails, and no permission prompt is ever
 * shown. To anyone using the app that is indistinguishable from the feature
 * being broken, and to anyone reading the code it looks like a sensible
 * lockdown. This shipped, and cost an afternoon standing outdoors.
 *
 * The header only exists in the hosting config, so nothing in the app can
 * catch it; the dev server now mirrors it, and this catches the mismatch
 * before a deploy does.
 */

import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('..', import.meta.url));

// What the app calls, and the policy feature that governs it.
const FEATURES = [
  { feature: 'geolocation', pattern: /navigator\.geolocation/, api: 'navigator.geolocation' },
  { feature: 'camera', pattern: /getUserMedia/, api: 'getUserMedia' },
  { feature: 'xr-spatial-tracking', pattern: /navigator\.xr|enterAR\(/, api: 'WebXR' }
];

const SOURCES = ['app.js', 'spatial/geo.js', 'spatial/store.js', 'spatial/localize.js', 'spatial/world.js'];

const code = (await Promise.all(
  SOURCES.map((f) => readFile(join(ROOT, f), 'utf8').catch(() => ''))
)).join('\n');

const config = JSON.parse(await readFile(join(ROOT, 'firebase.json'), 'utf8'));

const policies = ((config.hosting && config.hosting.headers) || [])
  .flatMap((rule) => (rule.headers || []).map((h) => ({ source: rule.source, ...h })))
  .filter((h) => h.key.toLowerCase() === 'permissions-policy');

if (!policies.length) {
  console.log('no Permissions-Policy header set — nothing to contradict');
  process.exit(0);
}

// "camera=(self), geolocation=()" -> { camera: '(self)', geolocation: '()' }
const declared = {};
for (const policy of policies) {
  for (const part of policy.value.split(',')) {
    const [name, ...rest] = part.trim().split('=');
    if (name) { declared[name.trim()] = rest.join('=').trim(); }
  }
}

const problems = [];
for (const { feature, pattern, api } of FEATURES) {
  if (!pattern.test(code)) { continue; }

  const allowlist = declared[feature];
  if (allowlist === undefined) { continue; }        // absent means the default, which is self

  if (allowlist === '()' || allowlist === '') {
    problems.push(
      `${feature}=() disables ${api}, which the app calls. An empty allowlist is ` +
      `off, not "prompt" — use ${feature}=(self).`
    );
  }
}

for (const p of problems) { console.error('  ' + p); }
if (problems.length) { process.exit(1); }

const used = FEATURES.filter((f) => f.pattern.test(code)).map((f) => f.feature);
console.log(`Permissions-Policy allows every API in use: ${used.join(', ')}`);
