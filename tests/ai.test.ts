// CPU entity: pathfinding and senses. Run with: node tests/ai.test.ts
import assert from 'node:assert/strict';
import { CELL, setSeed, solid } from '../shared/gen.ts';
import { EntityAI, type Prey } from '../src/ai/entity.ts';
import { clearLine, findPath, useSeed, wallAt } from '../src/ai/path.ts';

const SEED = 42;
setSeed(SEED);
useSeed(SEED);
const m = (g: number) => (g + 0.5) * CELL;

// a long, one-cell-thick wall running along z with open floor on both sides of its middle
let wall: { gx: number; gz: number } | null = null;
search: for (let gx = -200; gx < 200; gx++)
  for (let gz = -200; gz < 200; gz++) {
    let ok = true;
    for (let k = -8; k <= 8 && ok; k++) ok = solid(gx, gz + k) && !solid(gx - 1, gz + k) && !solid(gx + 1, gz + k) && !solid(gx - 2, gz + k) && !solid(gx + 2, gz + k);
    if (ok) {
      wall = { gx, gz };
      break search;
    }
  }
assert.ok(wall, 'found a long thin wall');
const { gx, gz } = wall!;
const A = { x: m(gx - 2), z: m(gz) },
  B = { x: m(gx + 2), z: m(gz) };

// --- path ---
{
  const around = findPath(A.x, A.z, B.x, B.z)!;
  assert.ok(around.length > 0, 'a route exists');
  assert.ok(around.every((w) => !w.wall), 'with walls solid, the path never enters one');
  const last = around[around.length - 1];
  assert.ok(Math.hypot(last.x - B.x, last.z - B.z) < 0.5, 'reaches the goal');
  // consecutive waypoints are joined by clear lines (smoothing never cuts through a wall)
  let px = A.x,
    pz = A.z;
  for (const w of around) {
    assert.ok(clearLine(px, pz, w.x, w.z), 'smoothed segment stays clear');
    px = w.x;
    pz = w.z;
  }
  const through = findPath(A.x, A.z, B.x, B.z, { wallCost: 2 })!;
  assert.ok(through.some((w) => w.wall), 'with cheap walls, the path cuts through');
  assert.ok(through.length <= 6, 'and is short');
}

// --- senses ---
{
  const ai = new EntityAI('normal', { phase: () => false, phaseReady: () => false, rand: () => 0.5 });
  ai.place(A.x, A.z);
  const prey = (x: number, z: number, speed = 0): Prey => ({ x, z, vx: 0, vz: 0, speed, panting: false });
  // facing +x (towards B): yaw such that forward = (-sin, -cos) = (1, 0)
  ai.yaw = -Math.PI / 2;
  assert.equal(ai.canSee(prey(B.x, B.z)), false, 'cannot see through a wall');
  assert.ok(wallAt(gx, gz));
  // in the open, 3 m along the wall on its own side: visible
  const open = prey(A.x - 0.0, A.z + 3);
  ai.yaw = Math.atan2(-(open.x - A.x), -(open.z - A.z));
  assert.equal(ai.canSee(open), true, 'sees in the open, in front');
  ai.yaw += Math.PI; // facing away
  assert.equal(ai.canSee(prey(A.x, A.z + 6)), false, 'not behind its back (beyond 4 m)');
  assert.equal(ai.canSee(prey(A.x, A.z + 2)), true, 'senses all around within 4 m');
  // hearing
  assert.equal(ai.hearingRange(prey(0, 0, 5.6), false), 18, 'sprinting is loud');
  assert.equal(ai.hearingRange(prey(0, 0, 1.7), false), 5, 'walking is quiet');
  assert.equal(ai.hearingRange(prey(0, 0, 0), false), 0, 'standing still is silent');
  assert.ok(ai.hearingRange(prey(0, 0, 5.6), true) < 18, 'walls muffle');
  assert.ok(ai.hearingRange({ ...prey(0, 0, 0), panting: true }, false) >= 10, 'panting carries');
}

// --- it hears a sprint behind a wall and comes to look ---
{
  const ai = new EntityAI('normal', { phase: () => false, phaseReady: () => false, rand: () => 0.5 });
  ai.place(A.x, A.z);
  ai.update(0.1, 1, { x: B.x, z: B.z, vx: 0, vz: 5.6, speed: 5.6, panting: false });
  assert.equal(ai.state, 'investigate');
}
console.log('ai: all tests passed');
