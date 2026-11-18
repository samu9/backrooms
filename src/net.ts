// Client side of the multiplayer relay (server/server.ts). Sends our pose ~15 Hz and keeps a
// short snapshot buffer per remote player, sampled ~120 ms in the past so motion is smooth.

export interface PeerInfo {
  id: number;
  name: string;
  color: string;
}

interface Snap {
  t: number;
  x: number;
  z: number;
  yaw: number;
  pitch: number;
  v: number;
  f: number;
}

export interface Peer extends PeerInfo {
  snaps: Snap[];
  /** interpolated pose, refreshed by Net.sample() */
  x: number;
  z: number;
  yaw: number;
  pitch: number;
  v: number;
  /** flags from the latest snapshot: 1 = out of breath, 2 = phasing */
  f: number;
  visible: boolean;
}

const DELAY_MS = 120;
const SEND_MS = 66;

export class Net {
  ws: WebSocket | null = null;
  id = 0;
  room = '';
  peers = new Map<number, Peer>();
  onJoin: (p: Peer) => void = () => {};
  onLeave: (p: Peer) => void = () => {};
  onClose: () => void = () => {};
  /** game messages (round, caught, escaped, phase, teleport) */
  onGame: (m: Record<string, unknown>) => void = () => {};
  private lastSend = 0;

  static url(): string {
    const q = new URLSearchParams(location.search);
    if (q.get('server')) return q.get('server')!;
    return `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
  }

  /** Join (or create) a room. Resolves with the room's seed. */
  connect(room: string, name: string, seed: number | null, timeoutMs = 4000): Promise<number> {
    this.room = room;
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(Net.url());
      this.ws = ws;
      const timer = setTimeout(() => {
        ws.close();
        reject(new Error('relay did not answer'));
      }, timeoutMs);
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', room, name, seed }));
      ws.onerror = () => {
        clearTimeout(timer);
        reject(new Error('cannot reach the relay at ' + Net.url()));
      };
      ws.onclose = () => {
        clearTimeout(timer);
        this.onClose();
      };
      ws.onmessage = (ev) => {
        let m: Record<string, unknown>;
        try {
          m = JSON.parse(ev.data);
        } catch {
          return;
        }
        switch (m.t) {
          case 'welcome': {
            clearTimeout(timer);
            this.id = m.id as number;
            for (const p of m.players as (PeerInfo & { pose: number[] | null })[]) {
              const peer = this.add(p);
              if (p.pose) this.push(peer, p.pose);
            }
            resolve(m.seed as number);
            break;
          }
          case 'full':
            clearTimeout(timer);
            reject(new Error('room is full'));
            break;
          case 'join':
            this.onJoin(this.add(m as unknown as PeerInfo));
            break;
          case 'leave': {
            const p = this.peers.get(m.id as number);
            if (p) {
              this.peers.delete(p.id);
              this.onLeave(p);
            }
            break;
          }
          case 'round':
          case 'caught':
          case 'escaped':
          case 'phase':
          case 'teleport':
            this.onGame(m);
            break;
          case 'poses':
            for (const [id, ...pose] of m.p as number[][]) {
              const p = this.peers.get(id);
              if (p) this.push(p, pose);
            }
            break;
        }
      };
    });
  }

  private add(info: PeerInfo): Peer {
    const p: Peer = { id: info.id, name: info.name, color: info.color, snaps: [], x: 0, z: 0, yaw: 0, pitch: 0, v: 0, f: 0, visible: false };
    this.peers.set(p.id, p);
    return p;
  }

  private push(p: Peer, [x, z, yaw, pitch, v, f = 0]: number[]) {
    p.snaps.push({ t: performance.now(), x, z, yaw, pitch, v, f });
    if (p.snaps.length > 30) p.snaps.shift();
  }

  get connected() {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  send(msg: Record<string, unknown>) {
    if (this.connected) this.ws!.send(JSON.stringify(msg));
  }

  sendPose(x: number, z: number, yaw: number, pitch: number, v: number, f = 0) {
    const now = performance.now();
    if (!this.connected || now - this.lastSend < SEND_MS) return;
    this.lastSend = now;
    this.ws!.send(JSON.stringify({ t: 'pose', x: +x.toFixed(3), z: +z.toFixed(3), yaw: +yaw.toFixed(3), pitch: +pitch.toFixed(3), v: +v.toFixed(2), f }));
  }

  /** Interpolate every peer at (now - DELAY). */
  sample() {
    const t = performance.now() - DELAY_MS;
    for (const p of this.peers.values()) {
      const s = p.snaps;
      if (!s.length) continue;
      p.visible = true;
      let a = s[0],
        b = s[0];
      for (let i = s.length - 1; i >= 0; i--)
        if (s[i].t <= t) {
          a = s[i];
          b = s[Math.min(i + 1, s.length - 1)];
          break;
        }
      const k = b.t > a.t ? Math.min(1, Math.max(0, (t - a.t) / (b.t - a.t))) : 1;
      p.x = a.x + (b.x - a.x) * k;
      p.z = a.z + (b.z - a.z) * k;
      let dy = b.yaw - a.yaw;
      dy -= Math.round(dy / (Math.PI * 2)) * Math.PI * 2;
      p.yaw = a.yaw + dy * k;
      p.pitch = a.pitch + (b.pitch - a.pitch) * k;
      p.v = a.v + (b.v - a.v) * k;
      p.f = b.f;
    }
  }
}
