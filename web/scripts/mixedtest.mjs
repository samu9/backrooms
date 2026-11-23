// A web player and a Godot player in one room, through the real relay. The browser stands still,
// the Godot client (headless) sprints for a few seconds; each must see the other, in the same
// world (same seed), and the browser must see the Godot player move.
//   npm run build && PORT=5190 npm run server &
//   GODOT=/path/to/godot node web/scripts/mixedtest.mjs [baseUrl]
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';

const base = process.argv[2] ?? 'http://127.0.0.1:5190/';
const godot = process.env.GODOT;
if (!godot) throw new Error('set GODOT to the Godot 4 .NET editor binary');
const wsUrl = base.replace(/^http/, 'ws').replace(/\/$/, '') + '/ws';
const root = process.env.HOME + '/.cache/ms-playwright';
const exe = readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().map((d) => `${root}/${d}/chrome-linux64/chrome`).pop();
const room = 'mixed-' + Date.now();
const fail = [];
const check = (ok, msg) => (ok ? console.log('ok  ', msg) : (fail.push(msg), console.log('FAIL', msg)));

const b = await chromium.launch({ executablePath: exe, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await b.newPage({ viewport: { width: 640, height: 360 } });
const errs = [];
page.on('pageerror', (e) => errs.push(String(e)));
await page.goto(`${base}?room=${room}&name=webby`);
await page.waitForFunction(() => window.__game?.net?.connected, null, { timeout: 90000 });

// the Godot client joins, sprints 6 s, prints what it saw and quits
const out = [];
const g = spawn(godot, ['--headless', '--path', resolve(import.meta.dirname, '../../godot/Game'), '--', `--room=${room}`, '--name=godotty', `--server=${wsUrl}`, '--autowalk=6'], {
  env: { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT ?? process.env.HOME + '/.dotnet' },
});
g.stdout.on('data', (d) => out.push(String(d)));
g.stderr.on('data', (d) => out.push(String(d)));

// meanwhile the browser watches the Godot player
await page.waitForFunction(() => [...window.__game.net.peers.values()].some((p) => p.name === 'godotty' && p.visible), null, { timeout: 60000 });
const first = await page.evaluate(() => [...window.__game.net.peers.values()].find((p) => p.name === 'godotty'));
await page.waitForTimeout(2500);
const later = await page.evaluate(() => [...window.__game.net.peers.values()].find((p) => p.name === 'godotty'));
const code = await new Promise((r) => g.on('exit', r));
const log = out.join('');
const netLine = log.split('\n').find((l) => l.startsWith('net:')) ?? '';
const label = await page.textContent('#room');

check(code === 0, `godot exited cleanly (${code})`);
check(!!first, 'the browser sees the Godot player');
check(first && later && Math.hypot(later.x - first.x, later.z - first.z) > 0.5, `the Godot player moves in the browser (${first?.x?.toFixed(2)},${first?.z?.toFixed(2)} -> ${later?.x?.toFixed(2)},${later?.z?.toFixed(2)})`);
check(/peers: .*webby at .* visible True rendered True/.test(netLine), `Godot sees and renders the web player: ${netLine}`);
// same world: free distances from where Godot stopped, in 8 directions, computed by both
const probe = /probe (\S+) (\S+): (.*)$/m.exec(log);
check(!!probe, 'Godot printed a probe');
if (probe) {
  const [x, z] = [Number(probe[1]), Number(probe[2])];
  const godotD = probe[3].trim().split(' ').map(Number);
  const webD = await page.evaluate(([x, z]) => Array.from({ length: 8 }, (_, i) => window.__game.freeDistance(x, z, Math.cos((i / 8) * Math.PI * 2), Math.sin((i / 8) * Math.PI * 2), 40)), [x, z]);
  check(JSON.stringify(godotD) === JSON.stringify(webD), `same world from (${x}, ${z}): godot ${godotD.join(' ')} / web ${webD.join(' ')}`);
}
await page.waitForTimeout(1500);
check(/· 1$/.test((await page.textContent('#room')) ?? ''), `the browser saw Godot leave (${label} -> ${await page.textContent('#room')})`);
check(errs.length === 0, 'no page errors ' + errs.join('; '));
await b.close();
if (fail.length) {
  console.log(log);
  process.exit(1);
}
