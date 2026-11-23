// The CPU entity (web/src/ai/entity.ts). It finds you the way a player would: by seeing you (a cone,
// shorter in the dark, blocked by walls, with a detection meter that takes a moment to fill) and by
// hearing you (sprinting carries far, panting further, walls muffle both). A small state machine
// turns that into roaming, investigating, chasing and searching; a "director" paces the tension so
// it neither loses you forever nor hounds you without a breather. No access to your position
// except through those senses, a short grace after losing sight, and the director's vague hints.
namespace Backrooms.Core.AI;

public enum Difficulty { Easy, Normal, Nightmare }
public enum AiState { Roam, Investigate, Chase, Search }

public sealed record Tuning(
    double SightLit, double SightDark, double DetectTime, double Hearing,
    double SpeedRoam, double SpeedSeek, double SpeedChase, double HintAfter, double WallCost, double Grace)
{
    public static Tuning For(Difficulty d) => d switch
    {
        Difficulty.Easy => new(20, 5, 1.3, 0.8, 1.4, 2.4, 4.1, 30, 16, 1.5),
        Difficulty.Nightmare => new(30, 9, 0.6, 1.3, 2.0, 3.1, 4.6, 15, 6, 2.2),
        _ => new(24, 7, 1.0, 1, 1.6, 2.8, 4.4, 25, 10, 2.0),
    };
}

/// <summary>What the entity's senses get to work with each tick (the real player state).</summary>
public readonly record struct Prey(double X, double Z, double Vx, double Vz, double Speed, bool Panting);

public interface IEntityHooks
{
    /// <summary>Ask the referee for a phase; true if granted (it then lasts 2 s).</summary>
    bool Phase();
    /// <summary>Is the phase ability off cooldown? (used when planning paths)</summary>
    bool PhaseReady();
    double Rand();
}

public sealed class EntityAI
{
    const double PhaseS = 2;
    static readonly double FovCos = Math.Cos(110.0 / 2 * (Math.PI / 180));

    public double X, Z, Yaw, Speed;
    public AiState State = AiState.Roam;
    /// <summary>0..1, fills while it can see you</summary>
    public double Detect;
    public double PhasingUntil;
    public readonly Tuning T;
    public readonly Difficulty Difficulty;
    public readonly PathFinder Paths;

    readonly IEntityHooks hooks;
    readonly Dictionary<(int, int), bool> litCache = [];
    double now;
    List<Waypoint> path = [];
    (double X, double Z)? goal;
    double repathAt, lookUntil, lookBase;
    (double X, double Z, double Vx, double Vz, double T)? lastSeen;
    readonly List<(double X, double Z)> searchPts = [];
    double searchUntil, chaseStart, lastContact, retreatUntil, heardAt = -99;
    (double X, double Z, double T) progress;

    public EntityAI(Difficulty difficulty, IEntityHooks hooks, PathFinder paths)
    {
        Difficulty = difficulty;
        this.hooks = hooks;
        Paths = paths;
        T = Tuning.For(difficulty);
    }

    static double Hypot(double x, double z) => Math.Sqrt(x * x + z * z);

    // lit/dark lookup per light-lattice cell (LightKind is pure, so cache it)
    bool LitAt(double x, double z)
    {
        int i0 = (int)Math.Floor(x / Gen.LightPeriod), j0 = (int)Math.Floor(z / Gen.LightPeriod);
        for (int j = j0 - 1; j <= j0 + 1; j++)
            for (int i = i0 - 1; i <= i0 + 1; i++)
            {
                if (!litCache.TryGetValue((i, j), out bool lit))
                {
                    if (litCache.Count > 20000) litCache.Clear();
                    litCache[(i, j)] = lit = Paths.Gen.LightKind(i, j) >= 2;
                }
                if (lit && Hypot((i + 0.5) * Gen.LightPeriod - x, (j + 0.5) * Gen.LightPeriod - z) < 4.5) return true;
            }
        return false;
    }

    public void Place(double x, double z)
    {
        (X, Z) = (x, z);
        path = [];
        goal = null;
        State = AiState.Roam;
        Detect = 0;
        lastSeen = null;
        lastContact = now - T.HintAfter * 0.6; // its first hint comes early
        progress = (x, z, now);
    }

    public bool Phasing => now < PhasingUntil;

    /// <summary>Can it see the prey right now? (cone, range by light, walls)</summary>
    public bool CanSee(Prey p)
    {
        double dx = p.X - X, dz = p.Z - Z;
        double d = Hypot(dx, dz);
        double range = LitAt(p.X, p.Z) ? T.SightLit : T.SightDark;
        bool chasing = State == AiState.Chase;
        if (d > range * (chasing ? 1.25 : 1)) return false;
        // facing: -sin(yaw), -cos(yaw). Within 4 m it senses you all around.
        if (d > 4)
        {
            double fx = -Math.Sin(Yaw), fz = -Math.Cos(Yaw);
            if ((dx * fx + dz * fz) / d < (chasing ? 0 : FovCos)) return false;
        }
        return Paths.ClearLine(X, Z, p.X, p.Z);
    }

    /// <summary>How far the prey is audible right now.</summary>
    public double HearingRange(Prey p, bool throughWalls)
    {
        double r = p.Speed > 3 ? 18 : p.Speed > 0.6 ? 5 : 0;
        if (p.Panting) r = Math.Max(r, 10);
        return r * T.Hearing * (throughWalls ? 0.55 : 1);
    }

    public void Update(double dt, double nowS, Prey p)
    {
        now = nowS;
        double d = Hypot(p.X - X, p.Z - Z);

        // ---- sight ----
        bool seen = CanSee(p);
        if (seen)
        {
            double range = LitAt(p.X, p.Z) ? T.SightLit : T.SightDark;
            // alert (just lost you, or checking a noise): it recognises you twice as fast
            double alert = State is AiState.Search or AiState.Investigate ? 0.5 : 1;
            Detect = Math.Min(1, Detect + dt / (alert * T.DetectTime * (0.25 + 0.75 * Math.Min(1, d / range))));
        }
        else Detect = Math.Max(0, Detect - dt * 0.4);
        if (seen && Detect >= 1)
        {
            if (State != AiState.Chase)
            {
                State = AiState.Chase;
                chaseStart = now;
                repathAt = 0;
            }
            lastSeen = (p.X, p.Z, p.Vx, p.Vz, now);
            lastContact = now;
        }
        else if (seen && Detect > 0.35 && State == AiState.Roam)
        {
            // something moved over there: go and look
            Investigate(p.X, p.Z);
        }

        // ---- hearing ----
        if (State != AiState.Chase && now - heardAt > 1.2)
        {
            bool walls = !Paths.ClearLine(X, Z, p.X, p.Z);
            double r = HearingRange(p, walls);
            if (d < r)
            {
                heardAt = now;
                lastContact = now;
                // a sound gives a rough direction, worse the further away
                double err = 1 + 3 * (d / r);
                double a = hooks.Rand() * Math.PI * 2;
                double ox = Math.Cos(a) * err * hooks.Rand();
                double oz = Math.Sin(a) * err * hooks.Rand();
                Investigate(p.X + ox, p.Z + oz);
            }
        }

        // ---- states ----
        double speed = T.SpeedRoam;
        switch (State)
        {
            case AiState.Chase:
            {
                speed = T.SpeedChase;
                var ls = lastSeen!.Value;
                if (!seen && now - ls.T > T.Grace)
                {
                    // lost it: search along where it was heading
                    StartSearch(ls);
                    break;
                }
                // during the grace period it keeps tracking the real position (no cheap sidestep escapes)
                double tx = seen ? p.X + p.Vx * 0.5 : p.X, tz = seen ? p.Z + p.Vz * 0.5 : p.Z;
                if (d < 6 && Paths.ClearLine(X, Z, p.X, p.Z, 0.2))
                {
                    path = [new Waypoint(p.X, p.Z, false)]; // close and clear: straight at you
                    goal = (p.X, p.Z);
                }
                else Plan(tx, tz, 0.3);
                break;
            }
            case AiState.Investigate:
                speed = T.SpeedSeek;
                if (Arrived()) LookAround(2.5, () => State = AiState.Roam);
                break;
            case AiState.Search:
                speed = T.SpeedSeek;
                if (now > searchUntil)
                {
                    EndSearch();
                    break;
                }
                if (Arrived())
                    LookAround(1.6, () =>
                    {
                        if (searchPts.Count > 0)
                        {
                            var nxt = searchPts[0];
                            searchPts.RemoveAt(0);
                            SetGoal(nxt.X, nxt.Z);
                        }
                        else EndSearch();
                    });
                break;
            case AiState.Roam:
                // the longer it goes without finding you, the more it hurries
                if (now - lastContact > T.HintAfter * 0.5) speed = T.SpeedSeek;
                if (goal == null || Arrived()) LookAround(0.4 + hooks.Rand() * 0.8, () => PickRoam(p));
                break;
        }

        // the director's rubber band: far away and out of your sight, it covers ground fast
        // (it never does this where you could see it move)
        if (State != AiState.Chase && d > 25 && now > retreatUntil && !Paths.ClearLine(p.X, p.Z, X, Z)) speed = Math.Max(speed, T.SpeedChase * 1.15);
        Move(dt, speed);
        Speed = speed;

        // stuck? (wedged, or chasing a goal it can't reach): drop the path and think again
        if (Hypot(X - progress.X, Z - progress.Z) > 0.8) progress = (X, Z, now);
        else if (now - progress.T > 3 && now > lookUntil)
        {
            progress.T = now;
            path = [];
            if (State != AiState.Chase) PickRoam(p);
            else repathAt = 0;
        }
    }

    // ---- behaviours ----
    void Investigate(double x, double z)
    {
        State = AiState.Investigate;
        lookUntil = 0;
        SetGoal(x, z);
    }

    void StartSearch((double X, double Z, double Vx, double Vz, double T) ls)
    {
        State = AiState.Search;
        searchUntil = now + 25;
        // first, where you would be now had you kept going the same way; then around and beyond it
        double v = Hypot(ls.Vx, ls.Vz);
        double ux = v > 0.2 ? ls.Vx / v : 0, uz = v > 0.2 ? ls.Vz / v : 0;
        double lead = Math.Min(10, 2 + v * 2.5);
        var (px, pz) = Paths.Gen.FindFree(ls.X + ux * lead, ls.Z + uz * lead, 1);
        searchPts.Clear();
        for (int i = 0; i < 4; i++)
        {
            double a = hooks.Rand() * Math.PI * 2, r = 3 + hooks.Rand() * 7;
            searchPts.Add(Paths.Gen.FindFree(px + ux * 6 + Math.Cos(a) * r, pz + uz * 6 + Math.Sin(a) * r, 1));
        }
        SetGoal(px, pz);
        // a long chase earns the player a breather
        if (now - chaseStart > 8) retreatUntil = now + 20 + hooks.Rand() * 20;
    }

    void EndSearch()
    {
        State = AiState.Roam;
        goal = null;
        path = [];
    }

    void PickRoam(Prey p)
    {
        double x, z;
        if (now < retreatUntil)
        {
            // back off, away from the player
            double a = Math.Atan2(Z - p.Z, X - p.X) + (hooks.Rand() - 0.5) * 1.2;
            x = X + Math.Cos(a) * (15 + hooks.Rand() * 15);
            z = Z + Math.Sin(a) * (15 + hooks.Rand() * 15);
        }
        else if (now - lastContact > T.HintAfter)
        {
            // the director: a vague idea of where you are going (ahead of you, blurred by 10-16 m;
            // never the spot itself), so it tends to come at you from the front
            double a = hooks.Rand() * Math.PI * 2, e = 10 + hooks.Rand() * 6;
            x = p.X + p.Vx * 8 + Math.Cos(a) * e;
            z = p.Z + p.Vz * 8 + Math.Sin(a) * e;
            lastContact = now - T.HintAfter * 0.5; // next hint comes sooner
        }
        else
        {
            // drift, loosely towards the player's part of the building
            double toward = Math.Atan2(p.Z - Z, p.X - X);
            double a = toward + (hooks.Rand() - 0.5) * Math.PI * 1.4;
            double dist = 12 + hooks.Rand() * 18;
            x = X + Math.Cos(a) * dist;
            z = Z + Math.Sin(a) * dist;
        }
        var (fx, fz) = Paths.Gen.FindFree(x, z, 1);
        SetGoal(fx, fz);
    }

    void LookAround(double secs, Action then)
    {
        if (lookUntil == 0)
        {
            lookUntil = now + secs;
            lookBase = Yaw;
            path = [];
        }
        else if (now >= lookUntil)
        {
            lookUntil = 0;
            then();
        }
    }

    void SetGoal(double x, double z)
    {
        goal = (x, z);
        lookUntil = 0;
        repathAt = 0;
        Plan(x, z, 1);
    }

    void Plan(double x, double z, double every)
    {
        if (now < repathAt && path.Count > 0) return;
        repathAt = now + every;
        goal = (x, z);
        bool ready = hooks.PhaseReady();
        path = Paths.FindPath(X, Z, x, z, ready ? T.WallCost : double.PositiveInfinity, 110);
    }

    bool Arrived() => goal is not { } g || (path.Count == 0 && lookUntil == 0) || Hypot(g.X - X, g.Z - Z) < 0.6;

    void Move(double dt, double speed)
    {
        // looking around: stand still, sweep the head
        if (lookUntil > 0)
        {
            Yaw = lookBase + Math.Sin((lookUntil - now) * 2.4) * 1.1;
            return;
        }
        double budget = speed * dt;
        while (budget > 0 && path.Count > 0)
        {
            var w = path[0];
            if (w.Wall && !Phasing)
            {
                if (hooks.Phase()) PhasingUntil = now + PhaseS;
                else
                {
                    path = []; // refused (cooldown): plan again with walls solid
                    repathAt = 0;
                    break;
                }
            }
            double dx = w.X - X, dz = w.Z - Z;
            double d = Hypot(dx, dz);
            if (d < 1e-4)
            {
                path.RemoveAt(0);
                continue;
            }
            double step = Math.Min(d, budget);
            // never step into a wall unless phasing
            double nx = X + dx / d * step, nz = Z + dz / d * step;
            // (if a phase ran out mid-wall, it may keep pushing through to get out)
            bool inWall = Paths.WallAt((int)Math.Floor(X / Gen.Cell), (int)Math.Floor(Z / Gen.Cell));
            if (!Phasing && !inWall && Paths.WallAt((int)Math.Floor(nx / Gen.Cell), (int)Math.Floor(nz / Gen.Cell)))
            {
                path = [];
                repathAt = 0;
                break;
            }
            (X, Z) = (nx, nz);
            budget -= step;
            // turn towards where it is going (its eyes follow)
            double want = Math.Atan2(-dx, -dz);
            double dy = want - Yaw;
            dy -= Gen.JsRound(dy / (Math.PI * 2)) * Math.PI * 2;
            Yaw += Math.Clamp(dy, -6 * dt, 6 * dt);
            if (step >= d) path.RemoveAt(0);
        }
    }
}
