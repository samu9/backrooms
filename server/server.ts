// Multiplayer relay. The world is a pure function of the seed, so the server never touches level
// data: it only owns rooms (a seed + who is in them) and relays player poses.
//
//   node server/server.ts            # ws on :5191, also serves dist/ if it exists
//   PORT=8080 node server/server.ts
//
// Protocol (JSON text frames):
//   c→s  {t:'hello', room, name, seed?}         first message; seed only used if the room is new
//   s→c  {t:'welcome', id, seed, players:[{id,name,color}]}
//   s→c  {t:'join', id, name, color} / {t:'leave', id}
//   c→s  {t:'pose', x, z, yaw, pitch, v}          ~15 Hz
//   s→c  {t:'poses', p:[[id, x, z, yaw, pitch, v, flags], ...]}   15 Hz batch per room
//
// The Hunt (rules in shared/hunt.ts):
//   c→s  {t:'start'}                              lobby only, needs 2+ players
//   c→s  {t:'phase'}                              entity asks to walk through walls
//   s→c  {t:'round', phase, remaining, exit, winner, roles:[[id, role]]}
//   s→c  {t:'caught', id, by} / {t:'escaped', id} / {t:'phase', id, ms} / {t:'teleport', x, z}
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize, resolve } from 'node:path';
import { WebSocketServer, type WebSocket } from 'ws';
import { type Event, type Hunter, type RoundState, lateRole, newRound, startRound, tickRound, tryPhase } from '../shared/hunt.ts';

const PORT = Number(process.env.PORT ?? 5191);
const DIST = resolve(import.meta.dirname, '..', 'dist');
const TICK_MS = 66;
const MAX_ROOM = 16;
const COLORS = ['#e8c547', '#4fb3d9', '#d9534f', '#7dc95e', '#c27bd9', '#f0f0e8', '#ff8c42', '#5ee0c0'];

interface Player extends Hunter {
  name: string;
  color: string;
  ws: WebSocket;
  dirty: boolean;
}
interface Room {
  key: string;
  seed: number;
  players: Map<number, Player>;
  round: RoundState;
}

const rooms = new Map<string, Room>();
let nextId = 1;

const MIME: Record<string, string> = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' };
const http = createServer((req, res) => {
  // static hosting of the built client, so one process is enough in production
  const url = new URL(req.url ?? '/', 'http://x');
  let file = normalize(join(DIST, decodeURIComponent(url.pathname)));
  if (!file.startsWith(DIST) || !existsSync(DIST)) {
    res.writeHead(404).end('not found (run npm run build, or use the vite dev server)');
    return;
  }
  if (existsSync(file) && statSync(file).isDirectory()) file = join(file, 'index.html');
  if (!existsSync(file)) {
    res.writeHead(404).end('not found');
    return;
  }
  res.writeHead(200, { 'content-type': MIME[extname(file)] ?? 'application/octet-stream' }).end(readFileSync(file));
});

const wss = new WebSocketServer({ server: http, path: '/ws', maxPayload: 4096 });

const send = (ws: WebSocket, msg: unknown) => ws.readyState === ws.OPEN && ws.send(JSON.stringify(msg));
const broadcast = (room: Room, msg: unknown, except?: number) => {
  const s = JSON.stringify(msg);
  for (const p of room.players.values()) if (p.id !== except && p.ws.readyState === p.ws.OPEN) p.ws.send(s);
};
const clean = (s: unknown, max: number) => String(s ?? '').replace(/[^\p{L}\p{N} _.-]/gu, '').trim().slice(0, max);
const finite = (n: unknown) => typeof n === 'number' && Number.isFinite(n);

function roundMsg(room: Room) {
  const r = room.round;
  return {
    t: 'round',
    phase: r.phase,
    remaining: r.endsAt ? Math.max(0, (r.endsAt - Date.now()) / 1000) : 0,
    exit: r.exit,
    winner: r.winner,
    roles: [...room.players.values()].map((p) => [p.id, p.role]),
  };
}

function dispatch(room: Room, events: Event[]) {
  let round = false;
  for (const e of events) {
    if (e.t === 'round') round = true;
    else if (e.t === 'teleport') {
      const p = room.players.get(e.id);
      if (p) send(p.ws, { t: 'teleport', x: e.x, z: e.z });
    } else {
      broadcast(room, e);
      const p = room.players.get(e.id);
      console.log(`[${room.key}] ${p?.name ?? e.id} ${e.t}`);
    }
  }
  if (round) {
    broadcast(room, roundMsg(room));
    const r = room.round;
    if (r.phase === 'end' || r.phase === 'countdown') console.log(`[${room.key}] round ${r.phase}${r.winner ? ' winner=' + r.winner : ''}`);
  }
}

wss.on('connection', (ws) => {
  let room: Room | null = null;
  let me: Player | null = null;
  const alive = { v: true };
  ws.on('pong', () => (alive.v = true));
  (ws as unknown as { alive: typeof alive }).alive = alive;

  ws.on('message', (data) => {
    let m: Record<string, unknown>;
    try {
      m = JSON.parse(String(data));
    } catch {
      return;
    }
    if (m.t === 'hello' && !me) {
      const key = clean(m.room, 32).toLowerCase() || 'lobby';
      room = rooms.get(key) ?? null;
      if (!room) {
        const seed = finite(m.seed) ? (m.seed as number) | 0 : Math.floor(Math.random() * 1e6);
        room = { key, seed, players: new Map(), round: newRound() };
        rooms.set(key, room);
      }
      if (room.players.size >= MAX_ROOM) {
        send(ws, { t: 'full' });
        ws.close();
        return;
      }
      const used = new Set([...room.players.values()].map((p) => p.color));
      me = {
        id: nextId++,
        name: clean(m.name, 20) || 'wanderer',
        color: COLORS.find((c) => !used.has(c)) ?? COLORS[nextId % COLORS.length],
        ws,
        pose: null,
        dirty: false,
        role: lateRole(room.round),
        phaseReadyAt: 0,
      };
      send(ws, { t: 'welcome', id: me.id, seed: room.seed, players: [...room.players.values()].map(({ id, name, color, pose }) => ({ id, name, color, pose })) });
      broadcast(room, { t: 'join', id: me.id, name: me.name, color: me.color });
      room.players.set(me.id, me);
      broadcast(room, roundMsg(room));
      console.log(`[${key}] +${me.name}#${me.id} (${room.players.size} in room, seed ${room.seed})`);
      ws.on('close', () => {
        if (!room || !me) return;
        room.players.delete(me.id);
        broadcast(room, { t: 'leave', id: me.id });
        broadcast(room, roundMsg(room));
        console.log(`[${key}] -${me.name}#${me.id} (${room.players.size} left)`);
        if (room.players.size === 0) rooms.delete(key);
      });
      return;
    }
    if (m.t === 'pose' && me && [m.x, m.z, m.yaw, m.pitch, m.v].every(finite)) {
      me.pose = [m.x as number, m.z as number, m.yaw as number, m.pitch as number, m.v as number, finite(m.f) ? (m.f as number) & 3 : 0];
      me.dirty = true;
      return;
    }
    if (!me || !room) return;
    if (m.t === 'start' && room.round.phase === 'lobby' && room.players.size >= 2) {
      dispatch(room, startRound(room.round, [...room.players.values()], room.seed, Date.now()));
      return;
    }
    if (m.t === 'phase') {
      const until = tryPhase(room.round, me, Date.now());
      if (until) broadcast(room, { t: 'phase', id: me.id, ms: until - Date.now() });
    }
  });
});

// one batched pose message per room per tick
setInterval(() => {
  for (const room of rooms.values()) {
    const p: (number | number[])[][] = [];
    for (const pl of room.players.values()) {
      if (!pl.dirty || !pl.pose) continue;
      pl.dirty = false;
      p.push([pl.id, ...pl.pose.map((v) => Math.round(v * 1000) / 1000)]);
    }
    if (p.length) broadcast(room, { t: 'poses', p });
    dispatch(room, tickRound(room.round, [...room.players.values()], room.seed, Date.now()));
  }
}, TICK_MS);

// drop dead connections
setInterval(() => {
  for (const ws of wss.clients) {
    const a = (ws as unknown as { alive: { v: boolean } }).alive;
    if (!a.v) ws.terminate();
    else {
      a.v = false;
      ws.ping();
    }
  }
}, 15000);

http.listen(PORT, () => console.log(`backrooms relay on :${PORT} (ws path /ws)`));
