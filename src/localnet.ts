// Single player: a stand-in for the relay that runs the same hunt rules (shared/hunt.ts) in the
// page, with one extra "peer" whose pose comes from the CPU entity instead of the network. Main,
// the HUD, the avatars and the audio can't tell the difference.
import { type Event, type Hunter, LAYOUT_SOLO, type RoundState, newRound, startRound, tickRound, tryPhase } from '../shared/hunt.ts';
import { type Difficulty, EntityAI } from './ai/entity.ts';
import { useSeed } from './ai/path.ts';
import { Net, type Peer } from './net.ts';

const ME = 1;
const CPU = 2;

export class LocalNet extends Net {
  readonly ai: EntityAI;
  private round: RoundState = newRound();
  private me: Hunter = { id: ME, role: 'none', pose: null, phaseReadyAt: 0 };
  private cpu: Hunter = { id: CPU, role: 'none', pose: null, phaseReadyAt: 0 };
  private cpuPeer: Peer;
  private last = performance.now();
  private prev: { x: number; z: number; t: number } | null = null;
  private vel = { x: 0, z: 0 };

  constructor(
    public difficulty: Difficulty,
    private seed: number,
  ) {
    super();
    this.id = ME;
    this.room = 'alone';
    useSeed(seed);
    this.cpuPeer = this.add({ id: CPU, name: 'it', color: '#000000' });
    this.ai = new EntityAI(difficulty, {
      phase: () => {
        const until = tryPhase(this.round, this.cpu, Date.now());
        if (until) this.onGame({ t: 'phase', id: CPU, ms: until - Date.now() });
        return until > 0;
      },
      phaseReady: () => this.round.phase === 'hunt' && Date.now() >= this.cpu.phaseReadyAt,
      rand: Math.random,
    });
  }

  override get connected() {
    return true;
  }

  override send(msg: Record<string, unknown>) {
    const now = Date.now();
    if (msg.t === 'start' && this.round.phase === 'lobby') {
      // the CPU is always the entity (startRound picks players[floor(rand * n)])
      let first = true;
      const rand = () => (first ? ((first = false), 0.999) : Math.random());
      this.dispatch(startRound(this.round, [this.me, this.cpu], this.seed, now, rand, LAYOUT_SOLO));
    } else if (msg.t === 'phase') {
      const until = tryPhase(this.round, this.me, now);
      if (until) this.onGame({ t: 'phase', id: ME, ms: until - now });
    }
  }

  override sendPose(x: number, z: number, yaw: number, pitch: number, v: number, f = 0) {
    this.me.pose = [x, z, yaw, pitch, v, f];
    const t = performance.now();
    if (this.prev && t > this.prev.t) {
      const k = Math.min(1, (t - this.prev.t) / 150);
      this.vel.x += ((x - this.prev.x) / ((t - this.prev.t) / 1000) - this.vel.x) * k;
      this.vel.z += ((z - this.prev.z) / ((t - this.prev.t) / 1000) - this.vel.z) * k;
    }
    this.prev = { x, z, t };
  }

  /** Runs the referee and the AI; called every frame by main in place of interpolation. */
  override sample() {
    const t = performance.now();
    const dt = Math.min(0.1, Math.max(0, (t - this.last) / 1000));
    this.last = t;
    const r = this.round;
    const ai = this.ai;
    if (r.phase === 'hunt' && this.me.pose && this.cpu.role === 'entity') {
      const [x, z, , , v, f] = this.me.pose;
      ai.update(dt, t / 1000, { x, z, vx: this.vel.x, vz: this.vel.z, speed: v, panting: (f & 1) === 1 });
    }
    this.cpu.pose = [ai.x, ai.z, ai.yaw, 0, ai.speed, ai.phasing ? 2 : 0];
    const p = this.cpuPeer;
    p.x = ai.x;
    p.z = ai.z;
    p.yaw = ai.yaw;
    p.v = r.phase === 'hunt' ? ai.speed : 0;
    p.f = ai.phasing ? 2 : 0;
    p.visible = r.phase === 'countdown' || r.phase === 'hunt';
    this.dispatch(tickRound(r, [this.me, this.cpu], this.seed, Date.now()));
  }

  private dispatch(events: Event[]) {
    let round = false;
    for (const e of events) {
      if (e.t === 'round') round = true;
      else if (e.t === 'teleport') {
        if (e.id === CPU) this.ai.place(e.x, e.z);
        else this.onGame({ t: 'teleport', x: e.x, z: e.z });
      } else this.onGame(e);
    }
    if (round) {
      const r = this.round;
      this.onGame({
        t: 'round',
        phase: r.phase,
        remaining: r.endsAt ? Math.max(0, (r.endsAt - Date.now()) / 1000) : 0,
        exit: r.exit,
        winner: r.winner,
        roles: [
          [ME, this.me.role],
          [CPU, this.cpu.role],
        ],
      });
    }
  }
}
