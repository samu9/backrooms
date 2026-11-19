// Single player vs the CPU entity, end to end in a real browser (no relay needed).
//   node scripts/solotest.mjs [baseUrl]
import { chromium } from 'playwright';
import { readdirSync } from 'node:fs';
const base = process.argv[2] ?? 'http://127.0.0.1:5190/';
const root = process.env.HOME + '/.cache/ms-playwright';
const exe = readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => `${root}/${d}/chrome-linux64/chrome`).pop();
const b = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const p = await b.newPage({ viewport: { width: 960, height: 540 } });
const errs = [];
p.on('pageerror', (e) => errs.push(String(e)));
p.on('console', (m) => m.type() === 'error' && !m.text().includes('404') && errs.push(m.text()));
await p.goto(`${base}?solo=normal&seed=42`);
await p.waitForFunction(() => window.__game?.net, null, { timeout: 90000, polling: 250 });
await p.click('#menu', { position: { x: 20, y: 20 } });
const st = () =>
  p.evaluate(() => {
    const g = window.__game, ai = g.net.ai;
    return { phase: g.hunt.phase, me: g.hunt.myRole, ai: ai.state, detect: +ai.detect.toFixed(2), dist: +Math.hypot(ai.x - g.player.x, ai.z - g.player.z).toFixed(1), room: document.getElementById('room').textContent, line: document.getElementById('hline').textContent };
  });
await p.waitForFunction(() => window.__game.hunt.phase === 'countdown', null, { timeout: 20000, polling: 250 });
console.log('countdown (auto-started):', await st());
await p.waitForFunction(() => window.__game.hunt.phase === 'hunt', null, { timeout: 30000, polling: 250 });
console.log('hunt:', await st());
// stand 9 m in front of the entity, in the open, and wait for it
await p.evaluate(() => {
  const g = window.__game, ai = g.net.ai;
  let best = 0, bt = 0;
  for (let a = 0; a < 32; a++) { const t = (a / 32) * Math.PI * 2; const d = g.freeDistance(ai.x, ai.z, Math.cos(t), Math.sin(t), 20); if (d > best) { best = d; bt = t; } }
  const d = Math.min(9, best - 0.6);
  Object.assign(g.player, { x: ai.x + Math.cos(bt) * d, z: ai.z + Math.sin(bt) * d, yaw: Math.atan2(Math.cos(bt), Math.sin(bt)), pitch: 0.05 });
  ai.yaw = Math.atan2(-Math.cos(bt), -Math.sin(bt)); // looking at the player
});
let shot = false;
for (let i = 0; i < 60; i++) {
  const s = await st();
  if (i % 4 === 0) console.log('  ', JSON.stringify(s));
  if (!shot && s.ai === 'chase') {
    await p.screenshot({ path: 'shots/solo-chase.png' });
    shot = true;
  }
  if (s.me === 'entity' || s.phase !== 'hunt') break;
  await p.waitForTimeout(250);
}
const end = await st();
console.log('end:', end, 'banner:', await p.textContent('#hbTitle'));
await p.screenshot({ path: 'shots/solo-end.png' });
console.log(errs.length ? 'errors:\n' + errs.join('\n') : 'no errors');
await b.close();
