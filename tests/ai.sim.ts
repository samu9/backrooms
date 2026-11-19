// Balance simulation: the CPU entity against a scripted wanderer, many rounds per difficulty.
//   node tests/ai.sim.ts [rounds=10] [--trace]
// Uses the real rules (shared/hunt.ts), the real AI and the real layout; only the player is a bot.
// The bot walks to the exit, sprints (with the same stamina rules) when it sees the entity
// coming, and pants when out of breath.
import { CELL, findFree, setSeed } from '../shared/gen.ts';
import { type Difficulty, EntityAI } from '../web/src/ai/entity.ts';
import { clearLine, findPath, useSeed, wallAt, type Waypoint } from '../web/src/ai/path.ts';
import { HUNT_S, type Hunter, LAYOUT_SOLO, newRound, startRound, tickRound, tryPhase } from '../shared/hunt.ts';

const ROUNDS = Number(process.argv[2] ?? 10);
const TRACE = process.argv.includes('--trace');
const DT = 0.1;
const WALK = 1.7,
  RUN = 5.6;

interface Result {
  outcome: 'caught' | 'escaped' | 'survived';
  t: number;
  phases: number;
  chases: number;
}

function round(diff: Difficulty, seed: number): Result {
  setSeed(seed);
  useSeed(seed);
  let rngState = seed * 9301 + 49297;
  const rand = () => ((rngState = (rngState * 233280 + 49297) % 2147483647) / 2147483647 + 1) % 1;
  const [sx, sz] = findFree(0, 0, 2);
  const me: Hunter = { id: 1, role: 'none', pose: [sx, sz, 0, 0, 0, 0], phaseReadyAt: 0 };
  const cpu: Hunter = { id: 2, role: 'none', pose: [sx, sz, 0, 0, 0, 0], phaseReadyAt: 0 };
  const r = newRound();
  let nowMs = 0;
  let phases = 0;
  const ai = new EntityAI(diff, {
    phase: () => {
      const ok = tryPhase(r, cpu, nowMs) > 0;
      if (ok) phases++;
      return ok;
    },
    phaseReady: () => r.phase === 'hunt' && nowMs >= cpu.phaseReadyAt,
    rand,
  });
  let first = true;
  const ev = startRound(r, [me, cpu], seed, nowMs, () => (first ? ((first = false), 0.999) : rand()), LAYOUT_SOLO);
  const tp = ev.find((e) => e.t === 'teleport')!;
  if (tp.t === 'teleport') ai.place(tp.x, tp.z);
  // bot state
  let x = sx,
    z = sz,
    stamina = 6,
    exhausted = false,
    sinceSprint = 0;
  let path: Waypoint[] = [];
  let repath = 0;
  let chases = 0,
    wasChasing = false;
  let tHunt = 0;
  for (let step = 0; step < (HUNT_S + 15) / DT; step++) {
    nowMs += DT * 1000;
    const t = nowMs / 1000;
    // --- bot ---
    const de = Math.hypot(ai.x - x, ai.z - z);
    const threat = r.phase === 'hunt' && de < 14 && clearLine(x, z, ai.x, ai.z);
    const wantRun = threat && !exhausted;
    if (t > repath || path.length === 0) {
      repath = t + 1;
      path = findPath(x, z, r.exit!.x, r.exit!.z) ?? [];
    }
    let speed = r.phase === 'countdown' || r.phase === 'hunt' ? (wantRun ? RUN : WALK) : 0;
    if (wantRun) {
      stamina = Math.max(0, stamina - DT);
      sinceSprint = 0;
      if (stamina === 0) exhausted = true;
    } else {
      sinceSprint += DT;
      if (sinceSprint > 1) stamina = Math.min(6, stamina + 0.6 * DT);
      if (exhausted && stamina > 2.1) exhausted = false;
    }
    let budget = speed * DT;
    let vx = 0,
      vz = 0;
    while (budget > 0 && path.length) {
      const w = path[0];
      const dx = w.x - x,
        dz = w.z - z,
        d = Math.hypot(dx, dz);
      if (d < 1e-4) {
        path.shift();
        continue;
      }
      const s = Math.min(d, budget);
      const nx = x + (dx / d) * s,
        nz = z + (dz / d) * s;
      if (wallAt(Math.floor(nx / CELL), Math.floor(nz / CELL))) {
        path = [];
        break;
      }
      vx = (dx / d) * speed;
      vz = (dz / d) * speed;
      x = nx;
      z = nz;
      budget -= s;
      if (s >= d) path.shift();
    }
    if (!path.length) speed = 0;
    me.pose = [x, z, 0, 0, speed, stamina < 1.8 ? 1 : 0];
    // --- entity ---
    if (r.phase === 'hunt') {
      tHunt += DT;
      ai.update(DT, t, { x, z, vx, vz, speed, panting: stamina < 1.8 });
      if (TRACE && step % 10 === 0 && de < 30) console.log(`  ${diff} t=${tHunt.toFixed(0)} state=${ai.state} dist=${de.toFixed(1)} ai=(${ai.x.toFixed(1)},${ai.z.toFixed(1)}) exit=${Math.hypot(r.exit!.x - x, r.exit!.z - z).toFixed(0)}m detect=${ai.detect.toFixed(2)}`);
      if (ai.state === 'chase' && !wasChasing) chases++;
      wasChasing = ai.state === 'chase';
    }
    cpu.pose = [ai.x, ai.z, ai.yaw, 0, ai.speed, ai.phasing ? 2 : 0];
    tickRound(r, [me, cpu], seed, nowMs);
    if (r.phase === 'end' || me.role !== 'wanderer') {
      const outcome = me.role === 'escaped' ? 'escaped' : me.role === 'entity' ? 'caught' : 'survived';
      return { outcome, t: tHunt, phases, chases };
    }
  }
  return { outcome: 'survived', t: tHunt, phases, chases };
}

const t0 = Date.now();
console.log(`${ROUNDS} rounds per difficulty, ${HUNT_S}s hunts, bot walks to the exit and sprints when it sees the entity\n`);
console.log('difficulty  caught  escaped  survived  avg time  chases/round  phases/round');
for (const diff of ['easy', 'normal', 'nightmare'] as Difficulty[]) {
  const res: Result[] = [];
  for (let i = 0; i < ROUNDS; i++) res.push(round(diff, 1000 + i * 7919));
  const n = (o: string) => res.filter((r) => r.outcome === o).length;
  const avg = (f: (r: Result) => number) => (res.reduce((s, r) => s + f(r), 0) / res.length).toFixed(1);
  console.log(
    `${diff.padEnd(10)}  ${String(n('caught')).padStart(6)}  ${String(n('escaped')).padStart(7)}  ${String(n('survived')).padStart(8)}  ${avg((r) => r.t).padStart(7)}s  ${avg((r) => r.chases).padStart(12)}  ${avg((r) => r.phases).padStart(12)}`,
  );
}
console.log(`\n(${((Date.now() - t0) / 1000).toFixed(1)} s)`);
