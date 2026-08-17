/* Draw the NFT target poster.
 *
 * Two things have to be true of a natural-feature target, and they are not
 * the same thing.
 *
 * It needs *many* features. That part is easy — any busy image has corners.
 *
 * It needs *distinctive* ones, and that is where a graphic design fails. The
 * matcher describes each feature by the pattern of light and dark around it,
 * and a page of solid shapes on white describes almost every corner
 * identically: black on one side, white on the other. Two earlier versions of
 * this poster — one a dense field of small marks, one a sparse field of large
 * ones — scored six hundred features per level and matched nothing at all,
 * because six hundred near-identical descriptors are worth about one.
 *
 * Photographs work because their texture varies continuously, so no two
 * neighbourhoods describe the same. This poster is therefore built from
 * multi-octave value noise — a synthetic photograph — with the typography and
 * a few crisp marks laid over it for identity and for coarse-scale structure.
 *
 * Scale matters too: AR.js matches on a 320x240 downsample of the camera feed
 * whatever the camera's real resolution, so the useful detail sits between
 * about 2% and 20% of the poster's width. The noise octaves are chosen to put
 * energy exactly there.
 *
 * Everything is drawn from a seeded PRNG, so the poster is reproducible and
 * the descriptors stay valid.
 *
 *   node scripts/make-poster.mjs [--out data/poster.png] [--width 1000]
 */

import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const argv = process.argv.slice(2);
const value = (n, d) => { const i = argv.indexOf(n); return i !== -1 && argv[i + 1] ? argv[i + 1] : d; };

const WIDTH = Number(value('--width', 1000));
const HEIGHT = Math.round(WIDTH * Math.SQRT2);          // A-series proportion
// JPEG, not PNG, and not for size: the NFT descriptor generator reads PNG
// input with the wrong row stride and trains on a sheared, tripled copy of
// the image. It produces a healthy-looking six hundred features per level
// from that garbage and matches nothing. Its JPEG path is correct.
const OUT = join(ROOT, value('--out', 'data/poster.jpg'));

const { chromium } = await import('playwright');
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: WIDTH, height: HEIGHT } });

const dataUrl = await page.evaluate(draw, { w: WIDTH, h: HEIGHT });
await browser.close();

await writeFile(OUT, Buffer.from(dataUrl.split(',')[1], 'base64'));
console.log(`wrote ${OUT} — ${WIDTH}x${HEIGHT}`);

function draw({ w, h }) {
  // xorshift32: same poster on every machine, every run.
  let seed = 0x9E3779B9;
  const rnd = () => {
    seed ^= seed << 13; seed >>>= 0;
    seed ^= seed >> 17;
    seed ^= seed << 5; seed >>>= 0;
    return seed / 0x100000000;
  };
  const pick = (a) => a[Math.floor(rnd() * a.length)];
  const range = (lo, hi) => lo + rnd() * (hi - lo);
  const smooth = (t) => t * t * (3 - 2 * t);

  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const x = c.getContext('2d');

  const INK = '#101018';
  const MID = '#4A4768';
  const pad = Math.round(w * 0.03);          // print safety only

  x.fillStyle = '#ffffff';
  x.fillRect(0, 0, w, h);

  const left = pad;
  const top = pad;
  const fieldW = w - pad * 2;
  const fieldH = h - pad * 2;

  /* ── base texture ──────────────────────────────────────────
     Multi-octave value noise, edge to edge. Amplitudes are close to flat
     rather than falling off steeply: a conventional 1/f spectrum reads as
     fog, with all the energy in the coarse octaves and the fine detail the
     matcher needs a whisper on top of it. */
  const AMPS = [0.8, 1, 0.95, 0.85, 0.7, 0.34];
  const field = new Float32Array(fieldW * fieldH);

  for (let oct = 0; oct < AMPS.length; oct++) {
    const amp = AMPS[oct];
    const cols = 4 << oct;
    const rows = Math.max(2, Math.round(cols * fieldH / fieldW));
    const gw = cols + 1;
    const gh = rows + 1;

    const grid = new Float32Array(gw * gh);
    for (let i = 0; i < grid.length; i++) grid[i] = rnd();

    for (let j = 0; j < fieldH; j++) {
      const gy = (j / fieldH) * rows;
      const j0 = Math.min(gh - 2, Math.floor(gy));
      const ty = smooth(gy - j0);

      for (let i = 0; i < fieldW; i++) {
        const gx = (i / fieldW) * cols;
        const i0 = Math.min(gw - 2, Math.floor(gx));
        const tx = smooth(gx - i0);

        const a = grid[j0 * gw + i0];
        const b = grid[j0 * gw + i0 + 1];
        const e = grid[(j0 + 1) * gw + i0];
        const f = grid[(j0 + 1) * gw + i0 + 1];
        const topRow = a + (b - a) * tx;

        field[j * fieldW + i] += amp * (topRow + ((e + (f - e) * tx) - topRow) * ty);
      }
    }
  }

  let lo = Infinity;
  let hi = -Infinity;
  for (let i = 0; i < field.length; i++) {
    if (field[i] < lo) lo = field[i];
    if (field[i] > hi) hi = field[i];
  }
  const span = (hi - lo) || 1;

  const img = x.createImageData(fieldW, fieldH);
  for (let i = 0; i < field.length; i++) {
    let v = (field[i] - lo) / span;
    v = v * v * (3 - 2 * v);
    v = 0.30 + v * 0.62;                     // keep it light enough to print

    const p = i * 4;
    img.data[p]     = Math.round(255 * v * 0.90 + 14);
    img.data[p + 1] = Math.round(255 * v * 0.89 + 14);
    img.data[p + 2] = Math.round(255 * v * 0.97 + 22);
    img.data[p + 3] = 255;
  }
  x.putImageData(img, left, top);

  /* ── structure at every scale ──────────────────────────────
     Texture alone is fragile: smooth noise resampled at a slightly different
     scale describes differently, and the match fails. Photographs survive
     because they carry hard edges at many scales at once. These marks supply
     that — deliberately spanning 2% to 22% of the width rather than
     clustering in one size band, which is what the two failed versions of
     this poster each did. */
  const lines = 22;
  for (let i = 0; i < lines; i++) {
    x.save();
    x.strokeStyle = rnd() > 0.45 ? INK : '#ffffff';
    x.lineWidth = range(w * 0.003, w * 0.014);
    x.globalAlpha = range(0.55, 1);
    x.beginPath();
    x.moveTo(range(left, left + fieldW), range(top, top + fieldH));
    x.lineTo(range(left, left + fieldW), range(top, top + fieldH));
    x.stroke();
    x.restore();
  }

  for (let i = 0; i < 150; i++) {
    // A power-law spread of sizes: a few big, many small, everything between.
    const u = rnd();
    const s = w * (0.018 + 0.20 * u * u * u);

    const px = range(left + s * 0.6, left + fieldW - s * 0.6);
    const py = range(top + s * 0.6, top + fieldH - s * 0.6);

    x.save();
    x.translate(px, py);
    x.rotate(range(0, Math.PI * 2));
    const light = rnd() > 0.5;
    x.fillStyle = light ? '#ffffff' : INK;
    x.strokeStyle = light ? '#ffffff' : (rnd() > 0.6 ? MID : INK);
    x.lineWidth = Math.max(w * 0.0035, s * 0.16);
    x.globalAlpha = range(0.7, 1);

    switch (pick(['bar', 'square', 'tri', 'arc', 'cross', 'dot', 'chev'])) {
      case 'bar': x.fillRect(-s * 0.6, -s * 0.13, s * 1.2, s * 0.26); break;
      case 'square':
        rnd() > 0.5 ? x.fillRect(-s / 2, -s / 2, s, s) : x.strokeRect(-s / 2, -s / 2, s, s);
        break;
      case 'tri':
        x.beginPath();
        x.moveTo(0, -s * 0.58); x.lineTo(s * 0.52, s * 0.42); x.lineTo(-s * 0.52, s * 0.42);
        x.closePath();
        rnd() > 0.5 ? x.fill() : x.stroke();
        break;
      case 'arc':
        x.beginPath();
        x.arc(0, 0, s * 0.5, 0, range(Math.PI * 0.6, Math.PI * 1.8));
        x.stroke();
        break;
      case 'cross':
        x.beginPath();
        x.moveTo(-s * 0.5, 0); x.lineTo(s * 0.5, 0);
        x.moveTo(0, -s * 0.5); x.lineTo(0, s * 0.5);
        x.stroke();
        break;
      case 'dot':
        x.beginPath(); x.arc(0, 0, s * 0.36, 0, Math.PI * 2);
        rnd() > 0.45 ? x.fill() : x.stroke();
        break;
      case 'chev':
        x.beginPath();
        x.moveTo(-s * 0.5, -s * 0.38); x.lineTo(0, s * 0.1); x.lineTo(s * 0.5, -s * 0.38);
        x.stroke();
        break;
    }
    x.restore();
  }

  /* ── type over the texture ─────────────────────────────────
     Reversed out rather than set on white: the letterforms then sit against
     varied ground, which makes their edges distinctive instead of generic,
     and no part of the sheet is wasted on blank paper. */
  const title = Math.round(w * 0.15);
  x.font = `700 ${title}px "DejaVu Sans Mono", ui-monospace, monospace`;
  x.textBaseline = 'alphabetic';

  const words = [['MARKER', pad * 2.2, pad * 2 + title], ['TWO', pad * 2.2, pad * 2 + title * 2.05]];
  words.forEach(([t, tx, ty]) => {
    x.save();
    x.fillStyle = 'rgba(16,16,24,0.85)';
    x.fillText(t, tx + w * 0.007, ty + w * 0.007);
    x.fillStyle = '#ffffff';
    x.fillText(t, tx, ty);
    x.restore();
  });

  x.font = `700 ${Math.round(w * 0.026)}px "DejaVu Sans Mono", ui-monospace, monospace`;
  x.fillStyle = 'rgba(16,16,24,0.9)';
  x.fillText('NATURAL FEATURE TARGET / PRINT AT ANY SIZE / KEEP FLAT',
    pad * 2.3 + w * 0.004, pad * 2 + title * 2.5 + w * 0.004);
  x.fillStyle = '#ffffff';
  x.fillText('NATURAL FEATURE TARGET / PRINT AT ANY SIZE / KEEP FLAT',
    pad * 2.3, pad * 2 + title * 2.5);

  const glyphs = '0123456789ABCDEF#/\\|<>=+-*:.';
  x.font = `700 ${Math.round(w * 0.058)}px "DejaVu Sans Mono", ui-monospace, monospace`;
  for (let row = 0; row < 3; row++) {
    let line = '';
    for (let i = 0; i < 18; i++) line += glyphs[Math.floor(rnd() * glyphs.length)];
    const ty = h - pad * 2.6 - (2 - row) * w * 0.068;
    x.fillStyle = 'rgba(16,16,24,0.85)';
    x.fillText(line, pad * 2.2 + w * 0.005, ty + w * 0.005);
    x.fillStyle = '#ffffff';
    x.fillText(line, pad * 2.2, ty);
  }

  /* ── frame and registration marks ──────────────────────────*/
  x.globalAlpha = 1;
  x.strokeStyle = INK;
  x.lineWidth = w * 0.007;
  x.strokeRect(pad, pad, fieldW, fieldH);

  const tick = w * 0.035;
  [[pad, pad, 1, 1], [w - pad, pad, -1, 1], [pad, h - pad, 1, -1], [w - pad, h - pad, -1, -1]]
    .forEach(([px, py, sx, sy]) => {
      x.strokeStyle = '#ffffff';
      x.lineWidth = w * 0.016;
      x.beginPath();
      x.moveTo(px + sx * tick * 2.2, py);
      x.lineTo(px, py);
      x.lineTo(px, py + sy * tick * 2.2);
      x.stroke();
      x.strokeStyle = INK;
      x.lineWidth = w * 0.007;
      x.beginPath();
      x.moveTo(px + sx * tick * 2.2, py);
      x.lineTo(px, py);
      x.lineTo(px, py + sy * tick * 2.2);
      x.stroke();
      x.beginPath();
      x.arc(px + sx * tick * 1.1, py + sy * tick * 1.1, tick * 0.4, 0, Math.PI * 2);
      x.stroke();
    });

  return c.toDataURL('image/jpeg', 0.94);
}
