import * as THREE from 'three';
import { Soundscape } from './audio';
import { ChunkManager, makeMaterials, type Shared } from './chunk';
import { CELL, findFree, freeDistance, rng, setSeed, solid } from './gen';
import { FullMap, Minimap } from './minimap';
import { Post } from './post';

const q = new URLSearchParams(location.search);
const num = (k: string, d: number) => (q.has(k) ? Number(q.get(k)) : d);
const SHOT = q.has('shot');
const seed = num('seed', SHOT ? 1 : Math.floor(Math.random() * 1e6));
setSeed(seed);

// ---------- renderer ----------
const canvas = document.getElementById('c') as HTMLCanvasElement;
const renderer = new THREE.WebGLRenderer({ canvas, antialias: false, powerPreference: 'high-performance', preserveDrawingBuffer: SHOT });
renderer.setPixelRatio(1);
renderer.autoClear = true;
renderer.info.autoReset = false;
const scene = new THREE.Scene();
const fogColor = new THREE.Color(0.62, 0.55, 0.34);
scene.background = fogColor.clone().multiplyScalar(0.4);
const camera = new THREE.PerspectiveCamera(num('fov', 66), 1, 0.05, 140);
camera.rotation.order = 'YXZ';

const shared: Shared = {
  uTime: { value: 0 },
  uSeed: { value: seed },
  uFogColor: { value: fogColor },
  uFogDensity: { value: num('fog', 0.022) },
  uQuality: { value: num('q', 1) },
};
const mats = makeMaterials(shared);
const chunks = new ChunkManager(scene, mats, shared, num('radius', 2));

let scale = num('scale', SHOT ? 1 : 0.85);
const post = new Post(renderer, 2, 2, num('msaa', 4));
post.final.uniforms.uVhs.value = num('vhs', 1);
post.final.uniforms.uExposure.value = num('exp', 1.0);

function resize() {
  const w = window.innerWidth;
  const h = window.innerHeight;
  renderer.setSize(w, h, false);
  canvas.style.width = w + 'px';
  canvas.style.height = h + 'px';
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  const dpr = Math.min(window.devicePixelRatio, 2);
  post.setSize(Math.max(2, Math.floor(w * dpr * scale)), Math.max(2, Math.floor(h * dpr * scale)));
}
window.addEventListener('resize', resize);
resize();

// ---------- player ----------
const player = { x: 0, z: 0, yaw: 0, pitch: 0, vx: 0, vz: 0, bob: 0, noclip: false };
const EYE = 1.62;
const R = 0.26;

function collides(x: number, z: number): boolean {
  const g0x = Math.floor((x - R) / CELL), g1x = Math.floor((x + R) / CELL);
  const g0z = Math.floor((z - R) / CELL), g1z = Math.floor((z + R) / CELL);
  for (let gz = g0z; gz <= g1z; gz++)
    for (let gx = g0x; gx <= g1x; gx++) {
      if (!solid(gx, gz)) continue;
      const cx = Math.max(gx * CELL, Math.min(x, (gx + 1) * CELL));
      const cz = Math.max(gz * CELL, Math.min(z, (gz + 1) * CELL));
      if ((cx - x) ** 2 + (cz - z) ** 2 < R * R) return true;
    }
  return false;
}

/** A viewpoint with a long sight line, chosen deterministically from k. */
function viewpoint(k: number): { x: number; z: number; yaw: number } {
  const r = rng(k, 3, 909);
  for (let tries = 0; tries < 40; tries++) {
    const [x, z] = findFree((r() - 0.5) * 600, (r() - 0.5) * 600, 3);
    let best = 0,
      bestA = 0;
    for (let a = 0; a < 24; a++) {
      const t = (a / 24) * Math.PI * 2;
      const d = freeDistance(x, z, Math.cos(t), Math.sin(t), 50);
      if (d > best) {
        best = d;
        bestA = t;
      }
    }
    if (best > 12 || tries === 39) {
      const t = bestA + (r() - 0.5) * 0.35;
      return { x, z, yaw: Math.atan2(-Math.cos(t), -Math.sin(t)) };
    }
  }
  return { x: 0, z: 0, yaw: 0 };
}

if (q.has('view')) {
  const v = viewpoint(num('view', 0));
  Object.assign(player, v);
} else {
  const [x, z] = findFree(num('x', 0), num('z', 0), 2);
  player.x = x;
  player.z = z;
}
if (q.has('x') && q.has('z')) {
  player.x = num('x', 0);
  player.z = num('z', 0);
}
player.yaw = num('yaw', player.yaw);
player.pitch = num('pitch', 0);

const keys = new Set<string>();
addEventListener('keydown', (e) => {
  if (!fullmap.open) keys.add(e.code);
  if (e.code === 'KeyN') player.noclip = !player.noclip;
  if (e.code === 'KeyV') post.final.uniforms.uVhs.value = post.final.uniforms.uVhs.value > 0.5 ? 0 : 1;
  if (e.code === 'KeyH') osd.classList.toggle('hidden');
  if (e.code === 'Tab') {
    e.preventDefault();
    mapEl.classList.toggle('hidden');
  }
  if (e.code === 'BracketLeft') minimap.scale = Math.max(1.5, minimap.scale / 1.4);
  if (e.code === 'BracketRight') minimap.scale = Math.min(14, minimap.scale * 1.4);
  if (e.code === 'KeyK' && audio) audio.setMuted(!audio.isMuted);
  if (e.code === 'KeyM' && started) toggleFullMap();
  if (e.code === 'KeyC' && fullmap.open) fullmap.center(player.x, player.z);
  if (e.code === 'Escape' && fullmap.open) closeFullMap(false);
  if (e.code === 'KeyF') document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen();
});
addEventListener('keyup', (e) => keys.delete(e.code));
addEventListener('blur', () => keys.clear());
addEventListener('mousemove', (e) => {
  if (document.pointerLockElement !== canvas) return;
  player.yaw -= e.movementX * 0.0022;
  player.pitch = Math.max(-1.45, Math.min(1.45, player.pitch - e.movementY * 0.0022));
});

// ---------- UI ----------
const menu = document.getElementById('menu')!;
const osd = document.getElementById('osd')!;
const tc = document.getElementById('tc')!;
const stats = document.getElementById('stats')!;
const mapEl = document.getElementById('map') as HTMLCanvasElement;
const minimap = new Minimap(mapEl);
addEventListener('resize', () => minimap.resize());
if (SHOT && q.get('map') !== '1') mapEl.classList.add('hidden');
const fullEl = document.getElementById('fullmap') as HTMLCanvasElement;
const fullmap = new FullMap(fullEl, minimap);
const wpEl = document.getElementById('wp')!;
const toastEl = document.getElementById('toast')!;
let toastT = 0;
function toast(msg: string) {
  toastEl.textContent = msg;
  toastEl.classList.remove('hidden');
  toastT = 3;
}
minimap.onReached = (m) => toast(`MARK ${m.id} REACHED`);
function toggleFullMap() {
  if (fullmap.open) closeFullMap(true);
  else {
    fullmap.show(player.x, player.z);
    keys.clear();
    menu.classList.add('hidden');
    if (document.pointerLockElement === canvas) document.exitPointerLock();
  }
}
function closeFullMap(resume: boolean) {
  fullmap.hide();
  keys.clear();
  if (resume) canvas.requestPointerLock();
  else menu.classList.remove('hidden');
}
let audio: Soundscape | null = null;
let started = false;
menu.addEventListener('click', () => {
  canvas.requestPointerLock();
  if (!audio) {
    try {
      audio = new Soundscape();
    } catch (e) {
      console.warn('audio unavailable', e);
    }
  }
  audio?.start();
  started = true;
});
document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (fullmap.open) return; // the map released the mouse on purpose
  menu.classList.toggle('hidden', locked);
  if (!locked && audio) audio.setMuted(true);
  else if (locked && audio) audio.setMuted(false);
});
if (SHOT || q.get('hud') === '0') menu.classList.add('hidden');
if (SHOT && q.get('osd') !== '1') osd.classList.add('hidden');

// ---------- loop ----------
let time = num('t', 0);
let last = performance.now();
let frameAvg = 16;
let statT = 0;
const fwd = new THREE.Vector3();

function step(dt: number) {
  // movement
  const run = keys.has('ShiftLeft') || keys.has('ShiftRight');
  const speed = player.noclip ? 6 : run ? 3.4 : 1.6;
  let ix = 0,
    iz = 0;
  if (keys.has('KeyW') || keys.has('ArrowUp')) iz -= 1;
  if (keys.has('KeyS') || keys.has('ArrowDown')) iz += 1;
  if (keys.has('KeyA') || keys.has('ArrowLeft')) ix -= 1;
  if (keys.has('KeyD') || keys.has('ArrowRight')) ix += 1;
  const il = Math.hypot(ix, iz) || 1;
  const s = Math.sin(player.yaw),
    c = Math.cos(player.yaw);
  const tx = ((ix * c + iz * s) / il) * speed;
  const tz = ((-ix * s + iz * c) / il) * speed;
  const k = 1 - Math.exp(-dt * 8);
  player.vx += (tx - player.vx) * k;
  player.vz += (tz - player.vz) * k;
  const nx = player.x + player.vx * dt;
  if (player.noclip || !collides(nx, player.z)) player.x = nx;
  else player.vx = 0;
  const nz = player.z + player.vz * dt;
  if (player.noclip || !collides(player.x, nz)) player.z = nz;
  else player.vz = 0;
  const moving = Math.hypot(player.vx, player.vz);
  player.bob += dt * moving * 1.9;

  // handheld camcorder: bob while walking, slow drift always
  const sway = (f: number, p: number) => Math.sin(time * f + p) * 0.6 + Math.sin(time * f * 2.3 + p * 1.7) * 0.4;
  const bobY = Math.sin(player.bob * Math.PI * 2) * 0.03 * Math.min(1, moving);
  const bobX = Math.cos(player.bob * Math.PI) * 0.02 * Math.min(1, moving);
  camera.position.set(player.x + bobX * c, EYE + bobY, player.z - bobX * s);
  camera.rotation.set(player.pitch + sway(0.7, 1) * 0.006, player.yaw + sway(0.5, 4) * 0.008, sway(0.6, 2) * 0.006 + bobX * 0.15);
  camera.updateMatrixWorld();
  return moving;
}

function frame(now: number) {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  if (!SHOT) time += dt;
  shared.uTime.value = time % 4096;
  const moving = step(SHOT ? 0 : dt);
  chunks.update(player.x, player.z, started || SHOT ? 4 : 12);
  minimap.reveal(player.x, player.z, dt, SHOT);
  minimap.checkReached(player.x, player.z);
  fullmap.draw(player.x, player.z, player.yaw, dt);
  const tgt = minimap.target;
  if (tgt) {
    const d = Math.hypot(tgt.x - player.x, tgt.z - player.z);
    wpEl.innerHTML = `<span style="color:${tgt.color}">◆</span> MARK ${tgt.id} · ${d < 1000 ? d.toFixed(0) + ' M' : (d / 1000).toFixed(1) + ' KM'}`;
  } else wpEl.textContent = '';
  if (toastT > 0 && (toastT -= dt) <= 0) toastEl.classList.add('hidden');
  if (!mapEl.classList.contains('hidden')) minimap.draw(player.x, player.z, player.yaw);
  if (audio) {
    camera.getWorldDirection(fwd);
    audio.update({ x: camera.position.x, y: camera.position.y, z: camera.position.z }, fwd, chunks.lights(), moving / 3.4, dt);
  }
  renderer.info.reset();
  post.render(scene, camera, time);

  // dynamic resolution: keep frames under ~18 ms
  frameAvg = frameAvg * 0.95 + dt * 1000 * 0.05;
  if (!SHOT && !q.has('scale')) {
    statT += dt;
    if (statT > 1.5) {
      statT = 0;
      const ns = frameAvg > 19 ? Math.max(0.5, scale - 0.1) : frameAvg < 13 ? Math.min(1, scale + 0.05) : scale;
      if (ns !== scale) {
        scale = ns;
        resize();
      }
    }
  }
  const secs = Math.floor(time);
  tc.textContent = `${String(Math.floor(secs / 3600)).padStart(1, '0')}:${String(Math.floor(secs / 60) % 60).padStart(2, '0')}:${String(secs % 60).padStart(2, '0')}`;
  if (!stats.classList.contains('hidden'))
    stats.textContent = `${(1000 / frameAvg).toFixed(0)} fps · scale ${scale.toFixed(2)} · chunks ${chunks.chunks.size} · calls ${renderer.info.render.calls} · tris ${(renderer.info.render.triangles / 1000).toFixed(1)}k · ${player.x.toFixed(1)}, ${player.z.toFixed(1)}${player.noclip ? ' · noclip' : ''}`;
  if (!SHOT) requestAnimationFrame(frame);
}
addEventListener('keydown', (e) => {
  if (e.code === 'KeyP') stats.classList.toggle('hidden');
});

if (q.has('prop')) {
  // debug view: stand a few metres from the k-th prop found around the spawn
  while (chunks.update(player.x, player.z, 1e9) > 0);
  const all = [...chunks.chunks.values()].flatMap((c) => c.props).filter((p) => q.get('pname') ? p.name === q.get('pname') : true);
  all.sort((a, b) => a.x - b.x || a.z - b.z);
  const k = num('prop', 0);
  for (let i = 0; i < all.length; i++) {
    const p = all[(k + i) % all.length];
    let done = false;
    for (let a = 0; a < 16 && !done; a++) {
      const t = (a / 16) * Math.PI * 2;
      for (const d of [1.7, 2.2, 2.8]) {
        const x = p.x + Math.cos(t) * d, z = p.z + Math.sin(t) * d;
        if (collides(x, z)) continue;
        if (freeDistance(x, z, -Math.cos(t), -Math.sin(t), d) < d - 0.9) continue;
        player.x = x;
        player.z = z;
        player.yaw = Math.atan2(Math.cos(t), Math.sin(t));
        player.pitch = -0.32;
        done = true;
        break;
      }
    }
    if (done) break;
  }
}

if (SHOT) {
  // build everything in range, render a few frames, then signal the screenshot tool
  while (chunks.update(player.x, player.z, 1e9) > 0);
  (window as unknown as { __state: unknown }).__state = { x: player.x, z: player.z, yaw: player.yaw };
  for (let i = 0; i < 3; i++) frame(performance.now());
  renderer.getContext().finish();
  (window as unknown as { __ready: boolean }).__ready = true;
} else {
  requestAnimationFrame(frame);
}
