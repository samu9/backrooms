// Two players in one room: B walks, A turns to look at B. Needs `npm run server` and the dev server.
//   node scripts/mptest.mjs [baseUrl]
import { chromium } from 'playwright';
import { readdirSync } from 'node:fs';
const base = process.argv[2] ?? 'http://127.0.0.1:5190/';
const root = process.env.HOME + '/.cache/ms-playwright';
const exe = readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => `${root}/${d}/chrome-linux64/chrome`).pop();
const b = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const room = 'test-' + Date.now();
const errs = [];
async function open(name) {
  const p = await b.newPage({ viewport: { width: 960, height: 540 } });
  p.on('pageerror', (e) => errs.push(name + ': ' + e));
  p.on('console', (m) => m.type() === 'error' && !m.text().includes('404') && errs.push(name + ': ' + m.text()));
  await p.goto(`${base}?room=${room}&name=${name}`);
  await p.waitForFunction(() => window.__game?.net?.connected, null, { timeout: 90000 });
  await p.click('#menu', { position: { x: 20, y: 20 } });
  return p;
}
const A = await open('alice');
const B = await open('bob');
// put bob 3.5 m in front of alice along her longest free sight line, facing her
const spot = await A.evaluate(() => {
  const g = window.__game;
  let best = 0, bt = 0;
  for (let a = 0; a < 32; a++) { const t = (a / 32) * Math.PI * 2; const d = g.freeDistance(g.player.x, g.player.z, Math.cos(t), Math.sin(t), 20); if (d > best) { best = d; bt = t; } }
  const d = Math.min(3.5, best - 0.6);
  return { x: g.player.x + Math.cos(bt) * d, z: g.player.z + Math.sin(bt) * d, yaw: Math.atan2(Math.cos(bt), Math.sin(bt)) };
});
await B.evaluate((s) => Object.assign(window.__game.player, s), spot);
await B.keyboard.down('KeyD');
await B.waitForTimeout(700);
await B.keyboard.up('KeyD');
await B.waitForTimeout(1500);
const info = await A.evaluate(() => {
  const g = window.__game;
  const peers = [...g.net.peers.values()].map((p) => ({ name: p.name, x: +p.x.toFixed(2), z: +p.z.toFixed(2), visible: p.visible }));
  const o = [...g.net.peers.values()][0];
  if (o) g.player.yaw = Math.atan2(-(o.x - g.player.x), -(o.z - g.player.z)); // face bob
  g.player.pitch = -0.05;
  return { me: { x: +g.player.x.toFixed(2), z: +g.player.z.toFixed(2) }, peers, room: document.getElementById('room').textContent };
});
const bInfo = await B.evaluate(() => ({ peers: [...window.__game.net.peers.values()].map((p) => p.name), room: document.getElementById('room').textContent }));
await A.waitForTimeout(1500);
await A.screenshot({ path: 'shots/mp-alice.png' });
await A.keyboard.press('KeyM');
await A.waitForTimeout(600);
await A.screenshot({ path: 'shots/mp-alice-map.png' });
console.log('alice sees', JSON.stringify(info));
console.log('bob sees', JSON.stringify(bInfo));
await B.close();
await A.waitForTimeout(1500);
console.log('after bob left, alice room label:', await A.textContent('#room'));
console.log(errs.length ? 'errors:\n' + errs.join('\n') : 'no errors');
await b.close();
