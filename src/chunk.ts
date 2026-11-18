// Chunk streaming. A chunk is 64x64 occupancy cells (32 m). Each one becomes:
//   - one mesh for floor + greedy-merged ceiling + greedy-merged walls (1 draw call)
//   - one mesh for its props (1 draw call)
//   - a 96x96 occupancy/height texture (chunk + 16-cell border) used for soft shadows and AO
//   - a 16x16 light-state texture for the ceiling-panel lattice that covers it
import * as THREE from 'three';
import { BORDER, CELL, CHUNK, CHUNK_CELLS, H_SCALE, LIGHT_PERIOD, TEX, ceilingAt, hash3, zoneAt, lightKind, solid } from './gen';
import { GeoBuilder, buildProps, type PropSpot } from './props';
import { levelFragment, levelVertex } from './shaders';

export interface Shared {
  uTime: { value: number };
  uSeed: { value: number };
  uFogColor: { value: THREE.Color };
  uFogDensity: { value: number };
  uQuality: { value: number };
}

export interface LightInfo {
  x: number;
  z: number;
  y: number;
  kind: number;
}

export interface Chunk {
  ci: number;
  cj: number;
  group: THREE.Group;
  lights: LightInfo[];
  props: PropSpot[];
  /** the per-chunk textures/uniform values, so moving objects can be lit like the chunk they stand in */
  env: { occ: THREE.Texture; lights: THREE.Texture; origin: THREE.Vector2; lbase: THREE.Vector2 };
  dispose(): void;
}

const V = (x: number, y: number, z: number) => new THREE.Vector3(x, y, z);

export function makeMaterials(shared: Shared) {
  const mk = (prop: boolean) =>
    new THREE.ShaderMaterial({
      vertexShader: levelVertex,
      fragmentShader: levelFragment,
      defines: prop ? { PROP: 1 } : {},
      side: prop ? THREE.DoubleSide : THREE.FrontSide,
      uniforms: {
        uOcc: { value: null },
        uLights: { value: null },
        uChunkOrigin: { value: new THREE.Vector2() },
        uLightBase: { value: new THREE.Vector2() },
        ...shared,
      },
    });
  return { level: mk(false), prop: mk(true) };
}

function cloneFor(base: THREE.ShaderMaterial, shared: Shared, occ: THREE.Texture, lights: THREE.Texture, origin: THREE.Vector2, lbase: THREE.Vector2) {
  const m = base.clone();
  // share the global uniforms by reference; per-chunk ones are unique
  Object.assign(m.uniforms, shared);
  m.uniforms.uOcc = { value: occ };
  m.uniforms.uLights = { value: lights };
  m.uniforms.uChunkOrigin = { value: origin };
  m.uniforms.uLightBase = { value: lbase };
  return m;
}

export function buildChunk(ci: number, cj: number, mats: { level: THREE.ShaderMaterial; prop: THREE.ShaderMaterial }, shared: Shared): Chunk {
  const gx0 = ci * CHUNK_CELLS - BORDER;
  const gz0 = cj * CHUNK_CELLS - BORDER;
  // occupancy + ceiling height over the bordered window
  const solidGrid = new Uint8Array(TEX * TEX);
  const hGrid = new Float32Array(TEX * TEX);
  const occData = new Uint8Array(TEX * TEX * 4);
  for (let z = 0; z < TEX; z++)
    for (let x = 0; x < TEX; x++) {
      const i = z * TEX + x;
      const s = solid(gx0 + x, gz0 + z);
      const h = ceilingAt(gx0 + x, gz0 + z);
      solidGrid[i] = s ? 1 : 0;
      hGrid[i] = h;
      occData[i * 4] = s ? 255 : 0;
      occData[i * 4 + 1] = Math.round((h / H_SCALE) * 255);
      occData[i * 4 + 2] = zoneAt(gx0 + x, gz0 + z).rail ? 255 : 0;
      occData[i * 4 + 3] = 255;
    }
  const S = (lx: number, lz: number) => solidGrid[(lz + BORDER) * TEX + lx + BORDER] === 1;
  const Hh = (lx: number, lz: number) => hGrid[(lz + BORDER) * TEX + lx + BORDER];
  // the stored height is quantised; geometry must use the same value the shader decodes
  const Hq = (lx: number, lz: number) => (Math.round((Hh(lx, lz) / H_SCALE) * 255) / 255) * H_SCALE;

  const occTex = new THREE.DataTexture(occData, TEX, TEX, THREE.RGBAFormat, THREE.UnsignedByteType);
  occTex.magFilter = THREE.LinearFilter;
  occTex.minFilter = THREE.LinearFilter;
  occTex.needsUpdate = true;

  // light lattice covering the chunk with a margin of two periods
  const ox = ci * CHUNK;
  const oz = cj * CHUNK;
  const li0 = Math.floor((ox - 2 * LIGHT_PERIOD) / LIGHT_PERIOD);
  const lj0 = Math.floor((oz - 2 * LIGHT_PERIOD) / LIGHT_PERIOD);
  const lData = new Uint8Array(16 * 16 * 4);
  const lights: LightInfo[] = [];
  for (let j = 0; j < 16; j++)
    for (let i = 0; i < 16; i++) {
      const li = li0 + i;
      const lj = lj0 + j;
      const k = lightKind(li, lj);
      const cx = (li + 0.5) * LIGHT_PERIOD;
      const cz = (lj + 0.5) * LIGHT_PERIOD;
      const h = ceilingAt(Math.floor(cx / CELL), Math.floor(cz / CELL));
      const o = (j * 16 + i) * 4;
      lData[o] = Math.round((k / 3) * 255);
      lData[o + 1] = Math.round((h / H_SCALE) * 255);
      lData[o + 2] = Math.round(hash3(li, lj, 80) * 255);
      lData[o + 3] = 255;
      if (k > 0 && cx >= ox && cx < ox + CHUNK && cz >= oz && cz < oz + CHUNK) lights.push({ x: cx, z: cz, y: h, kind: k });
    }
  const lTex = new THREE.DataTexture(lData, 16, 16, THREE.RGBAFormat, THREE.UnsignedByteType);
  lTex.needsUpdate = true;

  // ---- level geometry ----
  const g = new GeoBuilder();
  const Z3 = [0, 0, 0, 0];
  const none = [0, 0, 0];
  g.quad(V(0, 0, 0), V(CHUNK, 0, 0), V(0, 0, CHUNK), V(0, 1, 0), none, 0, 0, 0, 0, 0, Z3);

  // ceiling: greedy rectangles of equal height over open cells
  const N = CHUNK_CELLS;
  const used = new Uint8Array(N * N);
  for (let z = 0; z < N; z++)
    for (let x = 0; x < N; x++) {
      if (used[z * N + x] || S(x, z)) continue;
      const h = Hq(x, z);
      let w = 1;
      while (x + w < N && !used[z * N + x + w] && !S(x + w, z) && Hq(x + w, z) === h) w++;
      let d = 1;
      outer: while (z + d < N) {
        for (let k = 0; k < w; k++) if (used[(z + d) * N + x + k] || S(x + k, z + d) || Hq(x + k, z + d) !== h) break outer;
        d++;
      }
      for (let dz = 0; dz < d; dz++) for (let k = 0; k < w; k++) used[(z + dz) * N + x + k] = 1;
      g.quad(V(x * CELL, h, z * CELL), V(w * CELL, 0, 0), V(0, 0, d * CELL), V(0, -1, 0), none, 1, 0, 0, 0, 0, Z3);
    }

  // vertical faces on every cell boundary owned by this chunk, merged into runs
  // boundary between a=(x-1) and b=(x) along x; same for z
  type Face = { sign: number; y0: number; y1: number } | null;
  const faceAt = (ax: number, az: number, bx: number, bz: number): Face => {
    const sa = S(ax, az);
    const sb = S(bx, bz);
    if (sa && sb) return null;
    if (sa) return { sign: -1, y0: 0, y1: Hq(bx, bz) }; // faces towards a (negative axis)... normal points into b? see below
    if (sb) return { sign: 1, y0: 0, y1: Hq(ax, az) };
    const ha = Hq(ax, az);
    const hb = Hq(bx, bz);
    if (ha === hb) return null;
    return ha < hb ? { sign: -1, y0: ha, y1: hb } : { sign: 1, y0: hb, y1: ha };
  };
  // sign = +1: face visible from the a side (normal points to -axis... we define normal = -axis * ... )
  // Concretely: solid b, open a -> the wall surface faces a, normal = -axis. sign=+1 => normal -axis.
  const same = (p: Face, q: Face) => p && q && p.sign === q.sign && p.y0 === q.y0 && p.y1 === q.y1;
  for (let x = 0; x < N; x++) {
    let z = 0;
    while (z < N) {
      const f = faceAt(x - 1, z, x, z);
      if (!f) {
        z++;
        continue;
      }
      let len = 1;
      while (z + len < N && same(f, faceAt(x - 1, z + len, x, z + len))) len++;
      const n = V(-f.sign, 0, 0);
      g.quad(V(x * CELL, f.y0, z * CELL), V(0, 0, len * CELL), V(0, f.y1 - f.y0, 0), n, none, 2, 0, 0, 0, 0, Z3);
      z += len;
    }
  }
  for (let z = 0; z < N; z++) {
    let x = 0;
    while (x < N) {
      const f = faceAt(x, z - 1, x, z);
      if (!f) {
        x++;
        continue;
      }
      let len = 1;
      while (x + len < N && same(f, faceAt(x + len, z - 1, x + len, z))) len++;
      const n = V(0, 0, -f.sign);
      g.quad(V(x * CELL, f.y0, z * CELL), V(len * CELL, 0, 0), V(0, f.y1 - f.y0, 0), n, none, 2, 0, 0, 0, 0, Z3);
      x += len;
    }
  }

  const origin = new THREE.Vector2(ox, oz);
  const lbase = new THREE.Vector2(li0, lj0);
  const group = new THREE.Group();
  group.position.set(ox, 0, oz);
  const levelMat = cloneFor(mats.level, shared, occTex, lTex, origin, lbase);
  const levelGeo = g.geometry()!;
  const level = new THREE.Mesh(levelGeo, levelMat);
  level.matrixAutoUpdate = false;
  group.add(level);
  let propGeo: THREE.BufferGeometry | null = null;
  let propMat: THREE.ShaderMaterial | null = null;
  const props: PropSpot[] = [];
  propGeo = buildProps(ci, cj, props);
  if (propGeo) {
    propMat = cloneFor(mats.prop, shared, occTex, lTex, origin, lbase);
    const pm = new THREE.Mesh(propGeo, propMat);
    pm.matrixAutoUpdate = false;
    group.add(pm);
  }
  group.updateMatrixWorld(true);

  return {
    ci,
    cj,
    group,
    lights,
    props,
    env: { occ: occTex, lights: lTex, origin, lbase },
    dispose() {
      levelGeo.dispose();
      levelMat.dispose();
      propGeo?.dispose();
      propMat?.dispose();
      occTex.dispose();
      lTex.dispose();
    },
  };
}

/** Keeps the chunks around the camera built, within a per-frame time budget. */
export class ChunkManager {
  chunks = new Map<string, Chunk>();
  constructor(
    private scene: THREE.Scene,
    private mats: { level: THREE.ShaderMaterial; prop: THREE.ShaderMaterial },
    private shared: Shared,
    public radius = 2,
  ) {}

  /** Returns the number of chunks still missing. */
  update(x: number, z: number, budgetMs = 6): number {
    const ci = Math.floor(x / CHUNK);
    const cj = Math.floor(z / CHUNK);
    const want: [number, number, number][] = [];
    const R = this.radius;
    for (let j = cj - R; j <= cj + R; j++)
      for (let i = ci - R; i <= ci + R; i++) {
        if (this.chunks.has(i + ',' + j)) continue;
        const cx = (i + 0.5) * CHUNK - x;
        const cz = (j + 0.5) * CHUNK - z;
        want.push([cx * cx + cz * cz, i, j]);
      }
    want.sort((a, b) => a[0] - b[0]);
    const t0 = performance.now();
    let built = 0;
    for (const [, i, j] of want) {
      if (built > 0 && performance.now() - t0 > budgetMs) break;
      const c = buildChunk(i, j, this.mats, this.shared);
      this.chunks.set(i + ',' + j, c);
      this.scene.add(c.group);
      built++;
    }
    for (const [k, c] of this.chunks) {
      if (Math.abs(c.ci - ci) > R + 1 || Math.abs(c.cj - cj) > R + 1) {
        this.scene.remove(c.group);
        c.dispose();
        this.chunks.delete(k);
      }
    }
    return want.length - built;
  }

  at(x: number, z: number): Chunk | undefined {
    return this.chunks.get(Math.floor(x / CHUNK) + ',' + Math.floor(z / CHUNK));
  }

  *lights() {
    for (const c of this.chunks.values()) yield* c.lights;
  }
}
