// The CPU entity. It finds you the way a player would: by seeing you (a cone, shorter in the dark,
// blocked by walls, with a detection meter that takes a moment to fill) and by hearing you
// (sprinting carries far, panting further, walls muffle both). A small state machine turns that
// into roaming, investigating, chasing and searching; a "director" paces the tension so it
// neither loses you forever nor hounds you without a breather. No access to your position
// except through those senses, a short grace after losing sight, and the director's vague hints.
import { CELL, LIGHT_PERIOD, findFree, lightKind } from '../../../shared/gen.ts';
import { type Waypoint, clearLine, findPath, wallAt } from './path.ts';

export type Difficulty = 'easy' | 'normal' | 'nightmare';
export type State = 'roam' | 'investigate' | 'chase' | 'search';

export interface Tuning {
  sightLit: number; // metres it can see you in lit areas
  sightDark: number; // ... in dead zones
  detectTime: number; // seconds to fully spot you at the edge of its range (faster up close)
  hearing: number; // multiplier on how far it hears you
  speedRoam: number;
  speedSeek: number;
  speedChase: number;
  hintAfter: number; // seconds without contact before the director nudges it towards you
  wallCost: number; // A* cost of a wall cell when phasing is ready (lower = phases more eagerly)
  grace: number; // seconds it keeps tracking you after losing sight
}

export const TUNING: Record<Difficulty, Tuning> = {
  easy: { sightLit: 20, sightDark: 5, detectTime: 1.3, hearing: 0.8, speedRoam: 1.4, speedSeek: 2.4, speedChase: 4.1, hintAfter: 30, wallCost: 16, grace: 1.5 },
  normal: { sightLit: 24, sightDark: 7, detectTime: 1.0, hearing: 1, speedRoam: 1.6, speedSeek: 2.8, speedChase: 4.4, hintAfter: 25, wallCost: 10, grace: 2.0 },
  nightmare: { sightLit: 30, sightDark: 9, detectTime: 0.6, hearing: 1.3, speedRoam: 2.0, speedSeek: 3.1, speedChase: 4.6, hintAfter: 15, wallCost: 6, grace: 2.2 },
};

/** What the entity's senses get to work with each tick (the real player state). */
export interface Prey {
  x: number;
  z: number;
  vx: number;
  vz: number;
  speed: number;
  panting: boolean;
}

export interface Hooks {
  /** ask the referee for a phase; true if granted (it then lasts PHASE_S) */
  phase(): boolean;
  /** is the phase ability off cooldown? (used when planning paths) */
  phaseReady(): boolean;
  rand(): number;
}

const PHASE_S = 2;
const FOV_COS = Math.cos((110 / 2) * (Math.PI / 180));

// lit/dark lookup per light-lattice cell (lightKind is pure, so cache it)
const litCache = new Map<number, boolean>();
function litAt(x: number, z: number): boolean {
  const i0 = Math.floor(x / LIGHT_PERIOD),
    j0 = Math.floor(z / LIGHT_PERIOD);
  for (let j = j0 - 1; j <= j0 + 1; j++)
    for (let i = i0 - 1; i <= i0 + 1; i++) {
      const key = i * 100003 + j;
      let lit = litCache.get(key);
      if (lit === undefined) {
        if (litCache.size > 20000) litCache.clear();
        lit = lightKind(i, j) >= 2;
        litCache.set(key, lit);
      }
      if (lit && Math.hypot((i + 0.5) * LIGHT_PERIOD - x, (j + 0.5) * LIGHT_PERIOD - z) < 4.5) return true;
    }
  return false;
}
export function clearLightCache() {
  litCache.clear();
}

export class EntityAI {
  x = 0;
  z = 0;
  yaw = 0;
  speed = 0;
  state: State = 'roam';
  /** 0..1, fills while it can see you */
  detect = 0;
  phasingUntil = 0;
  readonly t: Tuning;

  private now = 0;
  private path: Waypoint[] = [];
  private goal: { x: number; z: number } | null = null;
  private repathAt = 0;
  private lookUntil = 0;
  private lookBase = 0;
  private lastSeen: { x: number; z: number; vx: number; vz: number; t: number } | null = null;
  private searchPts: { x: number; z: number }[] = [];
  private searchUntil = 0;
  private chaseStart = 0;
  private lastContact = 0;
  private retreatUntil = 0;
  private heardAt = -99;
  private progress = { x: 0, z: 0, t: 0 };

  readonly difficulty: Difficulty;
  private hooks: Hooks;

  // (no parameter properties: this file also runs under Node's type stripping, in the tests)
  constructor(difficulty: Difficulty, hooks: Hooks) {
    this.difficulty = difficulty;
    this.hooks = hooks;
    this.t = TUNING[difficulty];
  }

  place(x: number, z: number) {
    this.x = x;
    this.z = z;
    this.path = [];
    this.goal = null;
    this.state = 'roam';
    this.detect = 0;
    this.lastSeen = null;
    this.lastContact = this.now - this.t.hintAfter * 0.6; // its first hint comes early
    this.progress = { x, z, t: this.now };
  }

  get phasing() {
    return this.now < this.phasingUntil;
  }

  /** Can it see the prey right now? (cone, range by light, walls) */
  canSee(p: Prey): boolean {
    const dx = p.x - this.x,
      dz = p.z - this.z;
    const d = Math.hypot(dx, dz);
    const range = litAt(p.x, p.z) ? this.t.sightLit : this.t.sightDark;
    const chasing = this.state === 'chase';
    if (d > range * (chasing ? 1.25 : 1)) return false;
    // facing: -sin(yaw), -cos(yaw). Within 4 m it senses you all around.
    if (d > 4) {
      const fx = -Math.sin(this.yaw),
        fz = -Math.cos(this.yaw);
      if ((dx * fx + dz * fz) / d < (chasing ? 0 : FOV_COS)) return false;
    }
    return clearLine(this.x, this.z, p.x, p.z);
  }

  /** How far the prey is audible right now. */
  hearingRange(p: Prey, throughWalls: boolean): number {
    let r = p.speed > 3 ? 18 : p.speed > 0.6 ? 5 : 0;
    if (p.panting) r = Math.max(r, 10);
    return r * this.t.hearing * (throughWalls ? 0.55 : 1);
  }

  update(dt: number, now: number, p: Prey) {
    this.now = now;
    const d = Math.hypot(p.x - this.x, p.z - this.z);

    // ---- sight ----
    const seen = this.canSee(p);
    if (seen) {
      const range = litAt(p.x, p.z) ? this.t.sightLit : this.t.sightDark;
      // alert (just lost you, or checking a noise): it recognises you twice as fast
      const alert = this.state === 'search' || this.state === 'investigate' ? 0.5 : 1;
      this.detect = Math.min(1, this.detect + dt / (alert * this.t.detectTime * (0.25 + 0.75 * Math.min(1, d / range))));
    } else this.detect = Math.max(0, this.detect - dt * 0.4);
    if (seen && this.detect >= 1) {
      if (this.state !== 'chase') {
        this.state = 'chase';
        this.chaseStart = now;
        this.repathAt = 0;
      }
      this.lastSeen = { x: p.x, z: p.z, vx: p.vx, vz: p.vz, t: now };
      this.lastContact = now;
    } else if (seen && this.detect > 0.35 && this.state === 'roam') {
      // something moved over there: go and look
      this.investigate(p.x, p.z);
    }

    // ---- hearing ----
    if (this.state !== 'chase' && now - this.heardAt > 1.2) {
      const walls = !clearLine(this.x, this.z, p.x, p.z);
      const r = this.hearingRange(p, walls);
      if (d < r) {
        this.heardAt = now;
        this.lastContact = now;
        // a sound gives a rough direction, worse the further away
        const err = 1 + 3 * (d / r);
        const a = this.hooks.rand() * Math.PI * 2;
        this.investigate(p.x + Math.cos(a) * err * this.hooks.rand(), p.z + Math.sin(a) * err * this.hooks.rand());
      }
    }

    // ---- states ----
    let speed = this.t.speedRoam;
    switch (this.state) {
      case 'chase': {
        speed = this.t.speedChase;
        const ls = this.lastSeen!;
        if (!seen && now - ls.t > this.t.grace) {
          // lost it: search along where it was heading
          this.startSearch(ls);
          break;
        }
        // during the grace period it keeps tracking the real position (no cheap sidestep escapes)
        const tx = seen ? p.x + p.vx * 0.5 : p.x,
          tz = seen ? p.z + p.vz * 0.5 : p.z;
        if (d < 6 && clearLine(this.x, this.z, p.x, p.z, 0.2)) {
          this.path = [{ x: p.x, z: p.z, wall: false }]; // close and clear: straight at you
          this.goal = { x: p.x, z: p.z };
        } else this.plan(tx, tz, 0.3);
        break;
      }
      case 'investigate':
        speed = this.t.speedSeek;
        if (this.arrived()) this.lookAround(2.5, () => (this.state = 'roam'));
        break;
      case 'search':
        speed = this.t.speedSeek;
        if (now > this.searchUntil) {
          this.endSearch();
          break;
        }
        if (this.arrived())
          this.lookAround(1.6, () => {
            const nxt = this.searchPts.shift();
            if (nxt) this.setGoal(nxt.x, nxt.z);
            else this.endSearch();
          });
        break;
      case 'roam':
        // the longer it goes without finding you, the more it hurries
        if (now - this.lastContact > this.t.hintAfter * 0.5) speed = this.t.speedSeek;
        if (!this.goal || this.arrived()) this.lookAround(0.4 + this.hooks.rand() * 0.8, () => this.pickRoam(p));
        break;
    }

    // the director's rubber band: far away and out of your sight, it covers ground fast
    // (it never does this where you could see it move)
    if (this.state !== 'chase' && d > 25 && now > this.retreatUntil && !clearLine(p.x, p.z, this.x, this.z)) speed = Math.max(speed, this.t.speedChase * 1.15);
    this.move(dt, speed);
    this.speed = speed;

    // stuck? (wedged, or chasing a goal it can't reach): drop the path and think again
    if (Math.hypot(this.x - this.progress.x, this.z - this.progress.z) > 0.8) this.progress = { x: this.x, z: this.z, t: now };
    else if (now - this.progress.t > 3 && now > this.lookUntil) {
      this.progress.t = now;
      this.path = [];
      if (this.state !== 'chase') this.pickRoam(p);
      else this.repathAt = 0;
    }
  }

  // ---- behaviours ----
  private investigate(x: number, z: number) {
    this.state = 'investigate';
    this.lookUntil = 0;
    this.setGoal(x, z);
  }

  private startSearch(ls: { x: number; z: number; vx: number; vz: number }) {
    this.state = 'search';
    this.searchUntil = this.now + 25;
    // first, where you would be now had you kept going the same way; then around and beyond it
    const v = Math.hypot(ls.vx, ls.vz);
    const ux = v > 0.2 ? ls.vx / v : 0,
      uz = v > 0.2 ? ls.vz / v : 0;
    const lead = Math.min(10, 2 + v * 2.5);
    const [px, pz] = findFree(ls.x + ux * lead, ls.z + uz * lead, 1);
    this.searchPts = [];
    for (let i = 0; i < 4; i++) {
      const a = this.hooks.rand() * Math.PI * 2,
        r = 3 + this.hooks.rand() * 7;
      const [x, z] = findFree(px + ux * 6 + Math.cos(a) * r, pz + uz * 6 + Math.sin(a) * r, 1);
      this.searchPts.push({ x, z });
    }
    this.setGoal(px, pz);
    // a long chase earns the player a breather
    if (this.now - this.chaseStart > 8) this.retreatUntil = this.now + 20 + this.hooks.rand() * 20;
  }

  private endSearch() {
    this.state = 'roam';
    this.goal = null;
    this.path = [];
  }

  private pickRoam(p: Prey) {
    const r = this.hooks.rand;
    let x: number, z: number;
    if (this.now < this.retreatUntil) {
      // back off, away from the player
      const a = Math.atan2(this.z - p.z, this.x - p.x) + (r() - 0.5) * 1.2;
      x = this.x + Math.cos(a) * (15 + r() * 15);
      z = this.z + Math.sin(a) * (15 + r() * 15);
    } else if (this.now - this.lastContact > this.t.hintAfter) {
      // the director: a vague idea of where you are going (ahead of you, blurred by 10-16 m;
      // never the spot itself), so it tends to come at you from the front
      const a = r() * Math.PI * 2,
        e = 10 + r() * 6;
      x = p.x + p.vx * 8 + Math.cos(a) * e;
      z = p.z + p.vz * 8 + Math.sin(a) * e;
      this.lastContact = this.now - this.t.hintAfter * 0.5; // next hint comes sooner
    } else {
      // drift, loosely towards the player's part of the building
      const toward = Math.atan2(p.z - this.z, p.x - this.x);
      const a = toward + (r() - 0.5) * Math.PI * 1.4;
      const dist = 12 + r() * 18;
      x = this.x + Math.cos(a) * dist;
      z = this.z + Math.sin(a) * dist;
    }
    const [fx, fz] = findFree(x, z, 1);
    this.setGoal(fx, fz);
  }

  private lookAround(secs: number, then: () => void) {
    if (this.lookUntil === 0) {
      this.lookUntil = this.now + secs;
      this.lookBase = this.yaw;
      this.path = [];
    } else if (this.now >= this.lookUntil) {
      this.lookUntil = 0;
      then();
    }
  }

  private setGoal(x: number, z: number) {
    this.goal = { x, z };
    this.lookUntil = 0;
    this.repathAt = 0;
    this.plan(x, z, 1);
  }

  private plan(x: number, z: number, every: number) {
    if (this.now < this.repathAt && this.path.length) return;
    this.repathAt = this.now + every;
    this.goal = { x, z };
    const ready = this.hooks.phaseReady();
    this.path = findPath(this.x, this.z, x, z, { wallCost: ready ? this.t.wallCost : Infinity, half: 110 }) ?? [];
  }

  private arrived() {
    return !this.goal || (this.path.length === 0 && this.lookUntil === 0) || Math.hypot(this.goal.x - this.x, this.goal.z - this.z) < 0.6;
  }

  private move(dt: number, speed: number) {
    // looking around: stand still, sweep the head
    if (this.lookUntil > 0) {
      this.yaw = this.lookBase + Math.sin((this.lookUntil - this.now) * 2.4) * 1.1;
      return;
    }
    let budget = speed * dt;
    while (budget > 0 && this.path.length) {
      const w = this.path[0];
      if (w.wall && !this.phasing) {
        if (this.hooks.phase()) this.phasingUntil = this.now + PHASE_S;
        else {
          this.path = []; // refused (cooldown): plan again with walls solid
          this.repathAt = 0;
          break;
        }
      }
      const dx = w.x - this.x,
        dz = w.z - this.z;
      const d = Math.hypot(dx, dz);
      if (d < 1e-4) {
        this.path.shift();
        continue;
      }
      const step = Math.min(d, budget);
      // never step into a wall unless phasing
      const nx = this.x + (dx / d) * step,
        nz = this.z + (dz / d) * step;
      // (if a phase ran out mid-wall, it may keep pushing through to get out)
      const inWall = wallAt(Math.floor(this.x / CELL), Math.floor(this.z / CELL));
      if (!this.phasing && !inWall && wallAt(Math.floor(nx / CELL), Math.floor(nz / CELL))) {
        this.path = [];
        this.repathAt = 0;
        break;
      }
      this.x = nx;
      this.z = nz;
      budget -= step;
      // turn towards where it is going (its eyes follow)
      const want = Math.atan2(-dx, -dz);
      let dy = want - this.yaw;
      dy -= Math.round(dy / (Math.PI * 2)) * Math.PI * 2;
      this.yaw += Math.max(-6 * dt, Math.min(6 * dt, dy));
      if (step >= d) this.path.shift();
    }
  }
}
