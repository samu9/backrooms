// Contact sheet: node scripts/sheet.mjs out.png img1.png img2.png ... (2 columns)
import { chromium } from 'playwright';
import { readFileSync, readdirSync } from 'node:fs';
const [out, ...imgs] = process.argv.slice(2);
const html = `<body style="margin:0;background:#000;display:grid;grid-template-columns:1fr 1fr;gap:4px">${imgs
  .map((f) => `<div style="position:relative"><img style="width:100%;display:block" src="data:image/${f.endsWith('png') ? 'png' : 'jpeg'};base64,${readFileSync(f).toString('base64')}"><span style="position:absolute;left:6px;top:4px;color:#0f0;font:14px monospace">${f.split('/').pop()}</span></div>`)
  .join('')}</body>`;
const root = process.env.HOME + '/.cache/ms-playwright';
const exe = readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => `${root}/${d}/chrome-linux64/chrome`).pop();
const b = await chromium.launch({ executablePath: exe });
const p = await b.newPage({ viewport: { width: 1600, height: 400 } });
await p.setContent(html);
await p.screenshot({ path: out, fullPage: true });
await b.close();
