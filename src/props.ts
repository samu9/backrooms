// Office furniture made of boxes, placed the way the Backrooms place things: sunk into the carpet,
// pushed half into a wall, hanging upside down from the ceiling, a staircase climbing into the tiles.
import * as THREE from 'three';
import { CELL, CHUNK_CELLS, ceilingAt, rng, solid } from '../shared/gen';

// [cx, cy, cz, sx, sy, sz, colour]
export type Box = [number, number, number, number, number, number, number];
export interface Template {
  boxes: Box[];
  h: number;
  d: number; // depth (z extent) used when pushing into walls
  wall?: boolean; // belongs on a wall
}

const PALETTE: [number, number, number][] = [
  [0.4, 0.28, 0.16], // 0 wood
  [0.56, 0.54, 0.46], // 1 beige metal
  [0.2, 0.18, 0.17], // 2 dark fabric
  [0.42, 0.42, 0.4], // 3 grey plastic
  [0.6, 0.45, 0.27], // 4 cardboard
  [0.74, 0.73, 0.68], // 5 off-white
  [0.6, 0.6, 0.6], // 6 metal
  [0.68, 0.54, 0.35], // 7 door wood
  [0.33, 0.37, 0.31], // 8 chair green
  [0.07, 0.07, 0.07], // 9 black
  [0.7, 0.8, 0.84], // 10 pale blue
  [0.55, 0.12, 0.1], // 11 faded red
];

function legs(x: number, z: number, h: number, w: number, c: number): Box[] {
  return [
    [-x, h / 2, -z, w, h, w, c],
    [x, h / 2, -z, w, h, w, c],
    [-x, h / 2, z, w, h, w, c],
    [x, h / 2, z, w, h, w, c],
  ];
}

const T: Record<string, Template> = {
  chair: { h: 1.0, d: 0.5, boxes: [[0, 0.45, 0, 0.46, 0.06, 0.46, 8], [0, 0.76, -0.21, 0.44, 0.52, 0.05, 8], ...legs(0.2, 0.2, 0.42, 0.03, 6)] },
  table: { h: 0.75, d: 0.75, boxes: [[0, 0.73, 0, 1.4, 0.04, 0.72, 5], ...legs(0.66, 0.32, 0.71, 0.04, 6)] },
  desk: {
    h: 0.77,
    d: 0.75,
    boxes: [
      [0, 0.755, 0, 1.5, 0.04, 0.75, 0],
      [-0.73, 0.37, 0, 0.04, 0.74, 0.72, 0],
      [0.45, 0.37, 0, 0.52, 0.74, 0.7, 0],
      [0, 0.45, -0.34, 1.42, 0.55, 0.02, 0],
      [0.45, 0.6, 0.355, 0.12, 0.02, 0.02, 6],
      [0.45, 0.3, 0.355, 0.12, 0.02, 0.02, 6],
    ],
  },
  cabinet: {
    h: 1.32,
    d: 0.62,
    boxes: [[0, 0.66, 0, 0.46, 1.32, 0.62, 1], ...[0, 1, 2, 3].map((i): Box => [0, 0.22 + i * 0.32, 0.315, 0.12, 0.025, 0.025, 6])],
  },
  door: {
    h: 2.14,
    d: 0.14,
    wall: true,
    boxes: [
      [-0.48, 1.04, 0, 0.06, 2.08, 0.14, 5],
      [0.48, 1.04, 0, 0.06, 2.08, 0.14, 5],
      [0, 2.11, 0, 1.02, 0.06, 0.14, 5],
      [0, 1.02, 0, 0.9, 2.04, 0.045, 7],
      [0.36, 1.0, 0.04, 0.05, 0.05, 0.07, 6],
    ],
  },
  shelf: {
    h: 1.8,
    d: 0.36,
    boxes: [
      [-0.45, 0.9, 0, 0.03, 1.8, 0.36, 1],
      [0.45, 0.9, 0, 0.03, 1.8, 0.36, 1],
      [0, 0.9, -0.17, 0.9, 1.8, 0.015, 1],
      ...[0, 1, 2, 3, 4].map((i): Box => [0, 0.05 + i * 0.43, 0, 0.9, 0.025, 0.36, 1]),
    ],
  },
  box: { h: 0.4, d: 0.4, boxes: [[0, 0.2, 0, 0.5, 0.4, 0.4, 4], [0, 0.4, 0, 0.5, 0.004, 0.05, 4]] },
  cooler: { h: 1.4, d: 0.32, boxes: [[0, 0.5, 0, 0.32, 1.0, 0.32, 5], [0, 1.2, 0, 0.26, 0.4, 0.26, 10], [0, 0.85, 0.165, 0.05, 0.05, 0.03, 11]] },
  couch: { h: 0.85, d: 0.85, boxes: [[0, 0.22, 0, 1.9, 0.44, 0.85, 2], [0, 0.62, -0.34, 1.9, 0.5, 0.18, 2], [-0.88, 0.42, 0, 0.14, 0.38, 0.85, 2], [0.88, 0.42, 0, 0.14, 0.38, 0.85, 2]] },
  monitor: { h: 0.4, d: 0.42, boxes: [[0, 0.2, 0, 0.4, 0.36, 0.42, 3], [0, 0.2, 0.211, 0.32, 0.26, 0.005, 9], [0, 0.02, 0, 0.3, 0.04, 0.3, 3]] },
  stairs: { h: 1.62, d: 2.5, boxes: Array.from({ length: 9 }, (_, i): Box => [0, (i + 1) * 0.09, -1.12 + i * 0.28, 1.0, (i + 1) * 0.18, 0.28, 1]) },
  outlet: { h: 0.12, d: 0.02, wall: true, boxes: [[0, 0.3, 0, 0.08, 0.12, 0.02, 5], [-0.012, 0.32, 0.011, 0.004, 0.018, 0.003, 9], [0.012, 0.32, 0.011, 0.004, 0.018, 0.003, 9]] },
  vent: { h: 0.4, d: 0.03, wall: true, boxes: [[0, 0.2, 0, 0.6, 0.4, 0.03, 5], ...[0, 1, 2, 3, 4, 5].map((i): Box => [0, 0.05 + i * 0.06, 0.016, 0.54, 0.012, 0.006, 3])] },
  exitsign: { h: 0.2, d: 0.08, wall: true, boxes: [[0, 0.1, 0, 0.36, 0.18, 0.08, 11]] },
};

const FLOOR_SET = ['chair', 'chair', 'table', 'desk', 'cabinet', 'shelf', 'box', 'box', 'cooler', 'couch', 'monitor'];
const WALL_SET = ['door', 'door', 'door', 'desk', 'cabinet', 'shelf', 'chair', 'table', 'couch', 'vent', 'outlet', 'outlet', 'exitsign'];

export class GeoBuilder {
  pos: number[] = [];
  nrm: number[] = [];
  col: number[] = [];
  a: number[] = [];
  b: number[] = [];
  idx: number[] = [];

  /** Quad from origin o with edges eu, ev. Winding chosen so the face points along n. */
  quad(o: THREE.Vector3, eu: THREE.Vector3, ev: THREE.Vector3, n: THREE.Vector3, col: number[], kind: number, mode: number, su: number, sv: number, seed: number, dis: number[]) {
    const base = this.pos.length / 3;
    const corners = [
      [0, 0],
      [1, 0],
      [1, 1],
      [0, 1],
    ];
    for (let k = 0; k < 4; k++) {
      const [u, v] = corners[k];
      this.pos.push(o.x + eu.x * u + ev.x * v, o.y + eu.y * u + ev.y * v, o.z + eu.z * u + ev.z * v);
      this.nrm.push(n.x, n.y, n.z);
      this.col.push(col[0], col[1], col[2]);
      this.a.push(kind, mode, u * su, v * sv);
      this.b.push(su, sv, seed, dis[k]);
    }
    const c = new THREE.Vector3().crossVectors(eu, ev).dot(n);
    if (c >= 0) this.idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
    else this.idx.push(base, base + 2, base + 1, base, base + 3, base + 2);
  }

  geometry(): THREE.BufferGeometry | null {
    if (!this.idx.length) return null;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(this.pos, 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute(this.nrm, 3));
    g.setAttribute('aCol', new THREE.Float32BufferAttribute(this.col, 3));
    g.setAttribute('aA', new THREE.Float32BufferAttribute(this.a, 4));
    g.setAttribute('aB', new THREE.Float32BufferAttribute(this.b, 4));
    const n = this.pos.length / 3;
    g.setIndex(n > 65535 ? new THREE.Uint32BufferAttribute(this.idx, 1) : new THREE.Uint16BufferAttribute(this.idx, 1));
    g.computeBoundingSphere();
    return g;
  }
}

const _m = new THREE.Matrix4();
const _bm = new THREE.Matrix4();
const _o = new THREE.Vector3();
const _u = new THREE.Vector3();
const _v = new THREE.Vector3();
const _n = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _e = new THREE.Euler();
const FACES: [number[], number[], number[], number[]][] = [
  // origin corner (in -0.5..0.5 box units), eu axis, ev axis, normal
  [[0.5, -0.5, -0.5], [0, 0, 1], [0, 1, 0], [1, 0, 0]],
  [[-0.5, -0.5, 0.5], [0, 0, -1], [0, 1, 0], [-1, 0, 0]],
  [[-0.5, 0.5, -0.5], [1, 0, 0], [0, 0, 1], [0, 1, 0]],
  [[-0.5, -0.5, 0.5], [1, 0, 0], [0, 0, -1], [0, -1, 0]],
  [[-0.5, -0.5, 0.5], [1, 0, 0], [0, 1, 0], [0, 0, 1]],
  [[0.5, -0.5, -0.5], [-1, 0, 0], [0, 1, 0], [0, 0, -1]],
];

export function emitProp(g: GeoBuilder, t: Template, m: THREE.Matrix4, mode: number, seed: number, emissive: boolean, palette = PALETTE) {
  const nm = new THREE.Matrix3().getNormalMatrix(m);
  for (const [cx, cy, cz, sx, sy, sz, ci] of t.boxes) {
    const col = palette[ci];
    const c = emissive && ci === 11 ? [1.0, 0.25, 0.18] : col;
    _bm.makeScale(sx, sy, sz).setPosition(cx, cy, cz).premultiply(m);
    for (const [o, eu, ev, nn] of FACES) {
      _o.set(o[0], o[1], o[2]).applyMatrix4(_bm);
      // edge vectors: transform endpoints and subtract
      _u.set(o[0] + eu[0], o[1] + eu[1], o[2] + eu[2]).applyMatrix4(_bm).sub(_o);
      _v.set(o[0] + ev[0], o[1] + ev[1], o[2] + ev[2]).applyMatrix4(_bm).sub(_o);
      _n.set(nn[0], nn[1], nn[2]).applyMatrix3(nm).normalize();
      const su = Math.abs(eu[0] * sx + eu[1] * sy + eu[2] * sz);
      const sv = Math.abs(ev[0] * sx + ev[1] * sy + ev[2] * sz);
      // dissolve coordinate: normalised template height of each corner
      const ys = [0, 1, 2, 3].map((k) => {
        const u = k === 1 || k === 2 ? 1 : 0;
        const v = k >= 2 ? 1 : 0;
        const ly = cy + sy * (o[1] + eu[1] * u + ev[1] * v);
        return Math.min(1, Math.max(0, ly / t.h));
      });
      g.quad(_o, _u, _v, _n, c, 3, mode, su, sv, seed, ys);
    }
  }
}

/** Build the prop geometry for one chunk (chunk-local coordinates). */
export interface PropSpot {
  x: number;
  z: number;
  name: string;
}

export function buildProps(ci: number, cj: number, spots?: PropSpot[]): THREE.BufferGeometry | null {
  const g = new GeoBuilder();
  const r = rng(ci, cj, 777);
  const gx0 = ci * CHUNK_CELLS;
  const gz0 = cj * CHUNK_CELLS;
  const count = 12 + Math.floor(r() * 12);
  const dirs: [number, number][] = [
    [1, 0],
    [-1, 0],
    [0, 1],
    [0, -1],
  ];
  for (let k = 0; k < count; k++) {
    const lx = 2 + Math.floor(r() * (CHUNK_CELLS - 4));
    const lz = 2 + Math.floor(r() * (CHUNK_CELLS - 4));
    const gx = gx0 + lx;
    const gz = gz0 + lz;
    if (solid(gx, gz)) continue;
    const H = ceilingAt(gx, gz);
    // nearest wall within 3 cells
    let wall: [number, number] | null = null;
    let wallDist = 0;
    for (let d = 1; d <= 3 && !wall; d++) {
      const start = Math.floor(r() * 4);
      for (let q = 0; q < 4; q++) {
        const [dx, dz] = dirs[(start + q) % 4];
        if (solid(gx + dx * d, gz + dz * d)) {
          wall = [dx, dz];
          wallDist = d;
          break;
        }
      }
    }
    const roll = r();
    let modeR = r();
    const mode = modeR < 0.35 ? 0 : modeR < 0.7 ? 1 : modeR < 0.9 ? 2 : 3;
    const seed = r();
    const cx = (lx + 0.5) * CELL;
    const cz = (lz + 0.5) * CELL;
    _e.set(0, 0, 0);
    let px = cx,
      py = 0,
      pz = cz;
    let name: string;

    if (wall && roll < 0.5) {
      name = WALL_SET[Math.floor(r() * WALL_SET.length)];
      const t = T[name];
      // face away from the wall; push back so the prop sinks into it
      const [dx, dz] = wall;
      const yaw = Math.atan2(-dx, -dz);
      const wallFace = (wallDist - 0.5) * CELL; // distance from cell centre to wall surface
      const sink = t.wall ? t.d * (0.1 + 0.5 * r()) : t.d * (0.2 + 0.6 * r());
      const back = wallFace - t.d / 2 + sink;
      px = cx + dx * back;
      pz = cz + dz * back;
      const slide = (r() - 0.5) * 0.8;
      px += -dz * slide;
      pz += dx * slide;
      _e.set(0, yaw, 0, 'YXZ');
      const alt = r();
      if (name === 'outlet' || name === 'vent' || name === 'exitsign') {
        py = name === 'vent' ? H - 0.6 - r() * 0.6 : name === 'exitsign' ? H - 0.35 : r() < 0.7 ? 0 : 0.6 + r() * 1.0;
      } else if (alt < 0.25) {
        // glued flat against the wall, rotated onto its back
        _e.set(-Math.PI / 2, yaw, 0, 'YXZ');
        py = 0.3 + r() * 0.8;
      } else if (alt < 0.45) {
        py = 0.5 + r() * 1.2; // floating on the wall
      } else if (alt < 0.6 && name === 'door') {
        py = -0.4 - r() * 0.8; // door half sunk into the floor
      } else if (alt < 0.7) {
        py = H - t.h * (0.4 + 0.4 * r()); // hanging from the ceiling, pushed into the wall
      }
    } else if (roll < 0.62) {
      name = FLOOR_SET[Math.floor(r() * FLOOR_SET.length)];
      const t = T[name];
      py = -t.h * (0.2 + 0.55 * r());
      _e.set((r() - 0.5) * 0.5, r() * Math.PI * 2, (r() - 0.5) * 0.5, 'YXZ');
    } else if (roll < 0.74) {
      name = FLOOR_SET[Math.floor(r() * FLOOR_SET.length)];
      const t = T[name];
      // upside down from the ceiling
      _e.set(Math.PI + (r() - 0.5) * 0.3, r() * Math.PI * 2, (r() - 0.5) * 0.2, 'YXZ');
      py = H + t.h * (0.15 + 0.4 * r());
    } else if (roll < 0.8) {
      name = 'stairs';
      _e.set(0, Math.floor(r() * 4) * (Math.PI / 2), 0, 'YXZ');
      // make sure it reaches the ceiling: stretch vertically
      const sy = (H + 0.25) / 1.62;
      _q.setFromEuler(_e);
      _m.compose(new THREE.Vector3(cx, 0, cz), _q, new THREE.Vector3(1, sy, 1));
      emitProp(g, T.stairs, _m, mode === 1 ? 1 : mode, seed, false);
      continue;
    } else {
      // "normal" placement, a little crooked; small clusters
      name = FLOOR_SET[Math.floor(r() * FLOOR_SET.length)];
      _e.set(0, r() * Math.PI * 2, 0, 'YXZ');
      if (name === 'table' || name === 'desk') {
        // a ring of chairs, one of them sunk too deep
        const n = 2 + Math.floor(r() * 3);
        for (let c = 0; c < n; c++) {
          const a = (c / n) * Math.PI * 2 + r() * 0.4;
          const ce = new THREE.Euler(0, a + Math.PI, 0, 'YXZ');
          _q.setFromEuler(ce);
          _m.compose(new THREE.Vector3(cx + Math.sin(a) * 0.85, c === 0 ? -0.3 : 0, cz + Math.cos(a) * 0.85), _q, new THREE.Vector3(1, 1, 1));
          emitProp(g, T.chair, _m, mode, r(), false);
        }
      }
    }
    if (modeR > 0.97) modeR = 0;
    _q.setFromEuler(_e);
    _m.compose(new THREE.Vector3(px, py, pz), _q, new THREE.Vector3(1, 1, 1));
    emitProp(g, T[name], _m, name === 'exitsign' ? 4 : mode, seed, name === 'exitsign');
    spots?.push({ x: px + ci * CHUNK_CELLS * CELL, z: pz + cj * CHUNK_CELLS * CELL, name });
  }
  return g.geometry();
}
