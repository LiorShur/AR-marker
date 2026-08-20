/* Every path the service worker precaches must exist.
 *
 * caches.addAll() rejects atomically: one missing file and the whole install
 * fails, the worker never activates, and the app quietly stops working
 * offline with no error surfaced anywhere. Renaming a vendored file without
 * updating sw.js is exactly how that happens.
 */

import { readFile, access } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const sw = await readFile(join(ROOT, 'sw.js'), 'utf8');

const list = sw.match(/var ASSETS = \[([\s\S]*?)\];/);
if (!list) {
  console.error('could not find the ASSETS array in sw.js');
  process.exit(1);
}

const paths = [...list[1].matchAll(/'([^']+)'/g)]
  .map((m) => m[1])
  .filter((p) => p !== './');                  // the navigation URL, not a file

const missing = [];
for (const p of paths) {
  // Strip the cache-busting query before looking on disk.
  const file = join(ROOT, p.split('?')[0]);
  try {
    await access(file);
  } catch {
    missing.push(p);
  }
}

// The ?v= on the precached shell has to match what index.html actually asks
// for, or the entry is cached under a key nothing ever requests.
const html = await readFile(join(ROOT, 'index.html'), 'utf8');
const mismatched = paths
  .filter((p) => p.includes('?v='))
  .filter((p) => !html.includes(p));

// The build stamp exists to answer "am I running what I just deployed", which
// it cannot do if it is bumped separately from the assets it describes.
const app = await readFile(join(ROOT, 'app.js'), 'utf8');
const build = (app.match(/var BUILD = '([^']+)'/) || [])[1];
const assetVersion = (html.match(/app\.js\?v=([^"']+)/) || [])[1];
if (build !== assetVersion) {
  console.error(`build stamp is '${build}' but index.html asks for app.js?v=${assetVersion}`);
}

for (const p of missing) console.error(`missing: ${p}`);
for (const p of mismatched) console.error(`precached but not referenced by index.html: ${p}`);

if (missing.length || mismatched.length || build !== assetVersion) { process.exit(1); }
console.log(`${paths.length} precached assets present and referenced, build ${build}`);
