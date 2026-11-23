// Single player (web/src/localnet.ts): a stand-in for the relay that runs the same hunt rules in
// process, with one extra "peer" whose pose comes from the CPU entity instead of the network. It
// speaks the relay's JSON into a NetState, so the client hunt, HUD, avatars and audio can't tell
// the difference.
using System.Text.Json;
using System.Text.Json.Nodes;
using Backrooms.Core.AI;

namespace Backrooms.Core.Hunt;

public sealed class LocalRelay : IEntityHooks
{
    public const int Me = 1;
    public const int Cpu = 2;

    public readonly EntityAI Ai;
    public readonly Difficulty Difficulty;
    readonly Gen gen;
    readonly NetState state;
    readonly Random rng;
    readonly RoundState round = new();
    readonly Hunter me = new(Me), cpu = new(Cpu);
    readonly Peer cpuPeer;
    double now, last = double.NaN;
    (double X, double Z, double T)? prev;
    double velX, velZ;

    public LocalRelay(Difficulty difficulty, Gen gen, NetState state, Random? rng = null)
    {
        Difficulty = difficulty;
        this.gen = gen;
        this.state = state;
        this.rng = rng ?? new Random();
        state.Begin("alone");
        state.SetId(Me);
        cpuPeer = state.AddPeer(Cpu, "it", "#000000");
        Ai = new EntityAI(difficulty, this, new PathFinder(gen));
    }

    public RoundState Round => round;

    // ---- the AI's view of the referee ----
    bool IEntityHooks.Phase()
    {
        double until = HuntRules.TryPhase(round, cpu, now);
        if (until > 0) Emit(new JsonObject { ["t"] = "phase", ["id"] = Cpu, ["ms"] = until - now });
        return until > 0;
    }

    bool IEntityHooks.PhaseReady() => round.Phase == Phase.Hunt && now >= cpu.PhaseReadyAt;
    double IEntityHooks.Rand() => rng.NextDouble();

    /// <summary>What the client would send to the relay ({t:'start'} / {t:'phase'}).</summary>
    public void Send(string json, double nowMs)
    {
        now = nowMs;
        string? t;
        try
        {
            t = JsonDocument.Parse(json).RootElement.GetProperty("t").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }
        if (t == "start" && round.Phase == Phase.Lobby)
        {
            // the CPU is always the entity (StartRound picks players[floor(rand * n)])
            bool first = true;
            double Rand()
            {
                if (!first) return rng.NextDouble();
                first = false;
                return 0.999;
            }
            Dispatch(HuntRules.StartRound(gen, round, [me, cpu], nowMs, Rand, HuntRules.Solo));
        }
        else if (t == "phase")
        {
            double until = HuntRules.TryPhase(round, me, nowMs);
            if (until > 0) Emit(new JsonObject { ["t"] = "phase", ["id"] = Me, ["ms"] = until - nowMs });
        }
    }

    public void Pose(double x, double z, double yaw, double pitch, double v, int f, double nowMs)
    {
        me.Pose = [x, z, yaw, pitch, v, f];
        if (prev is { } p && nowMs > p.T)
        {
            double k = Math.Min(1, (nowMs - p.T) / 150);
            velX += ((x - p.X) / ((nowMs - p.T) / 1000) - velX) * k;
            velZ += ((z - p.Z) / ((nowMs - p.T) / 1000) - velZ) * k;
        }
        prev = (x, z, nowMs);
    }

    /// <summary>Runs the referee and the AI; called every frame in place of interpolation.</summary>
    public void Tick(double nowMs)
    {
        now = nowMs;
        double dt = double.IsNaN(last) ? 0 : Math.Clamp((nowMs - last) / 1000, 0, 0.1);
        last = nowMs;
        if (round.Phase == Phase.Hunt && me.Pose is { } mp && cpu.Role == Role.Entity)
            Ai.Update(dt, nowMs / 1000, new Prey(mp[0], mp[1], velX, velZ, mp[4], ((int)mp[5] & 1) == 1));
        cpu.Pose = [Ai.X, Ai.Z, Ai.Yaw, 0, Ai.Speed, Ai.Phasing ? 2 : 0];
        cpuPeer.X = Ai.X;
        cpuPeer.Z = Ai.Z;
        cpuPeer.Yaw = Ai.Yaw;
        cpuPeer.V = round.Phase == Phase.Hunt ? Ai.Speed : 0;
        cpuPeer.F = Ai.Phasing ? 2 : 0;
        cpuPeer.Visible = round.Phase is Phase.Countdown or Phase.Hunt;
        Dispatch(HuntRules.TickRound(gen, round, [me, cpu], nowMs));
    }

    void Emit(JsonObject m) => state.Handle(m.ToJsonString(), now);

    void Dispatch(List<HuntEvent> events)
    {
        bool changed = false;
        foreach (var e in events)
            switch (e)
            {
                case HuntEvent.Round: changed = true; break;
                case HuntEvent.Teleport tp when tp.Id == Cpu: Ai.Place(tp.X, tp.Z); break;
                case HuntEvent.Teleport tp: Emit(new JsonObject { ["t"] = "teleport", ["x"] = tp.X, ["z"] = tp.Z }); break;
                case HuntEvent.Caught c: Emit(new JsonObject { ["t"] = "caught", ["id"] = c.Id, ["by"] = c.By }); break;
                case HuntEvent.Escaped x: Emit(new JsonObject { ["t"] = "escaped", ["id"] = x.Id }); break;
            }
        if (changed) Emit(RoundMessage(round, [me, cpu], now));
    }

    /// <summary>The relay's {t:'round'} message.</summary>
    public static JsonObject RoundMessage(RoundState r, IEnumerable<Hunter> players, double nowMs) => new()
    {
        ["t"] = "round",
        ["phase"] = Names.Of(r.Phase),
        ["remaining"] = r.EndsAt > 0 ? Math.Max(0, (r.EndsAt - nowMs) / 1000) : 0,
        ["exit"] = r.Exit is { } x ? new JsonObject { ["x"] = x.X, ["z"] = x.Z } : null,
        ["winner"] = r.Winner,
        ["roles"] = new JsonArray(players.Select(p => (JsonNode)new JsonArray(p.Id, Names.Of(p.Role))).ToArray()),
    };
}
