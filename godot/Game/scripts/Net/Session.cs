using Backrooms.Core;
using Backrooms.Core.AI;
using Backrooms.Core.Hunt;

namespace Backrooms.Net;

/// <summary>Who we play with: the relay (a room) or the in-process stand-in (alone vs the CPU).</summary>
public interface ISession : IDisposable
{
    NetState State { get; }
    bool Connected { get; }
    /// <summary>"ROOM X · 3", "ALONE · NORMAL", "SIGNAL LOST"</summary>
    string Label { get; }
    void Send(string json);
    void Pose(double nowMs, double x, double z, double yaw, double pitch, double v, int flags);
    /// <summary>Per frame: take in what arrived, move the others.</summary>
    void Tick(double nowMs);
    event Action? Closed;
}

/// <summary>Single player: the hunt rules and the CPU entity, run in the game (web/src/localnet.ts).</summary>
public sealed class LocalSession(Difficulty difficulty, Gen gen) : ISession
{
    readonly NetState state = new();
    LocalRelay? relay;
    double now;

    LocalRelay Relay => relay ??= new LocalRelay(difficulty, gen, state);
    public NetState State
    {
        get
        {
            _ = Relay;
            return state;
        }
    }
    public Difficulty Difficulty => difficulty;
    public EntityAI Ai => Relay.Ai;
    public bool Connected => true;
    public string Label => $"ALONE · {difficulty.ToString().ToUpperInvariant()}";
    public event Action? Closed { add { } remove { } }

    public void Send(string json) => Relay.Send(json, now);
    public void Pose(double nowMs, double x, double z, double yaw, double pitch, double v, int flags) => Relay.Pose(x, z, yaw, pitch, v, flags, nowMs);

    public void Tick(double nowMs)
    {
        now = nowMs;
        Relay.Tick(nowMs);
    }

    public void Dispose()
    {
    }
}

/// <summary>Multiplayer through server/server.ts.</summary>
public sealed class RelaySession(NetClient client, string room) : ISession
{
    public NetState State => client.State;
    public bool Connected => client.Connected;
    public string Label => client.Connected ? $"ROOM {room.ToUpperInvariant()} · {client.State.Peers.Count + 1}" : "SIGNAL LOST";
    public event Action? Closed
    {
        add => client.Closed += value;
        remove => client.Closed -= value;
    }

    public void Send(string json) => client.Send(json);

    public void Pose(double nowMs, double x, double z, double yaw, double pitch, double v, int flags)
    {
        if (client.State.Pose(nowMs, x, z, yaw, pitch, v, flags) is { } m) client.Send(m);
    }

    public void Tick(double nowMs)
    {
        client.Poll(nowMs);
        client.State.Sample(nowMs);
    }

    public void Dispose() => client.Dispose();
}
