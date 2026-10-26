// Renders the director page frame by frame. Usage:
//   node render.mjs <outDir> [frame,frame,...]   (with a list, only those frames are shot, as stills)
import { chromium } from 'playwright-core';
import fs from 'node:fs';

const [, , outDir, only, baseUrl = 'http://localhost:5203'] = process.argv;
const pick = only ? new Set(only.split(',').map(Number)) : null;
fs.mkdirSync(outDir, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', args: ['--force-color-profile=srgb', '--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: 1080, height: 1080 }, deviceScaleFactor: 2 });
page.on('console', m => console.log('[page]', m.text()));
page.on('pageerror', e => console.log('[pageerror]', e.message));
await page.goto(`${baseUrl}/promo/director.html`);
const total = await page.evaluate(() => window.__init());
const last = pick ? Math.max(...pick) : total - 1;
const t0 = Date.now();
for (let n = 0; n <= last; n++) {
  await page.evaluate(k => window.__frame(k), n);
  if (!pick || pick.has(n)) {
    await page.screenshot({
      path: `${outDir}/f${String(n).padStart(4, '0')}.${pick ? 'png' : 'jpg'}`,
      ...(pick ? {} : { type: 'jpeg', quality: 94 }),
    });
    if (n % 60 === 0) console.log(`frame ${n}/${last} ${((Date.now() - t0) / 1000).toFixed(0)}s`);
  }
}
// Match the approved original: the upload's first frame is its title-card thumbnail.
if (!pick) fs.copyFileSync(`${outDir}/f0170.jpg`, `${outDir}/f0000.jpg`);
await browser.close();
console.log('done', last + 1, 'frames');
