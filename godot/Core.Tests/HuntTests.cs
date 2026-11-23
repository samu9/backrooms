// Ports of tests/hunt.test.ts (round rules) and tests/ai.test.ts (CPU entity), plus the client
// hunt and the single-player relay.
using System.Text.Json;
using Backrooms.Core.AI;
using Backrooms.Core.Hunt;
using Xunit;

namespace Backrooms.Core.Tests;

public class HuntRulesTests
{
    const int Seed = 42;
    static readonly Gen G = new(Seed);
    static readonly (double X, double Z) S = G.FindFree(0, 0, 3);
    static Hunter Mk(int id) => new(id) { Pose = [S.X, S.Z, 0, 0, 0, 0] };

    [Fact]
    public void ARoundFromStartToLobby()
    {
        var ps = new[] { Mk(1), Mk(2), Mk(3) };
        var r = new RoundState();
        double t = 1_000_000;
        var ev = HuntRules.StartRound(G, r, ps, t, () => 0.0);
        Assert.Equal(Phase.Countdown, r.Phase);
        Assert.Single(ps, p => p.Role == Role.Entity);
        Assert.Equal(2, ps.Count(p => p.Role == Role.Wanderer));
        var tp = ev.OfType<HuntEvent.Teleport>().Single();
        Assert.True(Math.Sqrt((tp.X - S.X) * (tp.X - S.X) + (tp.Z - S.Z) * (tp.Z - S.Z)) > 20, "entity starts far away");
        var exit = r.Exit!.Value;
        Assert.True(Math.Sqrt((exit.X - S.X) * (exit.X - S.X) + (exit.Z - S.Z) * (exit.Z - S.Z)) > 50, "exit is far away");
        // countdown -> hunt
        t += 10_001;
        HuntRules.TickRound(G, r, ps, t);
        Assert.Equal(Phase.Hunt, r.Phase);
        // phasing: cooldown respected
        var ent = ps.First(p => p.Role == Role.Entity);
        Assert.True(HuntRules.TryPhase(r, ent, t) > 0);
        Assert.Equal(0, HuntRules.TryPhase(r, ent, t + 1000));
        Assert.Equal(0, HuntRules.TryPhase(r, ps.First(p => p.Role == Role.Wanderer), t));
        // catch: entity right next to a wanderer in the open -> wanderer becomes an entity
        var wanderers = ps.Where(p => p.Role == Role.Wanderer).ToList();
        var (w, other) = (wanderers[0], wanderers[1]);
        ent.Pose = [S.X + HuntRules.CatchDist * 0.5, S.Z, 0, 0, 0, 0];
        w.Pose = [S.X, S.Z, 0, 0, 0, 0];
        other.Pose = [S.X + 40, S.Z + 40, 0, 0, 0, 0];
        var ev2 = HuntRules.TickRound(G, r, ps, t + 100);
        Assert.Contains(ev2, e => e is HuntEvent.Caught c && c.Id == w.Id);
        Assert.Equal(Role.Entity, w.Role);
        // last wanderer reaches the exit -> round ends, wanderers win
        var last = ps.First(p => p.Role == Role.Wanderer);
        last.Pose = [exit.X, exit.Z, 0, 0, 0, 0];
        HuntRules.TickRound(G, r, ps, t + 200);
        Assert.Equal(Role.Escaped, last.Role);
        Assert.Equal(Phase.End, r.Phase);
        Assert.Equal("wanderers", r.Winner);
        // end -> lobby
        HuntRules.TickRound(G, r, ps, t + 200 + 10_001);
        Assert.Equal(Phase.Lobby, r.Phase);
        Assert.All(ps, p => Assert.Equal(Role.None, p.Role));
    }

    [Fact]
    public void NoCatchThroughAWall()
    {
        (int gx, int gz)? spot = null;
        for (int gx = 0; gx < 400 && spot == null; gx++)
            for (int gz = 0; gz < 400 && spot == null; gz++)
                if (G.Solid(gx, gz) && !G.Solid(gx - 1, gz) && !G.Solid(gx + 1, gz) && !G.Solid(gx - 1, gz - 1) && !G.Solid(gx + 1, gz + 1)) spot = (gx, gz);
        Assert.NotNull(spot);
        var (x, z) = spot!.Value;
        var ps = new[] { Mk(1), Mk(2) };
        var r = new RoundState();
        HuntRules.StartRound(G, r, ps, 0, () => 0);
        HuntRules.TickRound(G, r, ps, 10_001);
        var ent = ps.First(p => p.Role == Role.Entity);
        var w = ps.First(p => p.Role == Role.Wanderer);
        ent.Pose = [(x - 0.5) * 0.5, (z + 0.5) * 0.5, 0, 0, 0, 0];
        w.Pose = [(x + 1.5) * 0.5, (z + 0.5) * 0.5, 0, 0, 0, 0];
        HuntRules.TickRound(G, r, ps, 10_100);
        Assert.Equal(Role.Wanderer, w.Role);
    }

    [Fact]
    public void EveryoneCaughtOrTimeRunsOut()
    {
        var ps = new[] { Mk(1), Mk(2) };
        var r = new RoundState();
        HuntRules.StartRound(G, r, ps, 0, () => 0);
        HuntRules.TickRound(G, r, ps, 10_001);
        var ent = ps.First(p => p.Role == Role.Entity);
        ent.Pose = [S.X + 0.3, S.Z, 0, 0, 0, 0];
        HuntRules.TickRound(G, r, ps, 10_100);
        Assert.Equal(Phase.End, r.Phase);
        Assert.Equal("entity", r.Winner);

        var ps2 = new[] { Mk(1), Mk(2) };
        var r2 = new RoundState();
        HuntRules.StartRound(G, r2, ps2, 0, () => 0);
        HuntRules.TickRound(G, r2, ps2, 10_001);
        ps2.First(p => p.Role == Role.Entity).Pose = [S.X + 30, S.Z + 30, 0, 0, 0, 0];
        HuntRules.TickRound(G, r2, ps2, 10_001 + HuntRules.HuntS * 1000 + 1);
        Assert.Equal("wanderers", r2.Winner);
        Assert.Equal(Role.Entity, HuntRules.LateRole(new RoundState { Phase = Phase.Hunt }));
    }
}

public class EntityAITests
{
    const int Seed = 42;
    static readonly Gen G = new(Seed);
    static double M(int g) => (g + 0.5) * Gen.Cell;

    sealed class Hooks : IEntityHooks
    {
        public bool Phase() => false;
        public bool PhaseReady() => false;
        public double Rand() => 0.5;
    }

    /// <summary>A long, one-cell-thick wall running along z with open floor on both sides of its middle.</summary>
    static (int gx, int gz) LongThinWall()
    {
        for (int gx = -200; gx < 200; gx++)
            for (int gz = -200; gz < 200; gz++)
            {
                bool ok = true;
                for (int k = -8; k <= 8 && ok; k++)
                    ok = G.Solid(gx, gz + k) && !G.Solid(gx - 1, gz + k) && !G.Solid(gx + 1, gz + k) && !G.Solid(gx - 2, gz + k) && !G.Solid(gx + 2, gz + k);
                if (ok) return (gx, gz);
            }
        throw new InvalidOperationException("no long thin wall");
    }

    [Fact]
    public void PathsGoAroundOrThroughWalls()
    {
        var (gx, gz) = LongThinWall();
        var pf = new PathFinder(G);
        double ax = M(gx - 2), az = M(gz), bx = M(gx + 2), bz = M(gz);
        var around = pf.FindPath(ax, az, bx, bz);
        Assert.NotEmpty(around);
        Assert.All(around, w => Assert.False(w.Wall));
        var last = around[^1];
        Assert.True(Math.Sqrt((last.X - bx) * (last.X - bx) + (last.Z - bz) * (last.Z - bz)) < 0.5, "reaches the goal");
        double px = ax, pz = az;
        foreach (var w in around)
        {
            Assert.True(pf.ClearLine(px, pz, w.X, w.Z), "smoothed segment stays clear");
            (px, pz) = (w.X, w.Z);
        }
        var through = pf.FindPath(ax, az, bx, bz, wallCost: 2);
        Assert.Contains(through, w => w.Wall);
        Assert.True(through.Count <= 6);
    }

    [Fact]
    public void Senses()
    {
        var (gx, gz) = LongThinWall();
        double ax = M(gx - 2), az = M(gz), bx = M(gx + 2), bz = M(gz);
        var ai = new EntityAI(Difficulty.Normal, new Hooks(), new PathFinder(G));
        ai.Place(ax, az);
        static Prey P(double x, double z, double speed = 0) => new(x, z, 0, 0, speed, false);
        ai.Yaw = -Math.PI / 2; // facing +x, towards B
        Assert.False(ai.CanSee(P(bx, bz)), "cannot see through a wall");
        var open = P(ax, az + 3);
        ai.Yaw = Math.Atan2(-(open.X - ax), -(open.Z - az));
        Assert.True(ai.CanSee(open), "sees in the open, in front");
        ai.Yaw += Math.PI;
        Assert.False(ai.CanSee(P(ax, az + 6)), "not behind its back beyond 4 m");
        Assert.True(ai.CanSee(P(ax, az + 2)), "senses all around within 4 m");
        Assert.Equal(18, ai.HearingRange(P(0, 0, 5.6), false));
        Assert.Equal(5, ai.HearingRange(P(0, 0, 1.7), false));
        Assert.Equal(0, ai.HearingRange(P(0, 0, 0), false));
        Assert.True(ai.HearingRange(P(0, 0, 5.6), true) < 18);
        Assert.True(ai.HearingRange(new Prey(0, 0, 0, 0, 0, true), false) >= 10);
    }

    [Fact]
    public void ItHearsASprintBehindAWall()
    {
        var (gx, gz) = LongThinWall();
        var ai = new EntityAI(Difficulty.Normal, new Hooks(), new PathFinder(G));
        ai.Place(M(gx - 2), M(gz));
        ai.Update(0.1, 1, new Prey(M(gx + 2), M(gz), 0, 5.6, 5.6, false));
        Assert.Equal(AiState.Investigate, ai.State);
    }
}

public class SoloTests
{
    [Fact]
    public void ASoloRoundRunsThroughTheClient()
    {
        var gen = new Gen(7);
        var state = new NetState();
        var relay = new LocalRelay(Difficulty.Normal, gen, state, new Random(1));
        double now = 0;
        var hunt = new HuntClient(state, m => relay.Send(m, now)) { Solo = true };
        (double, double)? teleported = null;
        hunt.Teleport += (x, z) => teleported = (x, z);
        var (x, z) = gen.FindFree(0, 0, 2);
        relay.Pose(x, z, 0, 0, 0, 0, now);
        hunt.RequestStart();
        Assert.Equal(Phase.Countdown, hunt.Phase);
        Assert.Equal(Role.Wanderer, hunt.MyRole);
        Assert.Equal(Role.Entity, hunt.RoleOf(LocalRelay.Cpu));
        Assert.NotNull(hunt.Exit);
        Assert.Null(teleported); // the CPU is the one placed away
        Assert.Equal("RUN.", hunt.BannerTitle);
        // run the countdown out, then the hunt: standing still at the spawn, it comes for us
        for (int i = 0; i < 300 * 10 + 200 && hunt.Phase != Phase.End; i++)
        {
            now += 100;
            hunt.Update(0.1, now);
            relay.Pose(x, z, 0, 0, 0, 0, now);
            relay.Tick(now);
        }
        Assert.Equal(Phase.End, hunt.Phase);
        Assert.Equal("entity", hunt.Winner);
        Assert.Equal("IT FOUND YOU", hunt.BannerTitle);
    }

    [Fact]
    public void StaminaRunsOutAndComesBack()
    {
        var state = new NetState();
        var hunt = new HuntClient(state, null);
        state.SetId(1);
        hunt.Message(JsonDocument.Parse("""{"t":"round","phase":"hunt","remaining":300,"exit":null,"winner":null,"roles":[[1,"wanderer"],[2,"entity"]]}""").RootElement, 0);
        string? toast = null;
        hunt.Toast += t => toast = t;
        int frames = 0;
        while (hunt.SprintGate(true, 5.6, 0.1)) frames++;
        Assert.InRange(frames, 59, 61); // 6 s of sprint
        Assert.True(hunt.Exhausted);
        Assert.Equal("OUT OF BREATH", toast);
        Assert.Equal(1, hunt.Flags & 1);
        for (int i = 0; i < 50; i++) hunt.SprintGate(false, 0, 0.1);
        Assert.False(hunt.Exhausted);
    }
}
