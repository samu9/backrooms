// Fog-of-war minimap. Every ~100 ms a fan of rays is cast through the occupancy grid from the
// player; each cell a ray reaches is marked as discovered and painted, one pixel per cell, into a
// small per-chunk canvas. The map is drawn by blitting those chunk canvases, rotated so that the
// direction you face is always up.
import { CELL, CHUNK, CHUNK_CELLS, solid } from './gen';

const RAYS = 160;
const RANGE = 18; // metres
const STEP = CELL * 0.5;
const FLOOR = [226, 208, 128, 170];
const WALL = [58, 48, 18, 245];

export interface Marker {
  id: number;
  x: number;
  z: number;
  color: string;
  reached: boolean;
}

const COLORS = ['#ff3b2f', '#2fd3ff', '#7dff4a', '#ff9f1c', '#e14bff', '#ffffff'];
export const REACH_DIST = 1.4;

function drawPin(c: CanvasRenderingContext2D, x: number, y: number, m: Marker, u: number) {
  c.save();
  c.translate(x, y);
  c.globalAlpha = m.reached ? 0.45 : 1;
  c.fillStyle = m.color;
  c.strokeStyle = 'rgba(0,0,0,0.75)';
  c.lineWidth = 1.5 * u;
  c.beginPath();
  c.moveTo(0, 0);
  c.lineTo(-5 * u, -9 * u);
  c.arc(0, -11 * u, 5.4 * u, Math.PI * 0.82, Math.PI * 0.18);
  c.closePath();
  if (m.reached) c.stroke();
  else {
    c.fill();
    c.stroke();
  }
  c.fillStyle = m.reached ? m.color : '#111';
  c.font = `bold ${7.5 * u}px "Courier New", monospace`;
  c.textAlign = 'center';
  c.textBaseline = 'middle';
  c.fillText(m.reached ? '✓' : String(m.id), 0, -11 * u);
  c.restore();
}

/** Arrow for another player; `rot` is the canvas rotation (0 = pointing up). */
export function drawPlayer(c: CanvasRenderingContext2D, x: number, y: number, rot: number, color: string, u: number) {
  c.save();
  c.translate(x, y);
  c.rotate(rot);
  c.fillStyle = color;
  c.strokeStyle = 'rgba(0,0,0,0.8)';
  c.lineWidth = u;
  c.beginPath();
  c.moveTo(0, -6 * u);
  c.lineTo(4.5 * u, 5 * u);
  c.lineTo(0, 2 * u);
  c.lineTo(-4.5 * u, 5 * u);
  c.closePath();
  c.fill();
  c.stroke();
  c.restore();
}

interface Tile {
  canvas: HTMLCanvasElement;
  ctx: CanvasRenderingContext2D;
  img: ImageData;
  seen: Uint8Array;
  dirty: boolean;
}

export class Minimap {
  private tiles = new Map<string, Tile>();
  private ctx: CanvasRenderingContext2D;
  private acc = 0;
  private size = 0;
  /** pixels per metre */
  scale = 4;
  markers: Marker[] = [];
  /** other players, drawn as coloured arrows */
  others: { x: number; z: number; yaw: number; color: string; name: string }[] = [];
  private nextId = 1;
  onReached: (m: Marker) => void = () => {};

  addMarker(x: number, z: number): Marker {
    const m = { id: this.nextId, x, z, color: COLORS[(this.nextId - 1) % COLORS.length], reached: false };
    this.nextId++;
    this.markers.push(m);
    return m;
  }

  /** Remove the marker nearest to (x, z) within r metres. */
  removeNear(x: number, z: number, r: number): boolean {
    let best = -1,
      bd = r * r;
    this.markers.forEach((m, i) => {
      const d = (m.x - x) ** 2 + (m.z - z) ** 2;
      if (d < bd) {
        bd = d;
        best = i;
      }
    });
    if (best < 0) return false;
    this.markers.splice(best, 1);
    return true;
  }

  /** Next unreached marker, in the order they were placed. */
  get target(): Marker | undefined {
    return this.markers.find((m) => !m.reached);
  }

  checkReached(x: number, z: number) {
    for (const m of this.markers) {
      if (!m.reached && (m.x - x) ** 2 + (m.z - z) ** 2 < REACH_DIST * REACH_DIST) {
        m.reached = true;
        this.onReached(m);
      }
    }
  }

  /** Blit the discovered tiles covering a world-space rectangle. Transform must already be set. */
  blitTiles(c: CanvasRenderingContext2D, x0: number, z0: number, x1: number, z1: number) {
    c.imageSmoothingEnabled = false;
    for (let cj = Math.floor(z0 / CHUNK); cj <= Math.floor(z1 / CHUNK); cj++)
      for (let ci = Math.floor(x0 / CHUNK); ci <= Math.floor(x1 / CHUNK); ci++) {
        const t = this.tiles.get(ci + ',' + cj);
        if (!t) continue;
        if (t.dirty) {
          t.ctx.putImageData(t.img, 0, 0);
          t.dirty = false;
        }
        c.drawImage(t.canvas, ci * CHUNK, cj * CHUNK, CHUNK, CHUNK);
      }
  }

  constructor(private canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext('2d')!;
    this.resize();
  }

  resize() {
    const css = this.canvas.clientWidth || 200;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    this.size = Math.round(css * dpr);
    this.canvas.width = this.canvas.height = this.size;
  }

  private tile(ci: number, cj: number): Tile {
    const k = ci + ',' + cj;
    let t = this.tiles.get(k);
    if (!t) {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = CHUNK_CELLS;
      const ctx = canvas.getContext('2d')!;
      t = { canvas, ctx, img: ctx.createImageData(CHUNK_CELLS, CHUNK_CELLS), seen: new Uint8Array(CHUNK_CELLS * CHUNK_CELLS), dirty: false };
      this.tiles.set(k, t);
    }
    return t;
  }

  private mark(gx: number, gz: number, wall: boolean) {
    const ci = Math.floor(gx / CHUNK_CELLS);
    const cj = Math.floor(gz / CHUNK_CELLS);
    const t = this.tile(ci, cj);
    const lx = gx - ci * CHUNK_CELLS;
    const lz = gz - cj * CHUNK_CELLS;
    const i = lz * CHUNK_CELLS + lx;
    if (t.seen[i]) return;
    t.seen[i] = 1;
    const c = wall ? WALL : FLOOR;
    t.img.data.set(c, i * 4);
    t.dirty = true;
  }

  /** Reveal what is visible from (x, z). Throttled internally. */
  reveal(x: number, z: number, dt: number, force = false) {
    this.acc += dt;
    if (!force && this.acc < 0.1) return;
    this.acc = 0;
    for (let r = 0; r < RAYS; r++) {
      const a = (r / RAYS) * Math.PI * 2;
      const dx = Math.cos(a);
      const dz = Math.sin(a);
      let lastX = NaN,
        lastZ = NaN;
      for (let d = 0; d < RANGE; d += STEP) {
        const gx = Math.floor((x + dx * d) / CELL);
        const gz = Math.floor((z + dz * d) / CELL);
        if (gx === lastX && gz === lastZ) continue;
        lastX = gx;
        lastZ = gz;
        if (solid(gx, gz)) {
          this.mark(gx, gz, true);
          break;
        }
        this.mark(gx, gz, false);
      }
    }
  }

  draw(x: number, z: number, yaw: number) {
    const c = this.ctx;
    const S = this.size;
    const k = (this.scale * S) / 200; // scale is defined for a 200 px map
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.clearRect(0, 0, S, S);
    c.save();
    c.beginPath();
    c.arc(S / 2, S / 2, S / 2 - 1, 0, Math.PI * 2);
    c.clip();
    c.fillStyle = 'rgba(14, 12, 5, 0.55)';
    c.fillRect(0, 0, S, S);
    // world -> map: centre on the player, forward (-sin yaw, -cos yaw) points up
    c.translate(S / 2, S / 2);
    c.rotate(yaw);
    c.scale(k, k);
    c.translate(-x, -z);
    const reach = S / k; // metres from centre to corner, generously
    this.blitTiles(c, x - reach, z - reach, x + reach, z + reach);
    c.restore();
    // player marker: a small arrow pointing up
    c.setTransform(1, 0, 0, 1, S / 2, S / 2);
    const u = S / 200;
    c.fillStyle = '#ff3b2f';
    c.beginPath();
    c.moveTo(0, -7 * u);
    c.lineTo(5 * u, 5 * u);
    c.lineTo(0, 2 * u);
    c.lineTo(-5 * u, 5 * u);
    c.closePath();
    c.fill();
    // rim
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.strokeStyle = 'rgba(242, 240, 230, 0.6)';
    c.lineWidth = 1.5 * u;
    c.beginPath();
    c.arc(S / 2, S / 2, S / 2 - u, 0, Math.PI * 2);
    c.stroke();
    // markers: inside the disc as pins, outside clamped to the rim as arrows
    const cs = Math.cos(yaw),
      sn = Math.sin(yaw);
    const R = S / 2 - 9 * u;
    for (const m of this.markers) {
      const dx = (m.x - x) * k,
        dz = (m.z - z) * k;
      const mx = dx * cs - dz * sn;
      const my = dx * sn + dz * cs;
      const d = Math.hypot(mx, my);
      if (d < R) drawPin(c, S / 2 + mx, S / 2 + my, m, u * 0.85);
      else if (!m.reached) {
        const ax = mx / d,
          ay = my / d;
        c.save();
        c.translate(S / 2 + ax * (S / 2 - 7 * u), S / 2 + ay * (S / 2 - 7 * u));
        c.rotate(Math.atan2(ay, ax) + Math.PI / 2);
        c.fillStyle = m.color;
        c.strokeStyle = 'rgba(0,0,0,0.7)';
        c.lineWidth = u;
        c.beginPath();
        c.moveTo(0, -6 * u);
        c.lineTo(5 * u, 4 * u);
        c.lineTo(-5 * u, 4 * u);
        c.closePath();
        c.fill();
        c.stroke();
        c.restore();
      }
    }
    for (const o of this.others) {
      const dx = (o.x - x) * k,
        dz = (o.z - z) * k;
      const mx = dx * cs - dz * sn;
      const my = dx * sn + dz * cs;
      const d = Math.hypot(mx, my);
      if (d < R) drawPlayer(c, S / 2 + mx, S / 2 + my, yaw - o.yaw, o.color, u * 0.8);
      else {
        c.fillStyle = o.color;
        c.globalAlpha = 0.85;
        c.beginPath();
        c.arc(S / 2 + (mx / d) * (S / 2 - 6 * u), S / 2 + (my / d) * (S / 2 - 6 * u), 3 * u, 0, Math.PI * 2);
        c.fill();
        c.globalAlpha = 1;
      }
    }
  }
}

/**
 * Full-screen map: north-up, drag to pan, wheel to zoom, left click places a mark, right click
 * removes one. Shares the discovered tiles and the markers with the minimap.
 */
export class FullMap {
  open = false;
  cx = 0;
  cz = 0;
  scale = 8; // px per metre (CSS px)
  private ctx: CanvasRenderingContext2D;
  private dpr = 1;
  private drag: { x: number; y: number; cx: number; cz: number; moved: boolean } | null = null;
  private keys = new Set<string>();

  constructor(
    private canvas: HTMLCanvasElement,
    private map: Minimap,
  ) {
    this.ctx = canvas.getContext('2d')!;
    canvas.addEventListener('contextmenu', (e) => e.preventDefault());
    canvas.addEventListener('pointerdown', (e) => {
      if (e.button === 2) {
        const [wx, wz] = this.toWorld(e.clientX, e.clientY);
        this.map.removeNear(wx, wz, 14 / this.scale + 0.5);
        return;
      }
      if (e.button !== 0) return;
      canvas.setPointerCapture(e.pointerId);
      this.drag = { x: e.clientX, y: e.clientY, cx: this.cx, cz: this.cz, moved: false };
    });
    canvas.addEventListener('pointermove', (e) => {
      const d = this.drag;
      if (!d) return;
      if (Math.hypot(e.clientX - d.x, e.clientY - d.y) > 4) d.moved = true;
      if (d.moved) {
        this.cx = d.cx - (e.clientX - d.x) / this.scale;
        this.cz = d.cz - (e.clientY - d.y) / this.scale;
      }
    });
    canvas.addEventListener('pointerup', (e) => {
      const d = this.drag;
      this.drag = null;
      if (d && !d.moved && e.button === 0) {
        const [wx, wz] = this.toWorld(e.clientX, e.clientY);
        this.map.addMarker(wx, wz);
      }
    });
    canvas.addEventListener(
      'wheel',
      (e) => {
        e.preventDefault();
        // zoom around the cursor
        const [wx, wz] = this.toWorld(e.clientX, e.clientY);
        this.scale = Math.min(40, Math.max(0.6, this.scale * Math.exp(-e.deltaY * 0.0015)));
        const [nx, nz] = this.toWorld(e.clientX, e.clientY);
        this.cx += wx - nx;
        this.cz += wz - nz;
      },
      { passive: false },
    );
    addEventListener('keydown', (e) => this.open && this.keys.add(e.code));
    addEventListener('keyup', (e) => this.keys.delete(e.code));
  }

  private toWorld(sx: number, sy: number): [number, number] {
    const r = this.canvas.getBoundingClientRect();
    return [this.cx + (sx - r.left - r.width / 2) / this.scale, this.cz + (sy - r.top - r.height / 2) / this.scale];
  }

  show(px: number, pz: number) {
    this.open = true;
    this.cx = px;
    this.cz = pz;
    this.keys.clear();
    this.canvas.classList.remove('hidden');
  }

  hide() {
    this.open = false;
    this.drag = null;
    this.canvas.classList.add('hidden');
  }

  center(px: number, pz: number) {
    this.cx = px;
    this.cz = pz;
  }

  draw(px: number, pz: number, yaw: number, dt: number) {
    if (!this.open) return;
    // keyboard panning
    const pan = (500 / this.scale) * dt;
    if (this.keys.has('KeyW') || this.keys.has('ArrowUp')) this.cz -= pan;
    if (this.keys.has('KeyS') || this.keys.has('ArrowDown')) this.cz += pan;
    if (this.keys.has('KeyA') || this.keys.has('ArrowLeft')) this.cx -= pan;
    if (this.keys.has('KeyD') || this.keys.has('ArrowRight')) this.cx += pan;
    if (this.keys.has('Equal') || this.keys.has('NumpadAdd')) this.scale = Math.min(40, this.scale * (1 + dt * 1.5));
    if (this.keys.has('Minus') || this.keys.has('NumpadSubtract')) this.scale = Math.max(0.6, this.scale / (1 + dt * 1.5));

    const cv = this.canvas;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const W = cv.clientWidth,
      H = cv.clientHeight;
    if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr) || dpr !== this.dpr) {
      cv.width = Math.round(W * dpr);
      cv.height = Math.round(H * dpr);
      this.dpr = dpr;
    }
    const c = this.ctx;
    const k = this.scale;
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.fillStyle = 'rgba(16, 14, 6, 0.93)';
    c.fillRect(0, 0, cv.width, cv.height);
    // world transform
    c.setTransform(dpr * k, 0, 0, dpr * k, dpr * (W / 2 - this.cx * k), dpr * (H / 2 - this.cz * k));
    const x0 = this.cx - W / 2 / k,
      x1 = this.cx + W / 2 / k;
    const z0 = this.cz - H / 2 / k,
      z1 = this.cz + H / 2 / k;
    // faint 32 m grid (the chunk grid) under the map
    c.strokeStyle = 'rgba(226, 208, 128, 0.08)';
    c.lineWidth = 1 / k;
    c.beginPath();
    for (let x = Math.floor(x0 / CHUNK) * CHUNK; x <= x1; x += CHUNK) {
      c.moveTo(x, z0);
      c.lineTo(x, z1);
    }
    for (let z = Math.floor(z0 / CHUNK) * CHUNK; z <= z1; z += CHUNK) {
      c.moveTo(x0, z);
      c.lineTo(x1, z);
    }
    c.stroke();
    this.map.blitTiles(c, x0, z0, x1, z1);

    // screen-space overlays
    const sx = (wx: number) => dpr * (W / 2 + (wx - this.cx) * k);
    const sy = (wz: number) => dpr * (H / 2 + (wz - this.cz) * k);
    c.setTransform(1, 0, 0, 1, 0, 0);
    const u = dpr * 1.6;
    // route through the unreached marks, in order
    const route = this.map.markers.filter((m) => !m.reached);
    if (route.length) {
      c.setLineDash([4 * u, 4 * u]);
      c.strokeStyle = 'rgba(242, 240, 230, 0.35)';
      c.lineWidth = u;
      c.beginPath();
      c.moveTo(sx(px), sy(pz));
      for (const m of route) c.lineTo(sx(m.x), sy(m.z));
      c.stroke();
      c.setLineDash([]);
    }
    for (const m of this.map.markers) drawPin(c, sx(m.x), sy(m.z), m, u);
    c.font = `${12 * dpr}px "Courier New", monospace`;
    c.textAlign = 'center';
    for (const o of this.map.others) {
      drawPlayer(c, sx(o.x), sy(o.z), -o.yaw, o.color, u);
      c.fillStyle = 'rgba(242, 240, 230, 0.9)';
      c.fillText(o.name, sx(o.x), sy(o.z) - 12 * u);
    }
    // player
    c.save();
    c.translate(sx(px), sy(pz));
    c.rotate(-yaw);
    c.fillStyle = '#ff3b2f';
    c.strokeStyle = 'rgba(0,0,0,0.8)';
    c.lineWidth = u * 0.6;
    c.beginPath();
    c.moveTo(0, -8 * u);
    c.lineTo(5.5 * u, 6 * u);
    c.lineTo(0, 2.5 * u);
    c.lineTo(-5.5 * u, 6 * u);
    c.closePath();
    c.fill();
    c.stroke();
    c.restore();

    // scale bar + compass + help
    c.fillStyle = 'rgba(242, 240, 230, 0.85)';
    c.font = `${13 * dpr}px "Courier New", monospace`;
    c.textBaseline = 'alphabetic';
    const nice = [1, 2, 5, 10, 20, 50, 100, 200, 500].find((v) => v * k > 70) ?? 1000;
    const bx = 24 * dpr,
      by = cv.height - 28 * dpr;
    c.fillRect(bx, by, nice * k * dpr, 2 * dpr);
    c.fillRect(bx, by - 5 * dpr, 2 * dpr, 7 * dpr);
    c.fillRect(bx + nice * k * dpr - 2 * dpr, by - 5 * dpr, 2 * dpr, 7 * dpr);
    c.textAlign = 'left';
    c.fillText(`${nice} m`, bx, by - 8 * dpr);
    c.textAlign = 'center';
    c.fillText('N', cv.width - 34 * dpr, 34 * dpr);
    c.beginPath();
    c.moveTo(cv.width - 34 * dpr, 40 * dpr);
    c.lineTo(cv.width - 39 * dpr, 54 * dpr);
    c.lineTo(cv.width - 29 * dpr, 54 * dpr);
    c.fill();
    c.textAlign = 'right';
    c.fillText('click: place mark · right click: remove · drag / WASD: pan · wheel / + -: zoom · C: centre · M: close', cv.width - 24 * dpr, cv.height - 24 * dpr);
    c.textAlign = 'left';
    c.font = `${20 * dpr}px "Courier New", monospace`;
    c.fillText('MAP — LEVEL 0', 24 * dpr, 38 * dpr);
    c.font = `${13 * dpr}px "Courier New", monospace`;
    const todo = route.length;
    c.fillText(`${this.map.markers.length} marks · ${todo} to reach`, 24 * dpr, 60 * dpr);
  }
}
