# Backrooms

Procedural, infinite Backrooms. One world, several clients: see the layout table in `README.md`
and the port plan in `docs/godot-port-plan.html`.

## Layout

- `shared/gen.ts` is the **source of truth** for the world. It's a pure function of the seed and the
  cell: no state, no engine calls. The web client, the relay (`shared/hunt.ts` → `server/`) and the
  tests all import it.
- `web/` is the three.js client. Vite runs with `--config web/vite.config.ts` and builds to the
  root `dist/`, which `server/server.ts` serves. Keep it there: the server must not change for
  layout reasons.
- `godot/` (planned) is the C# desktop client: `Core` (no Godot dependency) + `Game` (Godot).

## Generation parity

The relay decides catches with the TypeScript `solid()`, so every client must generate exactly
the same world, cell for cell.

- `hash3` is also mirrored in GLSL (`web/src/shaders.ts`). Change both together.
- Once `godot/Core` exists, any change to `shared/gen.ts` is mirrored in `godot/Core/Gen.cs` in the
  same commit, then run `npm run gen:fixtures` and the C# parity tests.

## Commands

The npm scripts need Node 22.6 or newer: they run `.ts` directly. With nvm, prefix
`PATH=~/.nvm/versions/node/v24.22.2/bin:$PATH` if the shell default is older.

- `npm run typecheck`, `npm test` (rules + AI), `npm run sim` (AI balance, slow)
- `npm run build`, then `PORT=5190 npm run server` serves the build and the relay on one port
- `node web/scripts/smoke.mjs`, `node web/scripts/solotest.mjs`: Playwright against :5190
- `npm run doc` rebuilds `docs/layout-algorithm.html` from `shared/gen.ts`
