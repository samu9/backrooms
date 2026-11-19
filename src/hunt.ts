// Client side of "The Hunt" (rules and referee live in server/hunt.ts). Holds the round state the
// relay broadcasts, the local-only mechanics (stamina, phasing), and the HUD.
import type { Net } from './net';

export type Role = 'none' | 'wanderer' | 'entity' | 'escaped';
export type Phase = 'lobby' | 'countdown' | 'hunt' | 'end';

export const ENTITY_SPEED = 3.7; // m/s, no sprint: faster than a walk, slower than a run
const STAMINA_MAX = 6; // seconds of full sprint
const STAMINA_REGEN = 0.6; // per second, after a short pause
const STAMINA_DELAY = 1.0;
const PHASE_MS = 2000;
const PHASE_COOLDOWN_MS = 30000;

const $ = (id: string) => document.getElementById(id)!;

export class Hunt {
  phase: Phase = 'lobby';
  endsAt = 0; // performance.now() timestamp
  roles = new Map<number, Role>();
  exit: { x: number; z: number } | null = null;
  winner: string | null = null;
  stamina = STAMINA_MAX;
  exhausted = false;
  private sinceSprint = 0;
  phasingUntil = 0;
  phaseReadyAt = 0;
  /** remote entities currently phasing: id -> until */
  remotePhase = new Map<number, number>();
  /** single player against the CPU (only changes some wording) */
  solo = false;
  /** seconds of "you were caught" glitch left */
  caughtFx = 0;

  onTeleport: (x: number, z: number) => void = () => {};
  onToast: (msg: string) => void = () => {};
  onPhaseEnd: () => void = () => {};

  constructor(private net: Net | null) {
    if (!net) return;
    net.onGame = (m) => this.message(m);
  }

  get myId() {
    return this.net?.id ?? 0;
  }
  get myRole(): Role {
    return this.roles.get(this.myId) ?? 'none';
  }
  roleOf(id: number): Role {
    return this.roles.get(id) ?? 'none';
  }
  get active() {
    return this.phase === 'countdown' || this.phase === 'hunt';
  }
  get isEntity() {
    return this.active && this.myRole === 'entity';
  }
  get isWanderer() {
    return this.active && this.myRole === 'wanderer';
  }
  /** the entity waits in the dark while the wanderers scatter */
  get frozen() {
    return this.phase === 'countdown' && this.myRole === 'entity';
  }
  get phasing() {
    return performance.now() < this.phasingUntil;
  }

  private message(m: Record<string, unknown>) {
    const now = performance.now();
    switch (m.t) {
      case 'round': {
        const prev = this.phase;
        const prevRole = this.myRole;
        this.phase = m.phase as Phase;
        this.endsAt = now + (m.remaining as number) * 1000;
        this.exit = (m.exit as { x: number; z: number }) ?? null;
        this.winner = (m.winner as string) ?? null;
        this.roles = new Map(m.roles as [number, Role][]);
        if (this.phase === 'countdown' && prev !== 'countdown') {
          this.stamina = STAMINA_MAX;
          this.exhausted = false;
          this.phaseReadyAt = now + 10000;
          this.banner(this.myRole === 'entity' ? 'YOU ARE THE ENTITY' : 'RUN.', this.myRole === 'entity' ? 'wait in the dark. then find them.' : 'something is coming. find the exit, or outlast it.', 5);
        }
        if (this.phase === 'hunt' && prev === 'countdown' && this.myRole === 'entity') this.banner('HUNT', 'E — walk through walls', 3);
        if (this.phase === 'end' && prev !== 'end') this.endScreen();
        if (this.phase === 'lobby' && prev === 'end') this.hideBanner();
        if (prevRole === 'wanderer' && this.myRole === 'escaped') this.banner('YOU GOT OUT', 'for now.', 6);
        break;
      }
      case 'caught': {
        const id = m.id as number;
        if (id === this.myId) {
          this.caughtFx = 2.2;
          this.phaseReadyAt = now + PHASE_COOLDOWN_MS;
          this.banner('FOUND', this.solo ? 'it was right behind you.' : 'you are one of them now. hunt the others.', 5);
        } else this.onToast(`${this.name(id)} WAS FOUND`);
        break;
      }
      case 'escaped':
        if (m.id !== this.myId) this.onToast(`${this.name(m.id as number)} FOUND THE EXIT`);
        break;
      case 'phase': {
        const until = now + (m.ms as number);
        if (m.id === this.myId) {
          this.phasingUntil = until;
          this.phaseReadyAt = until + PHASE_COOLDOWN_MS;
        } else this.remotePhase.set(m.id as number, until);
        break;
      }
      case 'teleport':
        this.onTeleport(m.x as number, m.z as number);
        break;
    }
  }

  private name(id: number) {
    return (this.net?.peers.get(id)?.name ?? 'someone').toUpperCase();
  }

  /** Lobby: ask the relay to start a round. */
  requestStart() {
    if (!this.net) return this.onToast('THE HUNT NEEDS A ROOM (MENU → MULTIPLAYER)');
    if (this.phase !== 'lobby') return;
    if (this.net.peers.size < 1) return this.onToast('THE HUNT NEEDS AT LEAST 2 PLAYERS');
    this.net.send({ t: 'start' });
  }

  requestPhase() {
    if (!this.isEntity || this.phase !== 'hunt') return;
    if (performance.now() < this.phaseReadyAt) return this.onToast('NOT YET');
    this.net?.send({ t: 'phase' });
  }

  /** Can the local player sprint right now? Drains/regenerates stamina as a side effect. */
  sprintGate(wantRun: boolean, moving: number, dt: number): boolean {
    if (!this.isWanderer) {
      this.stamina = STAMINA_MAX;
      this.exhausted = false;
      return wantRun;
    }
    const can = wantRun && !this.exhausted;
    if (can && moving > 2) {
      this.stamina = Math.max(0, this.stamina - dt);
      this.sinceSprint = 0;
      if (this.stamina === 0) {
        this.exhausted = true;
        this.onToast('OUT OF BREATH');
      }
    } else {
      this.sinceSprint += dt;
      if (this.sinceSprint > STAMINA_DELAY) this.stamina = Math.min(STAMINA_MAX, this.stamina + STAMINA_REGEN * dt);
      if (this.exhausted && this.stamina > STAMINA_MAX * 0.35) this.exhausted = false;
    }
    return can;
  }

  /** pose flags for the relay: 1 = out of breath (audible), 2 = phasing */
  get flags() {
    return (this.isWanderer && this.stamina < STAMINA_MAX * 0.3 ? 1 : 0) | (this.phasing ? 2 : 0);
  }

  private bannerT = 0;
  banner(title: string, sub: string, secs: number) {
    $('hbTitle').textContent = title;
    $('hbSub').textContent = sub;
    $('hbanner').classList.remove('hidden');
    this.bannerT = secs;
  }
  hideBanner() {
    $('hbanner').classList.add('hidden');
    this.bannerT = 0;
  }

  private endScreen() {
    const w = this.winner;
    if (w === 'entity') this.banner(this.solo ? 'IT FOUND YOU' : 'NO ONE GOT OUT', this.solo ? 'in a moment, R to try again' : 'the entity found everyone', 9);
    else if (w === 'wanderers') {
      const out = [...this.roles.entries()].filter(([, r]) => r === 'escaped' || r === 'wanderer').map(([id]) => (id === this.myId ? 'YOU' : this.name(id)));
      this.banner('SOMEONE GOT OUT', out.join(', ') + (out.length === 1 ? ' survived' : ' survived'), 9);
    } else this.banner('THE HUNT IS OVER', 'the entity left', 6);
  }

  /** Per-frame: timers and HUD. */
  update(dt: number) {
    if (this.bannerT > 0 && (this.bannerT -= dt) <= 0) this.hideBanner();
    this.caughtFx = Math.max(0, this.caughtFx - dt);
    const now = performance.now();
    if (this.phasingUntil && now >= this.phasingUntil) {
      this.phasingUntil = 0;
      this.onPhaseEnd();
    }
    const left = Math.max(0, (this.endsAt - now) / 1000);
    const mmss = `${Math.floor(left / 60)}:${String(Math.floor(left % 60)).padStart(2, '0')}`;
    const free = [...this.roles.values()].filter((r) => r === 'wanderer').length;
    const hunters = [...this.roles.values()].filter((r) => r === 'entity').length;
    let line = '';
    if (!this.net) line = '';
    else if (this.phase === 'lobby') line = this.net.peers.size >= 1 ? 'PRESS R TO START THE HUNT' : 'THE HUNT: WAITING FOR A SECOND PLAYER';
    else if (this.phase === 'countdown') line = `${this.myRole === 'entity' ? 'RELEASED IN' : 'IT WAKES IN'} ${Math.ceil(left)}`;
    else if (this.phase === 'hunt') line = `${mmss} · ${free} FREE · ${hunters} HUNTING`;
    else line = 'NEXT ROUND SOON';
    $('hline').textContent = line;
    $('hfrozen').classList.toggle('hidden', !this.frozen);
    if (this.frozen) $('hfrozenN').textContent = String(Math.ceil(left));
    // stamina bar for wanderers, phase charge for entities
    const bar = $('hbar');
    if (this.isWanderer) {
      bar.classList.remove('hidden');
      $('hbarLabel').textContent = this.exhausted ? 'BREATH' : 'STAMINA';
      ($('hbarFill') as HTMLElement).style.width = `${(this.stamina / STAMINA_MAX) * 100}%`;
      bar.classList.toggle('bad', this.exhausted);
    } else if (this.isEntity && this.phase === 'hunt') {
      bar.classList.remove('hidden');
      const ready = Math.max(0, this.phaseReadyAt - now);
      $('hbarLabel').textContent = this.phasing ? 'PHASING' : ready > 0 ? 'PHASE (E)' : 'PHASE READY (E)';
      ($('hbarFill') as HTMLElement).style.width = `${this.phasing ? ((this.phasingUntil - now) / PHASE_MS) * 100 : (1 - ready / PHASE_COOLDOWN_MS) * 100}%`;
      bar.classList.toggle('bad', false);
    } else bar.classList.add('hidden');
  }
}
