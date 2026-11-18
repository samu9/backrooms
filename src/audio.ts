// Everything you hear is synthesised: the mains hum of a thousand ballasts, buzzing panels you can
// locate with your ears (HRTF), and things that happen somewhere else in the building.
import type { LightInfo } from './chunk';

type V3 = { x: number; y: number; z: number };

export class Soundscape {
  ctx: AudioContext;
  private master: GainNode;
  private dry: GainNode;
  private wet: GainNode;
  private noise: AudioBuffer;
  private brown: AudioBuffer;
  private buzzers: { panner: PannerNode; gain: GainNode; light: LightInfo | null; crackle: number }[] = [];
  private nextEvent = 0;
  private stepPhase = 0;
  private muted = false;

  constructor() {
    this.ctx = new AudioContext();
    const c = this.ctx;
    this.master = c.createGain();
    this.master.gain.value = 0;
    const comp = c.createDynamicsCompressor();
    comp.threshold.value = -18;
    comp.ratio.value = 3;
    this.master.connect(comp).connect(c.destination);
    this.dry = c.createGain();
    this.dry.connect(this.master);
    // a large, dead, carpeted space: short-ish dense reverb with absorbed highs
    const verb = c.createConvolver();
    verb.buffer = this.impulse(2.6, 2.8);
    this.wet = c.createGain();
    this.wet.gain.value = 0.9;
    this.wet.connect(verb).connect(this.master);

    this.noise = c.createBuffer(1, c.sampleRate * 2, c.sampleRate);
    const nd = this.noise.getChannelData(0);
    for (let i = 0; i < nd.length; i++) nd[i] = Math.random() * 2 - 1;
    this.brown = c.createBuffer(1, c.sampleRate * 4, c.sampleRate);
    const bd = this.brown.getChannelData(0);
    let last = 0;
    for (let i = 0; i < bd.length; i++) {
      last = (last + 0.02 * (Math.random() * 2 - 1)) / 1.02;
      bd[i] = last * 3.5;
    }
    this.hum();
    this.roomTone();
    for (let i = 0; i < 4; i++) this.buzzers.push(this.makeBuzzer());
  }

  private impulse(sec: number, decay: number) {
    const c = this.ctx;
    const len = Math.floor(c.sampleRate * sec);
    const b = c.createBuffer(2, len, c.sampleRate);
    for (let ch = 0; ch < 2; ch++) {
      const d = b.getChannelData(ch);
      let lp = 0;
      for (let i = 0; i < len; i++) {
        const t = i / len;
        lp += 0.25 * (Math.random() * 2 - 1 - lp);
        d[i] = lp * Math.pow(1 - t, decay) * (i < 200 ? i / 200 : 1);
      }
    }
    return b;
  }

  private hum() {
    const c = this.ctx;
    const g = c.createGain();
    g.gain.value = 0.05;
    const lp = c.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 900;
    g.connect(lp).connect(this.dry);
    // 60 Hz mains, its 120 Hz ballast hum and odd harmonics, slightly detuned against each other
    const parts: [number, OscillatorType, number][] = [
      [60, 'sine', 0.5],
      [120, 'sawtooth', 0.22],
      [120.4, 'sine', 0.35],
      [180, 'sine', 0.08],
      [240, 'triangle', 0.06],
      [360, 'sine', 0.03],
    ];
    for (const [f, type, a] of parts) {
      const o = c.createOscillator();
      o.type = type;
      o.frequency.value = f;
      const og = c.createGain();
      og.gain.value = a;
      // slow beating
      const lfo = c.createOscillator();
      lfo.frequency.value = 0.05 + Math.random() * 0.2;
      const lg = c.createGain();
      lg.gain.value = a * 0.3;
      lfo.connect(lg).connect(og.gain);
      lfo.start();
      o.connect(og).connect(g);
      o.start();
    }
    // the high whine of ballasts
    const n = c.createBufferSource();
    n.buffer = this.noise;
    n.loop = true;
    const bp = c.createBiquadFilter();
    bp.type = 'bandpass';
    bp.frequency.value = 7800;
    bp.Q.value = 12;
    const ng = c.createGain();
    ng.gain.value = 0.05;
    n.connect(bp).connect(ng).connect(g);
    n.start();
  }

  private roomTone() {
    const c = this.ctx;
    const s = c.createBufferSource();
    s.buffer = this.brown;
    s.loop = true;
    const lp = c.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 180;
    const g = c.createGain();
    g.gain.value = 0.16;
    s.connect(lp).connect(g).connect(this.dry);
    s.start();
  }

  private makeBuzzer() {
    const c = this.ctx;
    const panner = c.createPanner();
    panner.panningModel = 'HRTF';
    panner.distanceModel = 'inverse';
    panner.refDistance = 1.2;
    panner.rolloffFactor = 1.4;
    panner.maxDistance = 40;
    const gain = c.createGain();
    gain.gain.value = 0;
    const o = c.createOscillator();
    o.type = 'sawtooth';
    o.frequency.value = 120 + Math.random() * 0.6;
    const hp = c.createBiquadFilter();
    hp.type = 'bandpass';
    hp.frequency.value = 2400 + Math.random() * 1600;
    hp.Q.value = 3;
    const o2 = c.createOscillator();
    o2.frequency.value = 240.3;
    const g2 = c.createGain();
    g2.gain.value = 0.4;
    o.connect(hp).connect(gain);
    o2.connect(g2).connect(gain);
    o.start();
    o2.start();
    gain.connect(panner);
    panner.connect(this.dry);
    const send = c.createGain();
    send.gain.value = 0.15;
    panner.connect(send).connect(this.wet);
    return { panner, gain, light: null as LightInfo | null, crackle: 0 };
  }

  setMuted(m: boolean) {
    this.muted = m;
    this.master.gain.setTargetAtTime(m ? 0 : 0.9, this.ctx.currentTime, 0.3);
  }
  get isMuted() {
    return this.muted;
  }

  start() {
    this.ctx.resume();
    this.setMuted(false);
    this.nextEvent = this.ctx.currentTime + 6;
  }

  /** Call every frame with the listener pose and the lights currently loaded. */
  update(pos: V3, fwd: V3, lights: Iterable<LightInfo>, moving: number, dt: number) {
    const c = this.ctx;
    const t = c.currentTime;
    const L = c.listener;
    if (L.positionX) {
      L.positionX.setTargetAtTime(pos.x, t, 0.02);
      L.positionY.setTargetAtTime(pos.y, t, 0.02);
      L.positionZ.setTargetAtTime(pos.z, t, 0.02);
      L.forwardX.setTargetAtTime(fwd.x, t, 0.02);
      L.forwardY.setTargetAtTime(fwd.y, t, 0.02);
      L.forwardZ.setTargetAtTime(fwd.z, t, 0.02);
      L.upX.value = 0;
      L.upY.value = 1;
      L.upZ.value = 0;
    }
    // nearest lit panels get a positional buzz (flickering ones preferred: they crackle)
    const near: [number, LightInfo][] = [];
    for (const l of lights) {
      if (l.kind < 2) continue;
      const dx = l.x - pos.x;
      const dz = l.z - pos.z;
      const d = dx * dx + dz * dz - (l.kind === 3 ? 30 : 0);
      if (d < 200) near.push([d, l]);
    }
    near.sort((a, b) => a[0] - b[0]);
    this.buzzers.forEach((b, i) => {
      const l = near[i]?.[1] ?? null;
      if (l !== b.light) {
        b.light = l;
        if (l) {
          b.panner.positionX.value = l.x;
          b.panner.positionY.value = l.y - 0.05;
          b.panner.positionZ.value = l.z;
        }
      }
      let target = l ? 0.05 : 0;
      if (l && l.kind === 3) {
        b.crackle -= dt;
        if (b.crackle < 0) {
          b.crackle = 0.04 + Math.random() * 0.25;
          target = Math.random() < 0.5 ? 0.0 : 0.12;
          if (Math.random() < 0.08) this.zap(l);
        } else return;
      }
      b.gain.gain.setTargetAtTime(target, t, 0.03);
    });

    // own footsteps on damp carpet
    if (moving > 0.05) {
      this.stepPhase += dt * (1.6 + moving * 1.2);
      if (this.stepPhase > 1) {
        this.stepPhase -= 1;
        this.thump({ x: pos.x, y: 0, z: pos.z }, 0.1 + moving * 0.05, 0.12, 380, false);
      }
    }

    if (t > this.nextEvent) {
      this.nextEvent = t + 7 + Math.random() * 16;
      this.event(pos, fwd);
    }
  }

  private src(buf: AudioBuffer, at: number) {
    const s = this.ctx.createBufferSource();
    s.buffer = buf;
    s.start(at, Math.random() * (buf.duration - 0.5));
    return s;
  }

  private spot(p: V3, wet = 0.6) {
    const c = this.ctx;
    const pn = c.createPanner();
    pn.panningModel = 'HRTF';
    pn.distanceModel = 'inverse';
    pn.refDistance = 2;
    pn.rolloffFactor = 1;
    pn.positionX.value = p.x;
    pn.positionY.value = p.y;
    pn.positionZ.value = p.z;
    pn.connect(this.dry);
    const s = c.createGain();
    s.gain.value = wet;
    pn.connect(s).connect(this.wet);
    return pn;
  }

  /** Muffled impact: noise burst + low sine. */
  /** Someone else's footstep at (x, z). */
  footstep(x: number, z: number) {
    this.thump({ x, y: 0.1, z }, 0.32, 0.16, 420, true);
  }

  private thump(p: V3, amp: number, dur: number, cutoff: number, spatial = true, at = this.ctx.currentTime) {
    const c = this.ctx;
    const out = spatial ? this.spot(p, 0.9) : this.dry;
    const n = this.src(this.noise, at);
    const lp = c.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = cutoff;
    const g = c.createGain();
    g.gain.setValueAtTime(0, at);
    g.gain.linearRampToValueAtTime(amp, at + 0.008);
    g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
    n.connect(lp).connect(g).connect(out);
    n.stop(at + dur + 0.05);
    const o = c.createOscillator();
    o.frequency.setValueAtTime(90, at);
    o.frequency.exponentialRampToValueAtTime(45, at + dur);
    const og = c.createGain();
    og.gain.setValueAtTime(amp * 0.8, at);
    og.gain.exponentialRampToValueAtTime(0.0001, at + dur);
    o.connect(og).connect(out);
    o.start(at);
    o.stop(at + dur + 0.05);
  }

  private around(pos: V3, rmin: number, rmax: number): V3 {
    const a = Math.random() * Math.PI * 2;
    const r = rmin + Math.random() * (rmax - rmin);
    return { x: pos.x + Math.cos(a) * r, y: 1.2, z: pos.z + Math.sin(a) * r };
  }

  private zap(l: LightInfo) {
    const c = this.ctx;
    const at = c.currentTime;
    const out = this.spot({ x: l.x, y: l.y, z: l.z }, 0.3);
    const n = this.src(this.noise, at);
    const hp = c.createBiquadFilter();
    hp.type = 'highpass';
    hp.frequency.value = 2500;
    const g = c.createGain();
    g.gain.setValueAtTime(0.25, at);
    g.gain.exponentialRampToValueAtTime(0.001, at + 0.09);
    n.connect(hp).connect(g).connect(out);
    n.stop(at + 0.12);
  }

  /** Something, somewhere else. */
  private event(pos: V3, fwd: V3) {
    const c = this.ctx;
    const at = c.currentTime + 0.05;
    const r = Math.random();
    if (r < 0.3) {
      // footsteps that are not yours, walking past at a distance
      const p = this.around(pos, 12, 26);
      const a = Math.random() * Math.PI * 2;
      const n = 6 + Math.floor(Math.random() * 10);
      const fast = Math.random() < 0.25;
      for (let i = 0; i < n; i++) {
        const q = { x: p.x + Math.cos(a) * i * 0.7, y: 0.1, z: p.z + Math.sin(a) * i * 0.7 };
        this.thump(q, 0.5, 0.18, 420, true, at + i * (fast ? 0.32 : 0.58) + Math.random() * 0.04);
      }
    } else if (r < 0.48) {
      // knocking inside a wall
      const p = this.around(pos, 5, 16);
      const n = 2 + Math.floor(Math.random() * 4);
      for (let i = 0; i < n; i++) this.knock(p, at + i * (0.22 + Math.random() * 0.15));
    } else if (r < 0.6) {
      // a heavy door slamming far away
      const p = this.around(pos, 25, 40);
      this.thump(p, 1.4, 1.2, 160, true, at);
    } else if (r < 0.75) {
      this.drone(at);
    } else if (r < 0.87) {
      this.creak(this.around(pos, 8, 30), at);
    } else {
      // breathing / whisper right behind you
      const p = { x: pos.x - fwd.x * 1.6 + (Math.random() - 0.5), y: 1.6, z: pos.z - fwd.z * 1.6 + (Math.random() - 0.5) };
      this.whisper(p, at);
    }
  }

  private knock(p: V3, at: number) {
    const c = this.ctx;
    const out = this.spot(p, 0.7);
    const o = c.createOscillator();
    o.frequency.setValueAtTime(190, at);
    o.frequency.exponentialRampToValueAtTime(120, at + 0.08);
    const g = c.createGain();
    g.gain.setValueAtTime(0.0001, at);
    g.gain.exponentialRampToValueAtTime(0.7, at + 0.004);
    g.gain.exponentialRampToValueAtTime(0.0001, at + 0.16);
    o.connect(g).connect(out);
    o.start(at);
    o.stop(at + 0.2);
    const n = this.src(this.noise, at);
    const bp = c.createBiquadFilter();
    bp.type = 'bandpass';
    bp.frequency.value = 900;
    bp.Q.value = 2;
    const ng = c.createGain();
    ng.gain.setValueAtTime(0.4, at);
    ng.gain.exponentialRampToValueAtTime(0.0001, at + 0.05);
    n.connect(bp).connect(ng).connect(out);
    n.stop(at + 0.08);
  }

  private drone(at: number) {
    const c = this.ctx;
    const g = c.createGain();
    const dur = 7 + Math.random() * 6;
    g.gain.setValueAtTime(0.0001, at);
    g.gain.exponentialRampToValueAtTime(0.22, at + dur * 0.5);
    g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
    const lp = c.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.setValueAtTime(120, at);
    lp.frequency.linearRampToValueAtTime(600, at + dur * 0.5);
    lp.frequency.linearRampToValueAtTime(100, at + dur);
    lp.Q.value = 6;
    g.connect(this.wet);
    lp.connect(g);
    const base = [41.2, 43.65, 46.25, 55][Math.floor(Math.random() * 4)];
    for (const m of [1, 1.006, 1.498, 2.01]) {
      const o = c.createOscillator();
      o.type = 'sawtooth';
      o.frequency.value = base * m;
      o.connect(lp);
      o.start(at);
      o.stop(at + dur + 0.1);
    }
  }

  private creak(p: V3, at: number) {
    const c = this.ctx;
    const out = this.spot(p, 1);
    const o = c.createOscillator();
    o.type = 'sawtooth';
    const dur = 0.8 + Math.random() * 1.5;
    const f0 = 60 + Math.random() * 60;
    o.frequency.setValueAtTime(f0, at);
    o.frequency.linearRampToValueAtTime(f0 * (0.6 + Math.random() * 0.8), at + dur);
    const bp = c.createBiquadFilter();
    bp.type = 'bandpass';
    bp.frequency.value = 500 + Math.random() * 700;
    bp.Q.value = 9;
    const g = c.createGain();
    g.gain.setValueAtTime(0.0001, at);
    g.gain.exponentialRampToValueAtTime(0.25, at + 0.1);
    g.gain.setValueAtTime(0.25, at + dur - 0.2);
    g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
    // stick-slip grain
    const am = c.createOscillator();
    am.frequency.value = 18 + Math.random() * 20;
    const amg = c.createGain();
    amg.gain.value = 0.2;
    am.connect(amg).connect(g.gain);
    o.connect(bp).connect(g).connect(out);
    o.start(at);
    am.start(at);
    o.stop(at + dur + 0.1);
    am.stop(at + dur + 0.1);
  }

  private whisper(p: V3, at: number) {
    const c = this.ctx;
    const out = this.spot(p, 0.4);
    const n = this.src(this.noise, at);
    const dur = 1.6 + Math.random();
    const g = c.createGain();
    g.gain.setValueAtTime(0.0001, at);
    g.gain.exponentialRampToValueAtTime(0.09, at + dur * 0.35);
    g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
    // two moving formants make it sound almost like breath through teeth
    for (const [f0, f1] of [
      [700, 1100],
      [1900, 2600],
    ]) {
      const bp = c.createBiquadFilter();
      bp.type = 'bandpass';
      bp.Q.value = 8;
      bp.frequency.setValueAtTime(f0, at);
      bp.frequency.linearRampToValueAtTime(f1, at + dur * 0.6);
      bp.frequency.linearRampToValueAtTime(f0 * 0.9, at + dur);
      n.connect(bp).connect(g);
    }
    g.connect(out);
    n.stop(at + dur + 0.1);
  }
}
