/* Precache everything. All assets are same-origin and vendored,
   so the app runs with the network off once installed.

   Updating: bump CACHE here, BUILD in app.js, and the ?v= on every asset in
   index.html, together — scripts/check-precache.mjs enforces that they agree. install() calls skipWaiting and activate() calls claim, so a
   new worker takes over on the next load rather than the one after —
   provided the host serves this file with no-cache (see firebase.json). */
var CACHE = 'marker-one-v19';

// Precached at install. The natural-feature descriptors are deliberately not
// in here: they are 1.4 MB for a target most visitors will never print, and
// making every first load pay for them to save a second load is the wrong
// trade. They are cached on first use instead, along with anything else
// same-origin the app reaches for.
var ASSETS = [
  './',                       // the navigation URL is "/", not "/index.html"
  'index.html',
  'marker.html',
  'poster.html',
  'app.css?v=17',
  'app.js?v=17',
  'manifest.webmanifest',
  'content.json',
  'vendor/aframe.min.js',
  'vendor/aframe-ar-nft.js',
  'vendor/meshopt_decoder.js',
  'spatial/config.js?v=17',
  'spatial/geo.js?v=17',
  'spatial/store.js?v=17',
  'spatial/localize.js?v=17',
  'spatial/appcheck.js?v=17',
  'spatial/world.js?v=17',
  'assets/rotary-phone.glb',  // meshopt-compressed, and the whole point of the scene
  'data/patt.hiro',
  'data/camera_para.dat',
  'data/marker-hiro.png',
  'icons/icon-192.png',
  'icons/icon-512.png',
  'icons/icon-maskable-512.png'
];

self.addEventListener('install', function (e) {
  e.waitUntil(
    caches.open(CACHE)
      .then(function (c) { return c.addAll(ASSETS); })
      .then(function () { return self.skipWaiting(); })
  );
});

self.addEventListener('activate', function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k !== CACHE; })
                           .map(function (k) { return caches.delete(k); }));
  }).then(function () { return self.clients.claim(); }));
});

self.addEventListener('fetch', function (e) {
  var req = e.request;
  if (req.method !== 'GET') { return; }

  var url = new URL(req.url);
  if (url.origin !== self.location.origin) { return; }

  e.respondWith(req.mode === 'navigate' ? navigation(req) : asset(req, e));
});

/* Navigations go to the network first.
 *
 * They used to come from the cache, which meant a deploy was invisible until
 * the load after the one that fetched it — and while chasing a bug through
 * six builds in a day, "am I even running the new code" became a question
 * asked more often than any question about the bug. Correctness beats a
 * hundred milliseconds here: the document is small, and the cache is still
 * behind it the moment the network is not there.
 */
function navigation(req) {
  return Promise.race([
    fetch(req).then(function (res) {
      if (res && res.ok) {
        var copy = res.clone();
        caches.open(CACHE).then(function (c) { c.put(req, copy); });
      }
      return res;
    }),
    // Not a failure mode worth waiting out on a slow connection.
    new Promise(function (resolve, reject) {
      setTimeout(function () { reject(new Error('slow')); }, 3000);
    })
  ]).catch(function () {
    // ignoreSearch here and only here: "?reset" and "?trace" hang a query off
    // the document URL and must still find the shell.
    return caches.match(req, { ignoreSearch: true }).then(function (hit) {
      return hit || caches.match('index.html');
    });
  });
}

/* Everything else is cache-first, and matched exactly.
 *
 * Exactly is the point. This used to match with ignoreSearch, which made
 * "app.js?v=13" find the cached "app.js?v=12" — quietly defeating the whole
 * versioning scheme it was there to support, and serving the previous build's
 * code to the current build's markup.
 */
function asset(req, e) {
  return caches.match(req).then(function (hit) {
    if (hit) { return hit; }

    return fetch(req).then(function (res) {
      // First use of an on-demand asset — the NFT descriptors, the poster —
      // puts it in the cache so the next run works offline too.
      if (res && res.ok && res.type === 'basic') {
        var copy = res.clone();
        e.waitUntil(caches.open(CACHE).then(function (c) { return c.put(req, copy); }));
      }
      return res;
    }).catch(function () {
      throw new Error('offline and uncached: ' + url(req).pathname);
    });
  });
}

function url(req) { return new URL(req.url); }

