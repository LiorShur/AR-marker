/* Local HTTPS static server, no dependencies.
 *
 * getUserMedia needs a secure context, and "secure" does not include
 * http://192.168.x.x — which is exactly the URL you need to open on a phone
 * to test this. So: self-signed certificate, served over the LAN, accept the
 * warning once on the phone. After that the origin is secure and the camera
 * opens, with no deploy in the loop.
 *
 *   node scripts/serve.mjs [--port 8443] [--http]
 */

import { createServer as createHttps } from 'node:https';
import { createServer as createHttp } from 'node:http';
import { readFile, mkdir, access } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { networkInterfaces } from 'node:os';
import { promisify } from 'node:util';
import { join, extname, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const exec = promisify(execFile);
const ROOT = fileURLToPath(new URL('..', import.meta.url));
const CERTS = join(ROOT, 'certs');

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name, fallback) => {
  const i = argv.indexOf(name);
  return i !== -1 && argv[i + 1] ? argv[i + 1] : fallback;
};

const PORT = Number(value('--port', 8443));
const PLAIN = flag('--http');

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.webmanifest': 'application/manifest+json',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.glb': 'model/gltf-binary',
  '.gltf': 'model/gltf+json',
  // The two that a misconfigured host gets wrong, and the two that turn the
  // AR view into a silent black screen when it does.
  '.dat': 'application/octet-stream',
  '.hiro': 'text/plain; charset=utf-8',
  '.patt': 'text/plain; charset=utf-8',
  '.fset': 'application/octet-stream',
  '.fset3': 'application/octet-stream',
  '.iset': 'application/octet-stream'
};

function lanAddress() {
  for (const list of Object.values(networkInterfaces())) {
    for (const net of list ?? []) {
      if (net.family === 'IPv4' && !net.internal) return net.address;
    }
  }
  return null;
}

async function exists(path) {
  try { await access(path); return true; } catch { return false; }
}

/* Self-signed cert covering localhost and whatever LAN address this machine
   currently has. Regenerated when the address changes, since a certificate
   without the right SAN is refused outright by Chrome rather than warned on. */
async function certificate(host) {
  await mkdir(CERTS, { recursive: true });
  const key = join(CERTS, 'dev-key.pem');
  const cert = join(CERTS, 'dev-cert.pem');
  const stamp = join(CERTS, `.for-${host}`);

  if (await exists(key) && await exists(cert) && await exists(stamp)) {
    return { key: await readFile(key), cert: await readFile(cert) };
  }

  const san = `subjectAltName=DNS:localhost,IP:127.0.0.1${host ? `,IP:${host}` : ''}`;
  try {
    await exec('openssl', [
      'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256',
      '-days', '825',
      '-keyout', key, '-out', cert,
      '-subj', '/CN=Marker One dev',
      '-addext', san
    ]);
  } catch (err) {
    console.error('\nCould not generate a certificate with openssl.');
    console.error('Run with --http instead and use localhost only:\n');
    console.error('  node scripts/serve.mjs --http\n');
    throw err;
  }

  await (await import('node:fs/promises')).writeFile(stamp, san);
  return { key: await readFile(key), cert: await readFile(cert) };
}

async function handler(req, res) {
  const url = new URL(req.url, 'http://x');
  let path = decodeURIComponent(url.pathname);
  if (path.endsWith('/')) path += 'index.html';

  // normalize collapses ".." before it can climb out of ROOT.
  const file = join(ROOT, normalize(path).replace(/^(\.\.[/\\])+/, ''));
  if (!file.startsWith(ROOT)) {
    res.writeHead(403).end('forbidden');
    return;
  }

  try {
    const body = await readFile(file);
    res.writeHead(200, {
      'Content-Type': TYPES[extname(file).toLowerCase()] ?? 'application/octet-stream',
      // Never cache in dev — a stale service worker asset is the single most
      // confusing failure mode this project has.
      'Cache-Control': 'no-store, must-revalidate',
      'Service-Worker-Allowed': '/'
    });
    res.end(body);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain' }).end('404 ' + path);
  }
}

const host = lanAddress();
const server = PLAIN ? createHttp(handler) : createHttps(await certificate(host), handler);

server.listen(PORT, '0.0.0.0', () => {
  const scheme = PLAIN ? 'http' : 'https';
  console.log('\n  Marker One — serving ' + ROOT);
  console.log('  ─────────────────────────────────────────────');
  console.log(`  this machine   ${scheme}://localhost:${PORT}/`);
  if (host) console.log(`  phone on wifi  ${scheme}://${host}:${PORT}/`);
  if (!PLAIN) {
    console.log('\n  The certificate is self-signed, so the first visit warns.');
    console.log('  Accept it — the origin counts as secure afterwards and the');
    console.log('  camera will open. Chrome: Advanced -> Proceed.');
  } else {
    console.log('\n  --http mode: the camera works on localhost only.');
  }
  if (!host) console.log('\n  No LAN address found — this machine may be offline.');
  console.log('');
});
