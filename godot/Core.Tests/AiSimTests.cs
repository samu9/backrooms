// Balance simulation, ported from tests/ai.sim.ts: the CPU entity against a scripted wanderer, many
// rounds per difficulty, with the real rules, AI and layout. The bot walks to the exit, sprints
// (same stamina rules) when it sees the entity coming, and pants when out of breath.
//   SIM_ROUNDS=30 dotnet test godot/Core.Tests --filter Category=Sim --logger "console;verbosity=detailed"
// Excluded from npm run godot:test (slow).
using Backrooms.Core.AI;
using Backrooms.Core.Hunt;
using Xunit;
using Xunit.Abstractions;

namespace Backrooms.Core.Tests;

public class AiSimTests(ITestOutputHelper output)
{
    const double Dt = 0.1, Walk = 1.7, Run = 5.6;

    sealed class SimHooks(Func<bool> phase, Func<bool> ready, Func<double> rand) : IEntityHooks
    {
        public bool Phase() => phase();
        public bool PhaseReady() => ready();
        public double Rand() => rand();
    }

    record Result(string Outcome, double T, int Phases, int Chases);

    static Result Round(Difficulty diff, int seed)
    {
        var gen = new Gen(seed);
        var pf = new PathFinder(gen);
        double rngState = seed * 9301.0 + 49297;
        double Rand() => ((rngState = (rngState * 233280 + 49297) % 2147483647) / 2147483647 + 1) % 1;
        var (sx, sz) = gen.FindFree(0, 0, 2);
        var me = new Hunter(1) { Pose = [sx, sz, 0, 0, 0, 0] };
        var cpu = new Hunter(2) { Pose = [sx, sz, 0, 0, 0, 0] };
        var r = new RoundState();
        double nowMs = 0;
        int phases = 0;
        var ai = new EntityAI(diff, new SimHooks(
            () =>
            {
                bool ok = HuntRules.TryPhase(r, cpu, nowMs) > 0;
                if (ok) phases++;
                return ok;
            },
            () => r.Phase == Phase.Hunt && nowMs >= cpu.PhaseReadyAt,
            Rand), new PathFinder(gen));
        bool first = true;
        var ev = HuntRules.StartRound(gen, r, [me, cpu], nowMs, () =>
        {
            if (!first) return Rand();
            first = false;
            return 0.999;
        }, HuntRules.Solo);
        var tp = ev.OfType<HuntEvent.Teleport>().Single();
        ai.Place(tp.X, tp.Z);
        double x = sx, z = sz, stamina = 6, sinceSprint = 0, repath = 0, tHunt = 0;
        bool exhausted = false, wasChasing = false;
        int chases = 0;
        var path = new List<Waypoint>();
        var exit = r.Exit!.Value;
        for (int step = 0; step < (HuntRules.HuntS + 15) / Dt; step++)
        {
            nowMs += Dt * 1000;
            double t = nowMs / 1000;
            // --- bot ---
            double de = Math.Sqrt((ai.X - x) * (ai.X - x) + (ai.Z - z) * (ai.Z - z));
            bool threat = r.Phase == Phase.Hunt && de < 14 && pf.ClearLine(x, z, ai.X, ai.Z);
            bool wantRun = threat && !exhausted;
            if (t > repath || path.Count == 0)
            {
                repath = t + 1;
                path = pf.FindPath(x, z, exit.X, exit.Z);
            }
            double speed = r.Phase is Phase.Countdown or Phase.Hunt ? (wantRun ? Run : Walk) : 0;
            if (wantRun)
            {
                stamina = Math.Max(0, stamina - Dt);
                sinceSprint = 0;
                if (stamina == 0) exhausted = true;
            }
            else
            {
                sinceSprint += Dt;
                if (sinceSprint > 1) stamina = Math.Min(6, stamina + 0.6 * Dt);
                if (exhausted && stamina > 2.1) exhausted = false;
            }
            double budget = speed * Dt, vx = 0, vz = 0;
            while (budget > 0 && path.Count > 0)
            {
                var w = path[0];
                double dx = w.X - x, dz = w.Z - z, d = Math.Sqrt(dx * dx + dz * dz);
                if (d < 1e-4)
                {
                    path.RemoveAt(0);
                    continue;
                }
                double s = Math.Min(d, budget);
                double nx = x + dx / d * s, nz = z + dz / d * s;
                if (pf.WallAt((int)Math.Floor(nx / Gen.Cell), (int)Math.Floor(nz / Gen.Cell)))
                {
                    path = [];
                    break;
                }
                vx = dx / d * speed;
                vz = dz / d * speed;
                (x, z) = (nx, nz);
                budget -= s;
                if (s >= d) path.RemoveAt(0);
            }
            if (path.Count == 0) speed = 0;
            me.Pose = [x, z, 0, 0, speed, stamina < 1.8 ? 1 : 0];
            // --- entity ---
            if (r.Phase == Phase.Hunt)
            {
                tHunt += Dt;
                ai.Update(Dt, t, new Prey(x, z, vx, vz, speed, stamina < 1.8));
                if (ai.State == AiState.Chase && !wasChasing) chases++;
                wasChasing = ai.State == AiState.Chase;
            }
            cpu.Pose = [ai.X, ai.Z, ai.Yaw, 0, ai.Speed, ai.Phasing ? 2 : 0];
            HuntRules.TickRound(gen, r, [me, cpu], nowMs);
            if (r.Phase == Phase.End || me.Role != Role.Wanderer)
                return new Result(me.Role == Role.Escaped ? "escaped" : me.Role == Role.Entity ? "caught" : "survived", tHunt, phases, chases);
        }
        return new Result("survived", tHunt, phases, chases);
    }

    [Fact, Trait("Category", "Sim")]
    public void Balance()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("SIM_ROUNDS"), out var n) ? n : 10;
        output.WriteLine($"{rounds} rounds per difficulty, {HuntRules.HuntS}s hunts\n");
        output.WriteLine("difficulty  caught  escaped  survived  avg time  chases/round  phases/round");
        var caught = new Dictionary<Difficulty, int>();
        foreach (var diff in new[] { Difficulty.Easy, Difficulty.Normal, Difficulty.Nightmare })
        {
            var res = Enumerable.Range(0, rounds).AsParallel().AsOrdered().Select(i => Round(diff, 1000 + i * 7919)).ToList();
            int N(string o) => res.Count(r => r.Outcome == o);
            caught[diff] = N("caught");
            output.WriteLine($"{diff.ToString().ToLowerInvariant(),-10}  {N("caught"),6}  {N("escaped"),7}  {N("survived"),8}  {res.Average(r => r.T),7:0.0}s  {res.Average(r => r.Chases),12:0.0}  {res.Average(r => r.Phases),12:0.0}");
        }
        // harder catches at least as often (a loose check: the rounds are few)
        Assert.True(caught[Difficulty.Easy] <= caught[Difficulty.Nightmare], "nightmare should catch at least as often as easy");
    }
}
