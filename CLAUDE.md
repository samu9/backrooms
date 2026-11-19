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
- `godot/` is the C# desktop client, ported milestone by milestone (M0–M10 in the plan):
  - `Core/`: generation, and later chunk data, rules, AI, protocol. **No `using Godot`**, so it
    builds and tests with plain `dotnet`.
  - `Core.Tests/`: xUnit, including the generator parity tests.
  - `Game/`: the Godot 4.7 .NET project (`project.godot`, `Backrooms.csproj`, `Backrooms.sln`
    next to it, as the editor expects). Commit the `*.cs.uid` files Godot creates; `.godot/`,
    `bin/` and `obj/` are ignored.

## Generation parity

The relay decides catches with the TypeScript `solid()`, so every client must generate exactly
the same world, cell for cell.

- Any change to `shared/gen.ts` is mirrored in `godot/Core/Gen.cs` in the same commit, then
  `npm run gen:fixtures && npm run godot:test`. Commit the regenerated fixture.
- `hash3` is also mirrored in GLSL (`web/src/shaders.ts`), and will be in the Godot shaders. Change
  them together.
- The porting rules are at the top of `Gen.cs`: wrapping int/uint for `Math.imul` and `>>>`,
  doubles in the same operation order, `Math.Floor` (never casts) for negatives, `Gen.JsRound`
  for `Math.round`.

## Commands

The npm scripts need Node 22.6 or newer: they run `.ts` directly. With nvm, prefix
`PATH=~/.nvm/versions/node/v24.22.2/bin:$PATH` if the shell default is older. The .NET 8 SDK is
installed per user in `~/.dotnet`: prefix `PATH=~/.dotnet:$PATH` (and `DOTNET_ROOT=~/.dotnet`
when running Godot).

- `npm run typecheck`, `npm test` (rules + AI), `npm run sim` (AI balance, slow)
- `npm run gen:fixtures`, `npm run godot:test` (C# generator vs the fixtures)
- `dotnet build godot/Game/Backrooms.sln` builds Core, the tests and the Godot project
- `godot --headless --path godot/Game --quit-after 30 -- --seed=123` runs the game without a window
- `npm run build`, then `PORT=5190 npm run server` serves the build and the relay on one port
- `node web/scripts/smoke.mjs`, `node web/scripts/solotest.mjs`: Playwright against :5190
- `npm run doc` rebuilds `docs/layout-algorithm.html` from `shared/gen.ts`
