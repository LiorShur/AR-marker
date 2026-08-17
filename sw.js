/* Precache everything. All assets are same-origin and vendored,
   so the app runs with the network off once installed. */
var CACHE = 'marker-one-v3';
var ASSETS = [
  'index.html',
  'marker.html',
  'app.css',
  'app.js',
  'manifest.webmanifest',
  'vendor/aframe.min.js',
  'vendor/aframe-ar.js',
  'data/patt.hiro',
  'data/camera_para.dat',
  'data/marker-hiro.png',
  'icons/icon-192.png',
  'icons/icon-512.png',
  'icons/icon-maskable-512.png'
];

self.addEventListener('install', function (e) {
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(ASSETS); }).then(function () {
    return self.skipWaiting();
  }));
});

self.addEventListener('activate', function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k !== CACHE; })
                           .map(function (k) { return caches.delete(k); }));
  }).then(function () { return self.clients.claim(); }));
});

self.addEventListener('fetch', function (e) {
  if (e.request.method !== 'GET') return;
  e.respondWith(
    caches.match(e.request).then(function (hit) {
      return hit || fetch(e.request);
    })
  );
});
