// Golden fixtures for the generator: what shared/gen.ts answers for a fixed set of questions, and
// what the web client's buildChunk makes from it. The C# port (godot/Core) is tested against this
// file, so both clients and the relay generate the same world cell for cell.
//
//   npm run gen:fixtures       # rewrites tests/fixtures/gen-golden.json.gz
//
// Inputs come from a fixed PRNG, so the file only changes when gen.ts does.
import { writeFileSync, mkdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { dirname, resolve } from 'node:path';
import { build } from 'rolldown';
import { ceilingAt, findFree, freeDistance, hash3, lightKind, rng, setSeed, solid, zoneAt } from '../../shared/gen.ts';

const OUT = resolve(import.meta.dirname, '../fixtures/gen-golden.json.gz');

// mulberry32, local to this script: picks the inputs
let st = 0x9e3779b9;
const rand = () => {
  st = (st + 0x6d2b79f5) >>> 0;
  let t = st;
  t = Math.imul(t ^ (t >>> 15), t | 1);
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const int = (lo: number, hi: number) => lo + Math.floor(rand() * (hi - lo + 1));

const b64 = (a: Uint8Array) => Buffer.from(a).toString('base64');
const bits = (n: number, f: (i: number) => boolean) => {
  const a = new Uint8Array(Math.ceil(n / 8));
  for (let i = 0; i < n; i++) if (f(i)) a[i >> 3] |= 1 << (i & 7);
  return a;
};

const SEEDS = [1, 123, -1000, 2147480000];
// windows of cells: around the origin, far out in each direction, across a lot of zone borders
const WINDOWS: [number, number][] = [
  [-64, -64],
  [5000, 3000],
  [-7000, -2500],
  [-64, 9000],
];
const W = 128;

const seeds = SEEDS.map((seed) => {
  setSeed(seed);

  const hashes: number[][] = [];
  const extremes = [0, 1, -1, 2147483647, -2147483648, 65535, -65536];
  for (const x of extremes) for (const y of extremes) hashes.push([x, y, 7, hash3(x, y, 7)]);
  for (let k = 0; k < 400; k++) {
    const x = int(-200000, 200000),
      y = int(-200000, 200000),
      z = int(-3000000, 3000000);
    hashes.push([x, y, z, hash3(x, y, z)]);
  }

  // ceiling heights come from a small set: store an index into `heights`, compare exactly
  const heights: number[] = [];
  const hIndex = (h: number) => {
    let i = heights.indexOf(h);
    if (i < 0) i = heights.push(h) - 1;
    return i;
  };
  const windows = WINDOWS.map(([gx0, gz0]) => {
    const n = W * W;
    const at = (i: number): [number, number] => [gx0 + (i % W), gz0 + Math.floor(i / W)];
    const ceil = new Uint8Array(n);
    for (let i = 0; i < n; i++) ceil[i] = hIndex(ceilingAt(...at(i)));
    return {
      gx0,
      gz0,
      w: W,
      solid: b64(bits(n, (i) => solid(...at(i)))),
      rail: b64(bits(n, (i) => zoneAt(...at(i)).rail)),
      ceil: b64(ceil),
    };
  });

  const zones: (number | boolean)[][] = [];
  for (let k = 0; k < 400; k++) {
    const gx = int(-40000, 40000),
      gz = int(-40000, 40000);
    const z = zoneAt(gx, gz);
    zones.push([gx, gz, z.type, z.id, z.S, z.off, z.p, z.pz, z.pd, z.dw, z.split, z.pillar, z.pillarSize, z.partial, z.h, z.rail]);
  }

  const L = 64,
    i0 = -32,
    j0 = -32;
  const kinds = new Uint8Array(L * L);
  for (let j = 0; j < L; j++) for (let i = 0; i < L; i++) kinds[j * L + i] = lightKind(i0 + i, j0 + j);

  const free: number[][] = [];
  for (let k = 0; k < 40; k++) {
    const x = (rand() - 0.5) * 6000,
      z = (rand() - 0.5) * 6000,
      clear = int(1, 3);
    free.push([x, z, clear, ...findFree(x, z, clear)]);
  }

  const dist: number[][] = [];
  for (let k = 0; k < 100; k++) {
    const x = (rand() - 0.5) * 2000,
      z = (rand() - 0.5) * 2000,
      a = rand() * Math.PI * 2;
    const dx = Math.cos(a),
      dz = Math.sin(a);
    dist.push([x, z, dx, dz, freeDistance(x, z, dx, dz, 60)]);
  }

  const rngs: (number | number[])[][] = [];
  for (let k = 0; k < 10; k++) {
    const a = int(-500, 500),
      b = int(-500, 500),
      salt = int(0, 1000);
    const r = rng(a, b, salt);
    rngs.push([a, b, salt, Array.from({ length: 16 }, r)]);
  }

  return { seed, hashes, heights, windows, zones, lights: { i0, j0, n: L, kinds: b64(kinds) }, findFree: free, freeDistance: dist, rng: rngs };
});

// ---- chunks: what the web client's buildChunk makes (textures in full, geometry as hashes) ----
// web/src imports without extensions, so bundle it (with three) and import the bundle.
const bundle = await build({ input: resolve(import.meta.dirname, 'chunk-entry.mjs'), write: false, platform: 'node', output: { format: 'esm' }, logLevel: 'silent' });
const web = await import('data:text/javascript;base64,' + Buffer.from(bundle.output[0].code).toString('base64'));

/** FNV-1a over the bytes, 32 bit: compact, and the same in C#. */
const fnv = (bytes: Uint8Array) => {
  let h = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) h = Math.imul(h ^ bytes[i], 0x01000193);
  return h >>> 0;
};
const f32hash = (a: ArrayLike<number>) => fnv(new Uint8Array(new Float32Array(a as ArrayLike<number>).buffer));

const sharedU = {
  uTime: { value: 0 },
  uSeed: { value: 0 },
  uFogColor: { value: null },
  uFogDensity: { value: 0 },
  uQuality: { value: 1 },
  uEnt: { value: [] },
};
const mats = web.makeMaterials(sharedU);
const CHUNKS: [number, number][] = [
  [0, 0],
  [-1, -1],
  [3, -2],
  [-40, 77],
];
const chunks = [1, -1000].flatMap((seed) => {
  web.setSeed(seed);
  return CHUNKS.map(([ci, cj]) => {
    const c = web.buildChunk(ci, cj, mats, sharedU);
    const geo = c.group.children[0].geometry;
    const attr = (k: string) => geo.getAttribute(k).array as Float32Array;
    const idx = Uint32Array.from(geo.index.array as ArrayLike<number>);
    const hashes = (g: any) => {
      if (!g) return null;
      const a = (k: string) => g.getAttribute(k).array as Float32Array;
      const ix = Uint32Array.from(g.index.array as ArrayLike<number>);
      return {
        vertices: a('position').length / 3,
        indices: ix.length,
        hash: { position: f32hash(a('position')), normal: f32hash(a('normal')), aCol: f32hash(a('aCol')), aA: f32hash(a('aA')), aB: f32hash(a('aB')), index: fnv(new Uint8Array(ix.buffer)) },
      };
    };
    const out = {
      seed,
      ci,
      cj,
      lightBase: [c.env.lbase.x, c.env.lbase.y],
      occ: b64(c.env.occ.image.data),
      lights: b64(c.env.lights.image.data),
      lightList: c.lights.map((l: { x: number; z: number; y: number; kind: number }) => [l.x, l.z, l.y, l.kind]),
      vertices: attr('position').length / 3,
      indices: idx.length,
      hash: {
        position: f32hash(attr('position')),
        normal: f32hash(attr('normal')),
        aCol: f32hash(attr('aCol')),
        aA: f32hash(attr('aA')),
        aB: f32hash(attr('aB')),
        index: fnv(new Uint8Array(idx.buffer)),
      },
      props: hashes(c.group.children[1]?.geometry),
      propSpots: c.props.map((p: { x: number; z: number; name: string }) => [p.x, p.z, p.name]),
    };
    c.dispose();
    return out;
  });
});

const json = JSON.stringify({ version: 2, source: 'shared/gen.ts, web/src/chunk.ts', seeds, chunks });
mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, gzipSync(json, { level: 9 }));
console.log(`${OUT}: ${(json.length / 1024).toFixed(0)} KB json, ${seeds.length} seeds`);
