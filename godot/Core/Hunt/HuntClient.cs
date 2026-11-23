// Client side of "The Hunt" (web/src/hunt.ts): the round state the relay (or LocalRelay) broadcasts,
// the local-only mechanics (stamina, phasing), and what the HUD should say. No UI here: the game
// shows Banner/Toast and reads the status line and bar.
using System.Text.Json;

namespace Backrooms.Core.Hunt;

public sealed class HuntClient
{
    public const double EntitySpeed = 3.7; // m/s, no sprint: faster than a walk, slower than a run
    public const double StaminaMax = 6; // seconds of full sprint
    const double StaminaRegen = 0.6; // per second, after a short pause
    const double StaminaDelay = 1.0;

    public Phase Phase = Phase.Lobby;
    public double EndsAt; // ms
    public readonly Dictionary<int, Role> Roles = [];
    public (double X, double Z)? Exit;
    public string? Winner;
    public double Stamina = StaminaMax;
    public bool Exhausted;
    double sinceSprint;
    public double PhasingUntil, PhaseReadyAt;
    /// <summary>Remote entities currently phasing: id -> until (ms).</summary>
    public readonly Dictionary<int, double> RemotePhase = [];
    /// <summary>Single player against the CPU (only changes some wording).</summary>
    public bool Solo;
    /// <summary>Seconds of "you were caught" glitch left.</summary>
    public double CaughtFx;
    public string BannerTitle = "", BannerSub = "";
    public double BannerT;

    public event Action<double, double>? Teleport;
    public event Action<string>? Toast;
    public event Action? PhaseEnded;

    readonly NetState? net;
    readonly Action<string>? send;
    double now;

    public HuntClient(NetState? net, Action<string>? send)
    {
        this.net = net;
        this.send = send;
        if (net != null) net.Game += m => Message(m, now);
    }

    int MyId => net?.Id ?? 0;
    public Role MyRole => Roles.GetValueOrDefault(MyId, Role.None);
    public Role RoleOf(int id) => Roles.GetValueOrDefault(id, Role.None);
    public bool Active => Phase is Phase.Countdown or Phase.Hunt;
    public bool IsEntity => Active && MyRole == Role.Entity;
    public bool IsWanderer => Active && MyRole == Role.Wanderer;
    /// <summary>The entity waits in the dark while the wanderers scatter.</summary>
    public bool Frozen => Phase == Phase.Countdown && MyRole == Role.Entity;
    public bool Phasing => now < PhasingUntil;

    /// <summary>Messages are timestamped with the clock last passed to Update.</summary>
    public void SetClock(double nowMs) => now = nowMs;

    public void Message(JsonElement m, double nowMs)
    {
        now = nowMs;
        switch (m.GetProperty("t").GetString())
        {
            case "round":
            {
                var prev = Phase;
                var prevRole = MyRole;
                Phase = Names.ParsePhase(m.GetProperty("phase").GetString());
                EndsAt = nowMs + m.GetProperty("remaining").GetDouble() * 1000;
                var ex = m.GetProperty("exit");
                Exit = ex.ValueKind == JsonValueKind.Object ? (ex.GetProperty("x").GetDouble(), ex.GetProperty("z").GetDouble()) : null;
                Winner = m.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                Roles.Clear();
                foreach (var r in m.GetProperty("roles").EnumerateArray()) Roles[r[0].GetInt32()] = Names.ParseRole(r[1].GetString());
                if (Phase == Phase.Countdown && prev != Phase.Countdown)
                {
                    Stamina = StaminaMax;
                    Exhausted = false;
                    PhaseReadyAt = nowMs + 10000;
                    if (MyRole == Role.Entity) Banner("YOU ARE THE ENTITY", "wait in the dark. then find them.", 5);
                    else Banner("RUN.", "something is coming. find the exit, or outlast it.", 5);
                }
                if (Phase == Phase.Hunt && prev == Phase.Countdown && MyRole == Role.Entity) Banner("HUNT", "E — walk through walls", 3);
                if (Phase == Phase.End && prev != Phase.End) EndScreen();
                if (Phase == Phase.Lobby && prev == Phase.End) HideBanner();
                if (prevRole == Role.Wanderer && MyRole == Role.Escaped) Banner("YOU GOT OUT", "for now.", 6);
                break;
            }
            case "caught":
            {
                int id = m.GetProperty("id").GetInt32();
                if (id == MyId)
                {
                    CaughtFx = 2.2;
                    PhaseReadyAt = nowMs + HuntRules.PhaseCooldownMs;
                    Banner("FOUND", Solo ? "it was right behind you." : "you are one of them now. hunt the others.", 5);
                }
                else Toast?.Invoke($"{Name(id)} WAS FOUND");
                break;
            }
            case "escaped":
            {
                int id = m.GetProperty("id").GetInt32();
                if (id != MyId) Toast?.Invoke($"{Name(id)} FOUND THE EXIT");
                break;
            }
            case "phase":
            {
                double until = nowMs + m.GetProperty("ms").GetDouble();
                int id = m.GetProperty("id").GetInt32();
                if (id == MyId)
                {
                    PhasingUntil = until;
                    PhaseReadyAt = until + HuntRules.PhaseCooldownMs;
                }
                else RemotePhase[id] = until;
                break;
            }
            case "teleport":
                Teleport?.Invoke(m.GetProperty("x").GetDouble(), m.GetProperty("z").GetDouble());
                break;
        }
    }

    string Name(int id) => (net != null && net.Peers.TryGetValue(id, out var p) ? p.Name : "someone").ToUpperInvariant();

    /// <summary>Lobby: ask the relay to start a round.</summary>
    public void RequestStart()
    {
        if (net == null)
        {
            Toast?.Invoke("THE HUNT NEEDS A ROOM (MENU → MULTIPLAYER)");
            return;
        }
        if (Phase != Phase.Lobby) return;
        if (net.Peers.Count < 1)
        {
            Toast?.Invoke("THE HUNT NEEDS AT LEAST 2 PLAYERS");
            return;
        }
        send?.Invoke(NetState.Simple("start"));
    }

    public void RequestPhase()
    {
        if (!IsEntity || Phase != Phase.Hunt) return;
        if (now < PhaseReadyAt)
        {
            Toast?.Invoke("NOT YET");
            return;
        }
        send?.Invoke(NetState.Simple("phase"));
    }

    /// <summary>Can the local player sprint right now? Drains/regenerates stamina as a side effect.</summary>
    public bool SprintGate(bool wantRun, double moving, double dt)
    {
        if (!IsWanderer)
        {
            Stamina = StaminaMax;
            Exhausted = false;
            return wantRun;
        }
        bool can = wantRun && !Exhausted;
        if (can && moving > 2)
        {
            Stamina = Math.Max(0, Stamina - dt);
            sinceSprint = 0;
            if (Stamina == 0)
            {
                Exhausted = true;
                Toast?.Invoke("OUT OF BREATH");
            }
        }
        else
        {
            sinceSprint += dt;
            if (sinceSprint > StaminaDelay) Stamina = Math.Min(StaminaMax, Stamina + StaminaRegen * dt);
            if (Exhausted && Stamina > StaminaMax * 0.35) Exhausted = false;
        }
        return can;
    }

    /// <summary>Pose flags for the relay: 1 = out of breath (audible), 2 = phasing.</summary>
    public int Flags => (IsWanderer && Stamina < StaminaMax * 0.3 ? 1 : 0) | (Phasing ? 2 : 0);

    public void Banner(string title, string sub, double secs)
    {
        (BannerTitle, BannerSub, BannerT) = (title, sub, secs);
    }

    public void HideBanner() => BannerT = 0;

    void EndScreen()
    {
        if (Winner == "entity") Banner(Solo ? "IT FOUND YOU" : "NO ONE GOT OUT", Solo ? "in a moment, R to try again" : "the entity found everyone", 9);
        else if (Winner == "wanderers")
        {
            var outNames = Roles.Where(r => r.Value is Role.Escaped or Role.Wanderer).Select(r => r.Key == MyId ? "YOU" : Name(r.Key));
            Banner("SOMEONE GOT OUT", string.Join(", ", outNames) + " survived", 9);
        }
        else Banner("THE HUNT IS OVER", "the entity left", 6);
    }

    /// <summary>Per frame: timers.</summary>
    public void Update(double dt, double nowMs)
    {
        now = nowMs;
        if (BannerT > 0) BannerT = Math.Max(0, BannerT - dt);
        CaughtFx = Math.Max(0, CaughtFx - dt);
        if (PhasingUntil > 0 && nowMs >= PhasingUntil)
        {
            PhasingUntil = 0;
            PhaseEnded?.Invoke();
        }
    }

    public double SecondsLeft => Math.Max(0, (EndsAt - now) / 1000);

    /// <summary>The status line at the top of the screen.</summary>
    public string Line()
    {
        if (net == null) return "";
        double left = SecondsLeft;
        int free = Roles.Values.Count(r => r == Role.Wanderer), hunters = Roles.Values.Count(r => r == Role.Entity);
        return Phase switch
        {
            Phase.Lobby => net.Peers.Count >= 1 ? "PRESS R TO START THE HUNT" : "THE HUNT: WAITING FOR A SECOND PLAYER",
            Phase.Countdown => $"{(MyRole == Role.Entity ? "RELEASED IN" : "IT WAKES IN")} {Math.Ceiling(left)}",
            Phase.Hunt => $"{(int)(left / 60)}:{(int)(left % 60):00} · {free} FREE · {hunters} HUNTING",
            _ => "NEXT ROUND SOON",
        };
    }

    /// <summary>Stamina bar for wanderers, phase charge for entities: label, fill 0..1, bad. Null = hidden.</summary>
    public (string Label, double Fill, bool Bad)? Bar()
    {
        if (IsWanderer) return (Exhausted ? "BREATH" : "STAMINA", Stamina / StaminaMax, Exhausted);
        if (IsEntity && Phase == Phase.Hunt)
        {
            double ready = Math.Max(0, PhaseReadyAt - now);
            string label = Phasing ? "PHASING" : ready > 0 ? "PHASE (E)" : "PHASE READY (E)";
            double fill = Phasing ? (PhasingUntil - now) / HuntRules.PhaseMs : 1 - ready / HuntRules.PhaseCooldownMs;
            return (label, fill, false);
        }
        return null;
    }
}
