/* Precache everything. All assets are same-origin and vendored,
   so the app runs with the network off once installed.

   Updating: bump CACHE here and the ?v= on the two links in index.html,
   together. install() calls skipWaiting and activate() calls claim, so a
   new worker takes over on the next load rather than the one after —
   provided the host serves this file with no-cache (see firebase.json). */
var CACHE = 'marker-one-v12';

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
  'app.css?v=10',
  'app.js?v=10',
  'manifest.webmanifest',
  'content.json',
  'vendor/aframe.min.js',
  'vendor/aframe-ar-nft.js',
  'vendor/meshopt_decoder.js',
  'spatial/config.js?v=10',
  'spatial/geo.js?v=10',
  'spatial/store.js?v=10',
  'spatial/localize.js?v=10',
  'spatial/appcheck.js?v=10',
  'spatial/world.js?v=10',
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

  e.respondWith(
    // ignoreSearch matters: index.html asks for "app.js?v=4" and "?reset"
    // hangs a query off the document URL. Without it every versioned
    // request misses the cache and the app is only offline-capable in
    // theory.
    caches.match(req, { ignoreSearch: true }).then(function (hit) {
      if (hit) {
        if (req.mode === 'navigate' && !url.search) { e.waitUntil(revalidate(req)); }
        return hit;
      }
      return fetch(req).then(function (res) {
        // First use of an on-demand asset — the NFT descriptors, the poster —
        // puts it in the cache so the next run works offline too.
        if (res && res.ok && res.type === 'basic') {
          var copy = res.clone();
          e.waitUntil(caches.open(CACHE).then(function (c) { return c.put(req, copy); }));
        }
        return res;
      }).catch(function () {
        // Offline and unrecognised: a navigation still gets the shell,
        // anything else is genuinely unavailable.
        if (req.mode === 'navigate') { return caches.match('index.html'); }
        throw new Error('offline and uncached: ' + url.pathname);
      });
    })
  );
});

// Keep the shell from pinning itself forever if a deploy lands while the
// old worker is still the active one.
function revalidate(req) {
  return fetch(req).then(function (res) {
    if (res && res.ok) { return caches.open(CACHE).then(function (c) { return c.put(req, res); }); }
  }).catch(function () { /* offline is the normal case here */ });
}
