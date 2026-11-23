// "The Hunt": one player is the Entity, the others are Wanderers. Caught wanderers become entities
// too. Wanderers win if anyone reaches the exit door or is still free when the timer runs out.
//
// Port of shared/hunt.ts, used by the desktop client for single player (the relay keeps running
// the TypeScript one for multiplayer). The referee assigns roles, places the exit, and decides
// catches and escapes from the positions players report, with line of sight through solid().
namespace Backrooms.Core.Hunt;

public enum Role { None, Wanderer, Entity, Escaped }
public enum Phase { Lobby, Countdown, Hunt, End }

public static class Names
{
    public static string Of(Role r) => r.ToString().ToLowerInvariant();
    public static string Of(Phase p) => p.ToString().ToLowerInvariant();
    public static Role ParseRole(string? s) => Enum.TryParse<Role>(s, true, out var r) ? r : Role.None;
    public static Phase ParsePhase(string? s) => Enum.TryParse<Phase>(s, true, out var p) ? p : Phase.Lobby;
}

public sealed class Hunter(int id)
{
    public int Id { get; } = id;
    public Role Role = Role.None;
    /// <summary>x, z, yaw, pitch, speed, flags</summary>
    public double[]? Pose;
    public double PhaseReadyAt;
}

public sealed class RoundState
{
    public Phase Phase = Phase.Lobby;
    /// <summary>ms of the current phase's end (0 = no timer)</summary>
    public double EndsAt;
    public (double X, double Z)? Exit;
    /// <summary>"entity", "wanderers", "aborted" or null</summary>
    public string? Winner;
    public (double X, double Z)? EntityStart;

    public void Reset()
    {
        Phase = Phase.Lobby;
        EndsAt = 0;
        Exit = null;
        Winner = null;
        EntityStart = null;
    }
}

public abstract record HuntEvent
{
    public sealed record Round : HuntEvent;
    public sealed record Caught(int Id, int By) : HuntEvent;
    public sealed record Escaped(int Id) : HuntEvent;
    public sealed record Teleport(int Id, double X, double Z) : HuntEvent;
}

/// <summary>Entity spawn and exit distance from the wanderers, metres [min, max].</summary>
public sealed record Layout((double Min, double Max) Entity, (double Min, double Max) Exit);

public static class HuntRules
{
    public const double CountdownS = 10;
    /// <summary>Round length; shortened by tests.</summary>
    public static double HuntS = 300;
    public const double EndS = 10;
    public const double CatchDist = 1.1;
    public const double ExitDist = 1.5;
    public const double PhaseMs = 2000;
    public const double PhaseCooldownMs = 30000;

    /// <summary>Multiplayer: the entity is a person and the wanderers help each other.</summary>
    public static readonly Layout Room = new((30, 40), (70, 100));
    /// <summary>Single player: alone against the CPU, the way out is further.</summary>
    public static readonly Layout Solo = new((35, 45), (130, 170));

    static double Hypot(double x, double z) => Math.Sqrt(x * x + z * z);

    /// <summary>True if nothing solid lies between the two points (sampled every 0.2 m).</summary>
    public static bool LineOfSight(Gen gen, double ax, double az, double bx, double bz)
    {
        double d = Hypot(bx - ax, bz - az);
        int n = Math.Max(1, (int)Math.Ceiling(d / 0.2));
        for (int i = 1; i < n; i++)
        {
            double t = (double)i / n;
            if (gen.Solid((int)Math.Floor((ax + (bx - ax) * t) / Gen.Cell), (int)Math.Floor((az + (bz - az) * t) / Gen.Cell))) return false;
        }
        return true;
    }

    /// <summary>Start a round: pick the entity, place it and the exit away from everyone else.</summary>
    public static List<HuntEvent> StartRound(Gen gen, RoundState r, IList<Hunter> players, double now, Func<double> rand, Layout? layout = null)
    {
        layout ??= Room;
        var entity = players[(int)Math.Floor(rand() * players.Count)];
        // distances are measured from the wanderers (where the entity was standing doesn't matter)
        var placed = players.Where(p => p.Pose != null && p != entity).ToList();
        double cx = placed.Sum(p => p.Pose![0]) / Math.Max(1, placed.Count);
        double cz = placed.Sum(p => p.Pose![1]) / Math.Max(1, placed.Count);
        foreach (var p in players)
        {
            p.Role = p == entity ? Role.Entity : Role.Wanderer;
            p.PhaseReadyAt = now + CountdownS * 1000; // no phasing during the countdown
        }
        // the entity starts out of sight; the exit lies further away, in another direction
        double a = rand() * Math.PI * 2;
        double de = layout.Entity.Min + rand() * (layout.Entity.Max - layout.Entity.Min);
        var (ex, ez) = gen.FindFree(cx + Math.Cos(a) * de, cz + Math.Sin(a) * de, 2);
        double b = a + Math.PI * (0.6 + rand() * 0.8);
        double dx = layout.Exit.Min + rand() * (layout.Exit.Max - layout.Exit.Min);
        var (xx, xz) = gen.FindFree(cx + Math.Cos(b) * dx, cz + Math.Sin(b) * dx, 2);
        r.Phase = Phase.Countdown;
        r.EndsAt = now + CountdownS * 1000;
        r.Exit = (xx, xz);
        r.EntityStart = (ex, ez);
        r.Winner = null;
        return [new HuntEvent.Round(), new HuntEvent.Teleport(entity.Id, ex, ez)];
    }

    static List<HuntEvent> Finish(RoundState r, string winner, double now)
    {
        r.Phase = Phase.End;
        r.Winner = winner;
        r.EndsAt = now + EndS * 1000;
        return [new HuntEvent.Round()];
    }

    /// <summary>Advance the round; returns what the room needs to be told.</summary>
    public static List<HuntEvent> TickRound(Gen gen, RoundState r, IList<Hunter> players, double now)
    {
        var ev = new List<HuntEvent>();
        if (r.Phase == Phase.Lobby) return ev;
        if (r.Phase == Phase.End)
        {
            if (now >= r.EndsAt)
            {
                r.Reset();
                foreach (var p in players) p.Role = Role.None;
                ev.Add(new HuntEvent.Round());
            }
            return ev;
        }
        var entities = players.Where(p => p.Role == Role.Entity).ToList();
        var free = players.Where(p => p.Role == Role.Wanderer).ToList();
        if (entities.Count == 0 || players.Count < 2) return Finish(r, "aborted", now);
        if (r.Phase == Phase.Countdown)
        {
            if (now >= r.EndsAt)
            {
                r.Phase = Phase.Hunt;
                r.EndsAt = now + HuntS * 1000;
                ev.Add(new HuntEvent.Round());
            }
            return ev;
        }
        // hunt
        foreach (var w in free)
        {
            if (w.Pose == null) continue;
            double wx = w.Pose[0], wz = w.Pose[1];
            if (r.Exit is { } exit && Hypot(wx - exit.X, wz - exit.Z) < ExitDist)
            {
                w.Role = Role.Escaped;
                ev.Add(new HuntEvent.Escaped(w.Id));
                ev.Add(new HuntEvent.Round());
                continue;
            }
            foreach (var e in entities)
            {
                if (e.Pose == null) continue;
                double ex = e.Pose[0], ez = e.Pose[1];
                if (Hypot(wx - ex, wz - ez) < CatchDist && LineOfSight(gen, ex, ez, wx, wz))
                {
                    w.Role = Role.Entity; // the hunt snowballs
                    w.PhaseReadyAt = now + PhaseCooldownMs;
                    ev.Add(new HuntEvent.Caught(w.Id, e.Id));
                    ev.Add(new HuntEvent.Round());
                    break;
                }
            }
        }
        int stillFree = players.Count(p => p.Role == Role.Wanderer);
        int escaped = players.Count(p => p.Role == Role.Escaped);
        if (stillFree == 0) return [.. ev, .. Finish(r, escaped > 0 ? "wanderers" : "entity", now)];
        if (now >= r.EndsAt) return [.. ev, .. Finish(r, "wanderers", now)];
        return ev;
    }

    /// <summary>Entity asks to phase through walls. Returns the phase end time, or 0 if refused.</summary>
    public static double TryPhase(RoundState r, Hunter p, double now)
    {
        if (r.Phase != Phase.Hunt || p.Role != Role.Entity || now < p.PhaseReadyAt) return 0;
        p.PhaseReadyAt = now + PhaseMs + PhaseCooldownMs;
        return now + PhaseMs;
    }

    /// <summary>Role for someone arriving mid-round: they join the hunters.</summary>
    public static Role LateRole(RoundState r) => r.Phase is Phase.Countdown or Phase.Hunt ? Role.Entity : Role.None;
}
