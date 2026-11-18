// Round logic of the hunt, run with: node tests/hunt.test.ts
import assert from 'node:assert/strict';
import { findFree, setSeed } from '../src/gen.ts';
import { CATCH_DIST, type Hunter, newRound, startRound, tickRound, tryPhase } from '../server/hunt.ts';

const SEED = 42;
setSeed(SEED);
const [sx, sz] = findFree(0, 0, 3);
const mk = (id: number): Hunter => ({ id, role: 'none', pose: [sx, sz, 0, 0, 0, 0], phaseReadyAt: 0 });

// start: one entity, everyone else wanderer, exit + entity placed away from the group
{
  const ps = [mk(1), mk(2), mk(3)];
  const r = newRound();
  let t = 1_000_000;
  const ev = startRound(r, ps, SEED, t, () => 0.0);
  assert.equal(r.phase, 'countdown');
  assert.equal(ps.filter((p) => p.role === 'entity').length, 1);
  assert.equal(ps.filter((p) => p.role === 'wanderer').length, 2);
  const tp = ev.find((e) => e.t === 'teleport') as { x: number; z: number; id: number };
  assert.ok(Math.hypot(tp.x - sx, tp.z - sz) > 20, 'entity starts far away');
  assert.ok(Math.hypot(r.exit!.x - sx, r.exit!.z - sz) > 50, 'exit is far away');
  // countdown -> hunt
  t += 10_001;
  tickRound(r, ps, SEED, t);
  assert.equal(r.phase, 'hunt');
  // phasing: cooldown respected
  const ent = ps.find((p) => p.role === 'entity')!;
  assert.ok(tryPhase(r, ent, t) > 0);
  assert.equal(tryPhase(r, ent, t + 1000), 0, 'cooldown');
  assert.equal(tryPhase(r, ps.find((p) => p.role === 'wanderer')!, t), 0, 'wanderers cannot phase');
  // catch: entity right next to a wanderer in the open -> wanderer becomes an entity
  const [w, other] = ps.filter((p) => p.role === 'wanderer');
  ent.pose = [sx + CATCH_DIST * 0.5, sz, 0, 0, 0, 0];
  w.pose = [sx, sz, 0, 0, 0, 0];
  other.pose = [sx + 40, sz + 40, 0, 0, 0, 0]; // far away
  const ev2 = tickRound(r, ps, SEED, t + 100);
  assert.ok(ev2.some((e) => e.t === 'caught' && e.id === w.id));
  assert.equal(w.role, 'entity');
  // last wanderer reaches the exit -> round ends, wanderers win
  const last = ps.find((p) => p.role === 'wanderer')!;
  last.pose = [r.exit!.x, r.exit!.z, 0, 0, 0, 0];
  tickRound(r, ps, SEED, t + 200);
  assert.equal(last.role, 'escaped');
  assert.equal(r.phase, 'end');
  assert.equal(r.winner, 'wanderers');
  // end -> lobby
  tickRound(r, ps, SEED, t + 200 + 10_001);
  assert.equal(r.phase, 'lobby');
  assert.ok(ps.every((p) => p.role === 'none'));
}

// no catch through a wall
{
  setSeed(SEED);
  // find a 1-cell wall with open floor on both sides
  let spot: [number, number] | null = null;
  const { solid } = await import('../src/gen.ts');
  for (let gx = 0; gx < 400 && !spot; gx++)
    for (let gz = 0; gz < 400 && !spot; gz++)
      if (solid(gx, gz) && !solid(gx - 1, gz) && !solid(gx + 1, gz) && !solid(gx - 1, gz - 1) && !solid(gx + 1, gz + 1)) spot = [gx, gz];
  assert.ok(spot);
  const [gx, gz] = spot!;
  const ps = [mk(1), mk(2)];
  const r = newRound();
  startRound(r, ps, SEED, 0, () => 0);
  tickRound(r, ps, SEED, 10_001);
  const ent = ps.find((p) => p.role === 'entity')!;
  const w = ps.find((p) => p.role === 'wanderer')!;
  ent.pose = [(gx - 0.5) * 0.5, (gz + 0.5) * 0.5, 0, 0, 0, 0];
  w.pose = [(gx + 1.5) * 0.5, (gz + 0.5) * 0.5, 0, 0, 0, 0];
  tickRound(r, ps, SEED, 10_100);
  assert.equal(w.role, 'wanderer', 'the wall protects the wanderer');
}

// everyone caught -> entity wins; the timer running out -> wanderers win
{
  const ps = [mk(1), mk(2)];
  const r = newRound();
  startRound(r, ps, SEED, 0, () => 0);
  tickRound(r, ps, SEED, 10_001);
  const ent = ps.find((p) => p.role === 'entity')!;
  const w = ps.find((p) => p.role === 'wanderer')!;
  ent.pose = [sx + 0.3, sz, 0, 0, 0, 0];
  w.pose = [sx, sz, 0, 0, 0, 0];
  tickRound(r, ps, SEED, 10_100);
  assert.equal(r.winner, 'entity');

  const ps2 = [mk(1), mk(2)];
  const r2 = newRound();
  startRound(r2, ps2, SEED, 0, () => 0);
  tickRound(r2, ps2, SEED, 10_001);
  ps2.find((p) => p.role === 'entity')!.pose = [sx + 30, sz, 0, 0, 0, 0];
  tickRound(r2, ps2, SEED, 10_001 + 301_000);
  assert.equal(r2.winner, 'wanderers');
}
console.log('hunt rules: all tests passed');
