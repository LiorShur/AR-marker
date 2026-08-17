/* Build a Y4M video of the Hiro marker for Chromium's fake camera device.
 *
 * Chrome can be told to read a raw Y4M file instead of a real camera
 * (--use-file-for-fake-video-capture), which is the only way to test marker
 * tracking without a human holding a phone. There is no image decoder in
 * Node, so the PNG is rasterised by a throwaway browser page onto a canvas,
 * read back as RGBA, and converted to planar YUV 4:2:0 here.
 */

import { writeFile, readFile } from 'node:fs/promises';

const WIDTH = 640;
const HEIGHT = 480;
const FRAMES = 8;

export async function makeFakeCamera({ chromium, markerPath, out, scale = 0.36 }) {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  // A data: URL rather than file: — the throwaway page has an opaque origin
  // and is not allowed to read off the disk.
  const dataUrl = 'data:image/png;base64,' + (await readFile(markerPath)).toString('base64');

  const rgba = await page.evaluate(async ({ url, w, h, scale }) => {
    const img = new Image();
    img.src = url;
    await img.decode();

    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    const ctx = c.getContext('2d');

    // White field, marker centred. The tracker locks onto the black border
    // first, so it needs clear margin on every side.
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, w, h);

    const size = Math.round(Math.min(w, h) * scale);
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img, (w - size) / 2, (h - size) / 2, size, size);

    return Array.from(ctx.getImageData(0, 0, w, h).data);
  }, { url: dataUrl, w: WIDTH, h: HEIGHT, scale });

  await browser.close();

  const frame = rgbaToYuv420(Uint8ClampedArray.from(rgba), WIDTH, HEIGHT);
  const header = Buffer.from(`YUV4MPEG2 W${WIDTH} H${HEIGHT} F15:1 Ip A1:1 C420\n`, 'ascii');
  const marker = Buffer.from('FRAME\n', 'ascii');

  const parts = [header];
  for (let i = 0; i < FRAMES; i++) { parts.push(marker, frame); }

  await writeFile(out, Buffer.concat(parts));
  return { path: out, width: WIDTH, height: HEIGHT };
}

function rgbaToYuv420(rgba, w, h) {
  const y = Buffer.alloc(w * h);
  const u = Buffer.alloc((w / 2) * (h / 2));
  const v = Buffer.alloc((w / 2) * (h / 2));

  for (let j = 0; j < h; j++) {
    for (let i = 0; i < w; i++) {
      const p = (j * w + i) * 4;
      const r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
      y[j * w + i] = clamp8(0.299 * r + 0.587 * g + 0.114 * b);

      // Chroma is subsampled 2x2; sampling the top-left of each block is
      // plenty for a black-and-white target.
      if ((j & 1) === 0 && (i & 1) === 0) {
        const c = (j / 2) * (w / 2) + i / 2;
        u[c] = clamp8(-0.169 * r - 0.331 * g + 0.5 * b + 128);
        v[c] = clamp8(0.5 * r - 0.419 * g - 0.081 * b + 128);
      }
    }
  }
  return Buffer.concat([y, u, v]);
}

function clamp8(n) { return n < 0 ? 0 : n > 255 ? 255 : Math.round(n); }
