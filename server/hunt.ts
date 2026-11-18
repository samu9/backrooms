// "The Hunt": one player is the Entity, the others are Wanderers. Caught wanderers become entities
// too. Wanderers win if anyone reaches the exit door or is still free when the timer runs out.
//
// The relay is the referee: it assigns roles, places the exit, and decides catches and escapes
// from the positions players report, checking line of sight with the same solid() the game uses.
import { CELL, findFree, setSeed, solid } from '../src/gen.ts';

export type Role = 'none' | 'wanderer' | 'entity' | 'escaped';
export type Phase = 'lobby' | 'countdown' | 'hunt' | 'end';
export type Winner = 'entity' | 'wanderers' | 'aborted' | null;

export const COUNTDOWN_S = 10;
export const HUNT_S = Number(process.env.HUNT_SECONDS ?? 300);
export const END_S = 10;
export const CATCH_DIST = 1.1;
export const EXIT_DIST = 1.5;
export const PHASE_MS = 2000;
export const PHASE_COOLDOWN_MS = 30000;

export interface Hunter {
  id: number;
  role: Role;
  pose: [number, number, number, number, number, number] | null; // x, z, yaw, pitch, speed, flags
  phaseReadyAt: number;
}

export interface RoundState {
  phase: Phase;
  endsAt: number; // ms epoch of the current phase's end (0 = no timer)
  exit: { x: number; z: number } | null;
  winner: Winner;
  entityStart: { x: number; z: number } | null;
}

export type Event =
  | { t: 'round' }
  | { t: 'caught'; id: number; by: number }
  | { t: 'escaped'; id: number }
  | { t: 'teleport'; id: number; x: number; z: number };

export function newRound(): RoundState {
  return { phase: 'lobby', endsAt: 0, exit: null, winner: null, entityStart: null };
}

/** True if nothing solid lies between the two points (sampled every 0.2 m). */
function lineOfSight(ax: number, az: number, bx: number, bz: number): boolean {
  const d = Math.hypot(bx - ax, bz - az);
  const n = Math.max(1, Math.ceil(d / 0.2));
  for (let i = 1; i < n; i++) {
    const t = i / n;
    if (solid(Math.floor((ax + (bx - ax) * t) / CELL), Math.floor((az + (bz - az) * t) / CELL))) return false;
  }
  return true;
}

/** Start a round: pick the entity, place it and the exit away from everyone else. */
export function startRound(r: RoundState, players: Hunter[], seed: number, now: number, rand = Math.random): Event[] {
  setSeed(seed);
  const placed = players.filter((p) => p.pose);
  const cx = placed.reduce((s, p) => s + p.pose![0], 0) / Math.max(1, placed.length);
  const cz = placed.reduce((s, p) => s + p.pose![1], 0) / Math.max(1, placed.length);
  const entity = players[Math.floor(rand() * players.length)];
  for (const p of players) {
    p.role = p === entity ? 'entity' : 'wanderer';
    p.phaseReadyAt = now + COUNTDOWN_S * 1000; // no phasing during the countdown
  }
  // the entity starts out of sight, 30-40 m away; the exit 70-100 m away in another direction
  const a = rand() * Math.PI * 2;
  const [ex, ez] = findFree(cx + Math.cos(a) * (30 + rand() * 10), cz + Math.sin(a) * (30 + rand() * 10), 2);
  const b = a + Math.PI * (0.6 + rand() * 0.8);
  const [xx, xz] = findFree(cx + Math.cos(b) * (70 + rand() * 30), cz + Math.sin(b) * (70 + rand() * 30), 2);
  r.phase = 'countdown';
  r.endsAt = now + COUNTDOWN_S * 1000;
  r.exit = { x: xx, z: xz };
  r.entityStart = { x: ex, z: ez };
  r.winner = null;
  return [{ t: 'round' }, { t: 'teleport', id: entity.id, x: ex, z: ez }];
}

function finish(r: RoundState, winner: Winner, now: number): Event[] {
  r.phase = 'end';
  r.winner = winner;
  r.endsAt = now + END_S * 1000;
  return [{ t: 'round' }];
}

/** Advance the round; returns what the room needs to be told. */
export function tickRound(r: RoundState, players: Hunter[], seed: number, now: number): Event[] {
  const ev: Event[] = [];
  if (r.phase === 'lobby') return ev;
  if (r.phase === 'end') {
    if (now >= r.endsAt) {
      Object.assign(r, newRound());
      for (const p of players) p.role = 'none';
      ev.push({ t: 'round' });
    }
    return ev;
  }
  const entities = players.filter((p) => p.role === 'entity');
  const free = players.filter((p) => p.role === 'wanderer');
  if (entities.length === 0 || players.length < 2) return finish(r, 'aborted', now);
  if (r.phase === 'countdown') {
    if (now >= r.endsAt) {
      r.phase = 'hunt';
      r.endsAt = now + HUNT_S * 1000;
      ev.push({ t: 'round' });
    }
    return ev;
  }
  // hunt
  setSeed(seed);
  for (const w of free) {
    if (!w.pose) continue;
    const [wx, wz] = w.pose;
    if (r.exit && Math.hypot(wx - r.exit.x, wz - r.exit.z) < EXIT_DIST) {
      w.role = 'escaped';
      ev.push({ t: 'escaped', id: w.id }, { t: 'round' });
      continue;
    }
    for (const e of entities) {
      if (!e.pose) continue;
      const [ex, ez] = e.pose;
      if (Math.hypot(wx - ex, wz - ez) < CATCH_DIST && lineOfSight(ex, ez, wx, wz)) {
        w.role = 'entity'; // the hunt snowballs
        w.phaseReadyAt = now + PHASE_COOLDOWN_MS;
        ev.push({ t: 'caught', id: w.id, by: e.id }, { t: 'round' });
        break;
      }
    }
  }
  const stillFree = players.filter((p) => p.role === 'wanderer').length;
  const escaped = players.filter((p) => p.role === 'escaped').length;
  if (stillFree === 0) return [...ev, ...finish(r, escaped > 0 ? 'wanderers' : 'entity', now)];
  if (now >= r.endsAt) return [...ev, ...finish(r, 'wanderers', now)];
  return ev;
}

/** Entity asks to phase through walls. Returns the phase end time, or 0 if refused. */
export function tryPhase(r: RoundState, p: Hunter, now: number): number {
  if (r.phase !== 'hunt' || p.role !== 'entity' || now < p.phaseReadyAt) return 0;
  p.phaseReadyAt = now + PHASE_MS + PHASE_COOLDOWN_MS;
  return now + PHASE_MS;
}

/** Role for someone arriving mid-round: they join the hunters. */
export function lateRole(r: RoundState): Role {
  return r.phase === 'countdown' || r.phase === 'hunt' ? 'entity' : 'none';
}

