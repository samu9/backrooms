// A* on the 0.5 m occupancy grid, in a window around the start. Walls can be given a finite cost,
// so a path may cut through one: that is where the entity phases.
import { CELL, solid } from '../../../shared/gen.ts';

// ---- cached occupancy (solid() is cheap but A* asks for the same cells many times) ----
const BLOCK = 64;
const blocks = new Map<number, Uint8Array>(); // 0 = unknown, 1 = open, 2 = wall
let cacheSeed = NaN;

export function clearGridCache() {
  blocks.clear();
}

export function wallAt(gx: number, gz: number): boolean {
  const bx = Math.floor(gx / BLOCK),
    bz = Math.floor(gz / BLOCK);
  const key = bx * 100003 + bz;
  let b = blocks.get(key);
  if (!b) {
    if (blocks.size > 512) blocks.clear();
    b = new Uint8Array(BLOCK * BLOCK);
    blocks.set(key, b);
  }
  const i = (gz - bz * BLOCK) * BLOCK + (gx - bx * BLOCK);
  let v = b[i];
  if (!v) {
    v = solid(gx, gz) ? 2 : 1;
    b[i] = v;
  }
  return v === 2;
}

/** Call when the world seed changes. */
export function useSeed(seed: number) {
  if (seed !== cacheSeed) {
    cacheSeed = seed;
    clearGridCache();
  }
}

const cell = (m: number) => Math.floor(m / CELL);

/** Nothing solid within `r` metres of the segment (sampled every 0.2 m, with side offsets). */
export function clearLine(ax: number, az: number, bx: number, bz: number, r = 0): boolean {
  const dx = bx - ax,
    dz = bz - az;
  const len = Math.hypot(dx, dz);
  const n = Math.max(1, Math.ceil(len / 0.2));
  const px = len > 0 ? -dz / len : 0,
    pz = len > 0 ? dx / len : 0;
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    const x = ax + dx * t,
      z = az + dz * t;
    if (wallAt(cell(x), cell(z))) return false;
    if (r > 0 && (wallAt(cell(x + px * r), cell(z + pz * r)) || wallAt(cell(x - px * r), cell(z - pz * r)))) return false;
  }
  return true;
}

export interface Waypoint {
  x: number;
  z: number;
  /** this waypoint is inside a wall: the entity must be phasing to pass */
  wall: boolean;
}

export interface PathOptions {
  /** cost of entering a wall cell; Infinity = walls are impassable */
  wallCost?: number;
  /** half size of the search window, in cells (default 120 = 60 m each way) */
  half?: number;
  /** expansion budget */
  maxNodes?: number;
}

// scratch buffers, reused between searches
let cap = 0;
let G: Float32Array, F: Float32Array, CAME: Int32Array, STATE: Uint8Array, HEAP: Int32Array;
function ensure(n: number) {
  if (n <= cap) return;
  cap = n;
  G = new Float32Array(n);
  F = new Float32Array(n);
  CAME = new Int32Array(n);
  STATE = new Uint8Array(n);
  HEAP = new Int32Array(n);
}

const SQRT2 = Math.SQRT2;
const DIRS = [
  [1, 0, 1],
  [-1, 0, 1],
  [0, 1, 1],
  [0, -1, 1],
  [1, 1, SQRT2],
  [1, -1, SQRT2],
  [-1, 1, SQRT2],
  [-1, -1, SQRT2],
];

/**
 * Path from (sx, sz) to (tx, tz) in metres. If the goal is outside the window or unreachable,
 * returns the path to the reachable cell closest to it. Returns null only if the start is boxed in.
 */
export function findPath(sx: number, sz: number, tx: number, tz: number, opt: PathOptions = {}): Waypoint[] | null {
  const wallCost = opt.wallCost ?? Infinity;
  const half = opt.half ?? 120;
  const maxNodes = opt.maxNodes ?? 40000;
  const W = half * 2 + 1;
  ensure(W * W);
  const ox = cell(sx) - half,
    oz = cell(sz) - half;
  const gxT = Math.min(W - 1, Math.max(0, cell(tx) - ox));
  const gzT = Math.min(W - 1, Math.max(0, cell(tz) - oz));
  const goal = gzT * W + gxT;
  const start = half * W + half;
  G.fill(Infinity, 0, W * W);
  STATE.fill(0, 0, W * W);
  const h = (i: number) => {
    const dx = Math.abs((i % W) - gxT),
      dz = Math.abs(((i / W) | 0) - gzT);
    return Math.max(dx, dz) + (SQRT2 - 1) * Math.min(dx, dz);
  };
  const passable = (gx: number, gz: number) => !wallAt(gx + ox, gz + oz) || wallCost < Infinity;
  let size = 0;
  const push = (i: number) => {
    let k = size++;
    HEAP[k] = i;
    while (k > 0) {
      const p = (k - 1) >> 1;
      if (F[HEAP[p]] <= F[HEAP[k]]) break;
      [HEAP[p], HEAP[k]] = [HEAP[k], HEAP[p]];
      k = p;
    }
  };
  const pop = () => {
    const top = HEAP[0];
    HEAP[0] = HEAP[--size];
    let k = 0;
    for (;;) {
      const l = 2 * k + 1,
        r = l + 1;
      let m = k;
      if (l < size && F[HEAP[l]] < F[HEAP[m]]) m = l;
      if (r < size && F[HEAP[r]] < F[HEAP[m]]) m = r;
      if (m === k) break;
      [HEAP[m], HEAP[k]] = [HEAP[k], HEAP[m]];
      k = m;
    }
    return top;
  };

  G[start] = 0;
  F[start] = h(start);
  CAME[start] = -1;
  push(start);
  STATE[start] = 1;
  let best = start,
    bestH = h(start);
  let expanded = 0;
  while (size > 0 && expanded < maxNodes) {
    const cur = pop();
    if (STATE[cur] === 2) continue;
    STATE[cur] = 2;
    expanded++;
    const hc = h(cur);
    if (hc < bestH) {
      bestH = hc;
      best = cur;
    }
    if (cur === goal) break;
    const cx = cur % W,
      cz = (cur / W) | 0;
    for (const [dx, dz, step] of DIRS) {
      const nx = cx + dx,
        nz = cz + dz;
      if (nx < 0 || nz < 0 || nx >= W || nz >= W) continue;
      const ni = nz * W + nx;
      if (STATE[ni] === 2) continue;
      const wall = wallAt(nx + ox, nz + oz);
      if (wall && wallCost === Infinity) continue;
      // no cutting corners, and no diagonal steps into or out of walls
      if (dx !== 0 && dz !== 0) {
        if (wall || wallAt(cx + ox, cz + oz)) continue;
        if (wallAt(cx + dx + ox, cz + oz) || wallAt(cx + ox, cz + dz + oz)) continue;
      } else if (!passable(nx, nz)) continue;
      const g = G[cur] + step * (wall ? wallCost : 1);
      if (g >= G[ni]) continue;
      G[ni] = g;
      F[ni] = g + h(ni);
      CAME[ni] = cur;
      push(ni);
      STATE[ni] = 1;
    }
  }
  const end = STATE[goal] === 2 ? goal : best;
  if (end === start && start !== goal) return [];
  const cells: number[] = [];
  for (let i = end; i !== -1; i = CAME[i]) cells.push(i);
  cells.reverse();
  const pts: Waypoint[] = cells.map((i) => {
    const gx = (i % W) + ox,
      gz = ((i / W) | 0) + oz;
    return { x: (gx + 0.5) * CELL, z: (gz + 0.5) * CELL, wall: wallAt(gx, gz) };
  });
  return smooth(pts);
}

/** String-pull the open stretches: keep a waypoint only where the straight line would clip a wall. */
function smooth(pts: Waypoint[]): Waypoint[] {
  if (pts.length <= 2) return pts.slice(1);
  const out: Waypoint[] = [];
  let a = 0;
  while (a < pts.length - 1) {
    if (pts[a + 1].wall || pts[a].wall) {
      out.push(pts[a + 1]);
      a++;
      continue;
    }
    let b = a + 1;
    while (b + 1 < pts.length && !pts[b + 1].wall && clearLine(pts[a].x, pts[a].z, pts[b + 1].x, pts[b + 1].z, 0.22)) b++;
    out.push(pts[b]);
    a = b;
  }
  return out;
}

/** Path length in metres. */
export function pathLength(sx: number, sz: number, pts: Waypoint[]): number {
  let L = 0,
    x = sx,
    z = sz;
  for (const p of pts) {
    L += Math.hypot(p.x - x, p.z - z);
    x = p.x;
    z = p.z;
  }
  return L;
}
