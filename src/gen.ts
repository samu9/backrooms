// Deterministic, infinite layout. Everything here is a pure function of (seed, global cell coords),
// so any chunk can be built in any order and borders always agree. Mirrored in GLSL where needed
// (hash3 and the light lattice) — keep both sides in sync.

export const CELL = 0.5; // metres per occupancy cell
export const CHUNK_CELLS = 64;
export const CHUNK = CELL * CHUNK_CELLS; // 32 m
export const BORDER = 16; // cells of neighbour data baked around each chunk texture
export const TEX = CHUNK_CELLS + 2 * BORDER; // 96
export const LIGHT_PERIOD = 3.0; // metres between ceiling panels
export const H_SCALE = 8.0; // ceiling height encoding range in the texture (metres)

let SEED = 1;
export function setSeed(s: number) {
  SEED = s | 0;
  zoneCache.clear();
}
export function getSeed() {
  return SEED;
}

/** 32-bit integer hash -> [0,1). Identical to `hash3` in shaders.ts. */
export function hash3(x: number, y: number, z: number): number {
  let h = (Math.imul(x, 374761393) + Math.imul(y, 668265263) + Math.imul(z + Math.imul(SEED, 1013), -2048144777)) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

function vnoise1(x: number, salt: number): number {
  const i = Math.floor(x);
  const f = x - i;
  const u = f * f * (3 - 2 * f);
  return (hash3(i, 0, salt) * (1 - u) + hash3(i + 1, 0, salt) * u) * 2 - 1;
}

const mod = (a: number, b: number) => ((a % b) + b) % b;

// a plain object rather than an enum, so the relay can import this file under Node's type stripping
export const ZoneType = { Office: 0, Maze: 1, Hall: 2, Void: 3, Weird: 4, Corridor: 5 } as const;
export type ZoneType = (typeof ZoneType)[keyof typeof ZoneType];

export interface Zone {
  type: ZoneType;
  id: number;
  S: number; // wall-line spacing (cells)
  off: number;
  p: number; // segment probability (x-lines)
  pz: number; // segment probability (z-lines)
  pd: number; // door probability per segment
  dw: number; // door width (cells)
  split: number; // chance of an inner wall per block
  pillar: number; // chance of a pillar at a line crossing
  pillarSize: number;
  partial: boolean; // segments may stop short
  h: number; // ceiling height (m)
  rail: boolean; // wooden chair rail on the walls
}

const ZONE_SIZE = 52; // cells (26 m), before warping
const zoneCache = new Map<number, Zone>();

function makeZone(zi: number, zj: number): Zone {
  const id = (zi * 73856093) ^ (zj * 19349663);
  const r = hash3(zi, zj, 1);
  const r2 = hash3(zi, zj, 2);
  const r3 = hash3(zi, zj, 3);
  let type: ZoneType;
  if (r < 0.34) type = ZoneType.Office;
  else if (r < 0.5) type = ZoneType.Maze;
  else if (r < 0.64) type = ZoneType.Corridor;
  else if (r < 0.78) type = ZoneType.Hall;
  else if (r < 0.9) type = ZoneType.Weird;
  else type = ZoneType.Void;
  const z: Zone = { type, id, S: 12, off: 0, p: 0.7, pz: 0.7, pd: 0.7, dw: 2, split: 0.3, pillar: 0, pillarSize: 1, partial: false, h: 2.75, rail: hash3(zi, zj, 5) < 0.3 };
  switch (type) {
    case ZoneType.Office:
      z.S = 10 + 2 * Math.floor(r2 * 4);
      z.p = z.pz = 0.72;
      z.pd = 0.8;
      z.dw = 2 + Math.floor(r3 * 2);
      z.split = 0.45;
      z.h = r3 < 0.15 ? 2.35 : 2.75;
      break;
    case ZoneType.Maze:
      z.S = 4 + Math.floor(r2 * 3);
      z.p = z.pz = 0.55;
      z.pd = 0.3;
      z.dw = 2;
      z.split = 0;
      z.h = 2.6;
      break;
    case ZoneType.Corridor: {
      z.S = 4 + Math.floor(r2 * 3);
      const ax = r3 < 0.5;
      z.p = ax ? 0.93 : 0.1;
      z.pz = ax ? 0.1 : 0.93;
      z.pd = 0.22;
      z.dw = 2;
      z.split = 0;
      z.h = 2.6;
      break;
    }
    case ZoneType.Hall:
      z.S = 10 + 2 * Math.floor(r2 * 4);
      z.p = z.pz = 0.1;
      z.pd = 0.5;
      z.dw = 3;
      z.pillar = 0.9;
      z.pillarSize = r3 < 0.5 ? 2 : 1;
      z.split = 0.05;
      z.h = r3 < 0.35 ? 4.6 : 3.1;
      break;
    case ZoneType.Weird:
      z.S = 6 + Math.floor(r2 * 4);
      z.p = z.pz = 0.62;
      z.pd = 0.55;
      z.dw = 1 + Math.floor(r3 * 4);
      z.split = 0.6;
      z.partial = true;
      z.h = r3 < 0.3 ? 2.2 : r3 < 0.6 ? 3.4 : 2.75;
      break;
    case ZoneType.Void:
      z.S = 22;
      z.p = z.pz = 0.06;
      z.pd = 0.2;
      z.dw = 3;
      z.pillar = 0.25;
      z.pillarSize = 2;
      z.h = r3 < 0.5 ? 6.0 : 2.75;
      break;
  }
  z.off = Math.floor(hash3(zi, zj, 4) * z.S);
  return z;
}

export function zoneAt(gx: number, gz: number): Zone {
  const wx = gx + Math.floor(14 * vnoise1(gz * 0.035, 11));
  const wz = gz + Math.floor(14 * vnoise1(gx * 0.031, 12));
  const zi = Math.floor(wx / ZONE_SIZE);
  const zj = Math.floor(wz / ZONE_SIZE);
  const key = zi * 1000003 + zj;
  let z = zoneCache.get(key);
  if (!z) {
    z = makeZone(zi, zj);
    if (zoneCache.size > 4096) zoneCache.clear();
    zoneCache.set(key, z);
  }
  return z;
}

/** Is a segment of a wall line present at `along` (cells within segment 0..S)? Handles doors / partial walls. */
function segment(z: Zone, line: number, seg: number, local: number, salt: number, p: number): boolean {
  const s = z.id + salt;
  if (hash3(line, seg, s) >= p) return false;
  if (z.partial) {
    const a = Math.floor(hash3(line, seg, s + 7) * z.S * 0.6);
    const b = z.S - Math.floor(hash3(line, seg, s + 8) * z.S * 0.6);
    if (local < a || local > b) return false;
  }
  if (local === 0) return true;
  if (hash3(line, seg, s + 2) < z.pd) {
    const room = Math.max(1, z.S - 1 - z.dw);
    const start = 1 + Math.floor(hash3(line, seg, s + 3) * room);
    if (local >= start && local < start + z.dw) return false;
  }
  return true;
}

/** Occupancy: true where the cell is wall. */
export function solid(gx: number, gz: number): boolean {
  const z = zoneAt(gx, gz);
  const S = z.S;
  const lx = mod(gx - z.off, S);
  const lz = mod(gz - z.off, S);
  const kx = Math.floor((gx - z.off) / S);
  const kz = Math.floor((gz - z.off) / S);

  if (z.pillar > 0 && lx < z.pillarSize && lz < z.pillarSize && hash3(kx, kz, z.id + 40) < z.pillar) return true;

  const onX = lx === 0; // wall line of constant x, running along z
  const onZ = lz === 0;
  if (onX && onZ) {
    // crossing: solid if any of the four adjoining segments is
    return (
      hash3(kx, kz, z.id + 10) < z.p || hash3(kx, kz - 1, z.id + 10) < z.p || hash3(kz, kx, z.id + 20) < z.pz || hash3(kz, kx - 1, z.id + 20) < z.pz
    );
  }
  if (onX && segment(z, kx, kz, lz, 10, z.p)) return true;
  if (onZ && segment(z, kz, kx, lx, 20, z.pz)) return true;

  // inner split wall within this block
  if (z.split > 0 && S >= 6 && hash3(kx, kz, z.id + 30) < z.split) {
    const alongX = hash3(kx, kz, z.id + 31) < 0.5;
    const pos = 2 + Math.floor(hash3(kx, kz, z.id + 32) * (S - 4));
    const local = alongX ? lx : lz;
    const across = alongX ? lz : lx;
    if (across === pos && local > 0) {
      const dw = Math.min(z.dw, S - 2);
      const start = 1 + Math.floor(hash3(kx, kz, z.id + 33) * Math.max(1, S - 1 - dw));
      if (!(local >= start && local < start + dw)) return true;
    }
  }
  return false;
}

export function ceilingAt(gx: number, gz: number): number {
  return zoneAt(gx, gz).h;
}

// ---- ceiling light lattice (mirrored in GLSL) ----
export function lightCenter(i: number, j: number): [number, number] {
  return [(i + 0.5) * LIGHT_PERIOD, (j + 0.5) * LIGHT_PERIOD];
}

/** 0 = no panel, 1 = dead panel, 2 = lit, 3 = flickering. */
export function lightKind(i: number, j: number): number {
  const [x, z] = lightCenter(i, j);
  const gx = Math.floor(x / CELL);
  const gz = Math.floor(z / CELL);
  // the panel spans ±0.6 m in x: any wall under it removes it
  for (let dx = -2; dx <= 1; dx++) for (let dz = -1; dz <= 0; dz++) if (solid(gx + dx, gz + dz)) return 0;
  if (hash3(i, j, 51) < 0.05) return 0;
  if (hash3(Math.floor(i / 5), Math.floor(j / 5), 53) < 0.08) return 1; // dead block
  if (hash3(i, j, 52) < 0.08) return 1;
  if (hash3(i, j, 54) < 0.07) return 3;
  return 2;
}

/** Find a free spot near (x,z) with clearance. */
export function findFree(x: number, z: number, clear = 2): [number, number] {
  const gx0 = Math.floor(x / CELL);
  const gz0 = Math.floor(z / CELL);
  for (let r = 0; r < 200; r++) {
    for (let a = 0; a < Math.max(1, r * 8); a++) {
      const t = (a / Math.max(1, r * 8)) * Math.PI * 2;
      const gx = gx0 + Math.round(Math.cos(t) * r);
      const gz = gz0 + Math.round(Math.sin(t) * r);
      let ok = true;
      for (let dx = -clear; dx <= clear && ok; dx++) for (let dz = -clear; dz <= clear && ok; dz++) if (solid(gx + dx, gz + dz)) ok = false;
      if (ok) return [(gx + 0.5) * CELL, (gz + 0.5) * CELL];
    }
  }
  return [x, z];
}

/** Grid ray march: free distance from (x,z) along direction (dx,dz), up to max metres. */
export function freeDistance(x: number, z: number, dx: number, dz: number, max = 60): number {
  const step = CELL * 0.5;
  for (let d = 0; d < max; d += step) {
    if (solid(Math.floor((x + dx * d) / CELL), Math.floor((z + dz * d) / CELL))) return d;
  }
  return max;
}

/** Small deterministic PRNG for per-chunk decisions. */
export function rng(a: number, b: number, salt: number) {
  let s = Math.floor(hash3(a, b, salt) * 4294967296) >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
