// End-to-end check of the hunt with two headless players.
//   node scripts/hunttest.mjs [baseUrl]     (needs the relay + dev server)
import { chromium } from 'playwright';
import { readdirSync } from 'node:fs';
const base = process.argv[2] ?? 'http://127.0.0.1:5190/';
const root = process.env.HOME + '/.cache/ms-playwright';
const exe = readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => `${root}/${d}/chrome-linux64/chrome`).pop();
const b = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const room = 'hunt-' + Date.now();
const errs = [];
async function open(name) {
  const p = await b.newPage({ viewport: { width: 960, height: 540 } });
  p.on('pageerror', (e) => errs.push(name + ': ' + e));
  p.on('console', (m) => m.type() === 'error' && !m.text().includes('404') && errs.push(name + ': ' + m.text()));
  await p.goto(`${base}?room=${room}&name=${name}&seed=42`);
  await p.waitForFunction(() => window.__game?.net?.connected, null, { timeout: 90000, polling: 250 });
  await p.click('#menu', { position: { x: 20, y: 20 } });
  return p;
}
const state = (p) => p.evaluate(() => { const h = window.__game.hunt; return { phase: h.phase, me: h.myRole, line: document.getElementById('hline').textContent, banner: document.getElementById('hbanner').classList.contains('hidden') ? '' : document.getElementById('hbTitle').textContent }; });
const A = await open('alice');
const B = await open('bob');
await A.waitForTimeout(1000);
console.log('lobby:', await state(A));
await A.keyboard.press('KeyR');
await A.waitForFunction(() => window.__game.hunt.phase === 'countdown', null, { timeout: 10000, polling: 250 });
await B.waitForFunction(() => window.__game.hunt.phase === 'countdown', null, { timeout: 10000, polling: 250 });
const sA = await state(A), sB = await state(B);
console.log('countdown:', sA, sB);
const [E, W] = sA.me === 'entity' ? [A, B] : [B, A];
await E.waitForTimeout(800);
await E.screenshot({ path: 'shots/hunt-entity-wait.png' });
await W.screenshot({ path: 'shots/hunt-wanderer-start.png' });
await W.waitForFunction(() => window.__game.hunt.phase === 'hunt', null, { timeout: 30000, polling: 250 });
console.log('hunt:', await state(W));
// entity steps out 4 m in front of the wanderer, along the wanderer's longest sight line
const spot = await W.evaluate(() => {
  const g = window.__game;
  let best = 0, bt = 0;
  for (let a = 0; a < 32; a++) { const t = (a / 32) * Math.PI * 2; const d = g.freeDistance(g.player.x, g.player.z, Math.cos(t), Math.sin(t), 20); if (d > best) { best = d; bt = t; } }
  const d = Math.min(4.5, best - 0.6);
  g.player.yaw = Math.atan2(-Math.cos(bt), -Math.sin(bt));
  g.player.pitch = 0.05;
  return { x: g.player.x + Math.cos(bt) * d, z: g.player.z + Math.sin(bt) * d, yaw: Math.atan2(Math.cos(bt), Math.sin(bt)), wx: g.player.x, wz: g.player.z };
});
await E.evaluate((s) => Object.assign(window.__game.player, { x: s.x, z: s.z, yaw: s.yaw, pitch: -0.1 }), spot);
await W.waitForTimeout(2500);
await W.screenshot({ path: 'shots/hunt-wanderer-sees.png' });
await E.screenshot({ path: 'shots/hunt-entity-sees.png' });
const dread = await W.evaluate(() => window.__game.hunt && +document.querySelector('#c') && 0);
// phase through walls
await E.keyboard.press('KeyE');
await E.waitForTimeout(500);
console.log('entity phasing:', await E.evaluate(() => window.__game.hunt.phasing));
// the catch
await E.evaluate((s) => Object.assign(window.__game.player, { x: s.wx + 0.5, z: s.wz }), spot);
for (let i = 0; i < 20; i++) {
  const st = await W.evaluate(() => ({ role: window.__game.hunt.myRole, phase: window.__game.hunt.phase, roles: [...window.__game.hunt.roles], connected: window.__game.net.connected }));
  if (i % 4 === 0) console.log('wanderer state', JSON.stringify(st));
  if (st.role === 'entity' || st.phase === 'end') break;
  await W.waitForTimeout(500);
}
console.log('after the catch:', JSON.stringify(await state(W)), 'winner:', await W.evaluate(() => window.__game.hunt.winner));
await W.screenshot({ path: 'shots/hunt-caught.png' });
await W.waitForFunction(() => window.__game.hunt.phase === 'lobby', null, { timeout: 20000, polling: 250 });
console.log('back to lobby:', await state(W));
void dread;
console.log(errs.length ? 'errors:\n' + errs.join('\n') : 'no errors');
await b.close();
