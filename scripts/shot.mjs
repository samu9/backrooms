// Deterministic screenshots of the running dev server.
//   node scripts/shot.mjs --out shots/v1 --views 0,1,2,3 [--params "seed=1&t=10"] [--w 1280 --h 720]
// Each view k loads /?shot&view=k&<params>, waits for window.__ready and saves <out>/view-k.png.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => (a.startsWith('--') ? [...acc, [a.slice(2), arr[i + 1]?.startsWith('--') ? 'true' : arr[i + 1]]] : acc), []),
);
const base = args.base ?? 'http://127.0.0.1:5190/';
const out = args.out ?? 'shots/latest';
const views = (args.views ?? '0,1,2,3').split(',');
const w = Number(args.w ?? 1280);
const h = Number(args.h ?? 720);
const extra = args.params ?? '';
mkdirSync(out, { recursive: true });

const exe = process.env.CHROME ?? (await import('node:fs')).readdirSync(process.env.HOME + '/.cache/ms-playwright').filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => process.env.HOME + '/.cache/ms-playwright/' + d + '/chrome-linux64/chrome').pop();
const browser = await chromium.launch({
  executablePath: exe,
  headless: true,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--enable-webgl'],
});
const page = await browser.newPage({ viewport: { width: w, height: h } });
const errors = [];
page.on('console', (m) => {
  if (m.type() === 'error' || m.type() === 'warning') errors.push(m.text());
});
page.on('pageerror', (e) => errors.push(String(e)));
for (const v of views) {
  const url = `${base}?shot&${v.includes('=') ? v : 'view=' + v}&${extra}`;
  const t0 = Date.now();
  await page.goto(url);
  await page.waitForFunction(() => window.__ready === true, null, { timeout: 180000 });
  const st = await page.evaluate(() => window.__state);
  const name = v.includes('=') ? v.replace(/[^a-z0-9.-]+/gi, '_') : 'view-' + v;
  await page.screenshot({ path: `${out}/${name}.png` });
  console.log(`${name}: ${Date.now() - t0} ms ${JSON.stringify(st)}`);
}
if (errors.length) console.log('console:\n' + [...new Set(errors)].slice(0, 20).join('\n'));
await browser.close();
