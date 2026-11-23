// Client side of the multiplayer relay (server/server.ts), from web/src/net.ts: the JSON protocol,
// the other players, and a short snapshot buffer per player sampled ~120 ms in the past so their
// motion is smooth. No transport here: Game/Net feeds it the messages and sends what it returns.
//
//   c→s  {t:'hello', room, name, seed?}            s→c  {t:'welcome', id, seed, players:[{id,name,color,pose}]}
//   c→s  {t:'pose', x, z, yaw, pitch, v, f}         s→c  {t:'poses', p:[[id, x, z, yaw, pitch, v, f], ...]}
//   c→s  {t:'start'} / {t:'phase'}                  s→c  {t:'join'|'leave'|'full'|'round'|'caught'|'escaped'|'phase'|'teleport', ...}
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backrooms.Core;

public sealed class Peer(int id, string name, string color)
{
    public readonly record struct Snap(double T, double X, double Z, double Yaw, double Pitch, double V, int F);

    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Color { get; } = color;
    public readonly List<Snap> Snaps = [];
    /// <summary>Interpolated pose, refreshed by NetState.Sample.</summary>
    public double X, Z, Yaw, Pitch, V;
    /// <summary>Flags from the latest snapshot: 1 = out of breath, 2 = phasing.</summary>
    public int F;
    public bool Visible;
}

public sealed class NetState
{
    public const double DelayMs = 120;
    public const double SendMs = 66;

    public int Id { get; private set; }
    public int? Seed { get; private set; }
    public string Room { get; private set; } = "";
    public readonly Dictionary<int, Peer> Peers = [];
    public event Action<Peer>? Joined;
    public event Action<Peer>? Left;
    /// <summary>Hunt messages (round, caught, escaped, phase, teleport), raw.</summary>
    public event Action<JsonElement>? Game;
    /// <summary>The relay said the room is full.</summary>
    public bool Full { get; private set; }
    double lastSend = double.NegativeInfinity;

    public static string Hello(string room, string name, int? seed) =>
        new JsonObject { ["t"] = "hello", ["room"] = room, ["name"] = name, ["seed"] = seed is { } s ? JsonValue.Create(s) : null }.ToJsonString();

    public static string Simple(string t) => new JsonObject { ["t"] = t }.ToJsonString();

    public void Begin(string room) => Room = room;

    /// <summary>For an in-process relay (single player): who we are, and peers that aren't on the network.</summary>
    public void SetId(int id) => Id = id;

    public Peer AddPeer(int id, string name, string color)
    {
        var p = new Peer(id, name, color);
        Peers[id] = p;
        return p;
    }

    /// <summary>A pose message, or null when the last one went out less than 66 ms ago.</summary>
    public string? Pose(double nowMs, double x, double z, double yaw, double pitch, double v, int f = 0)
    {
        if (nowMs - lastSend < SendMs) return null;
        lastSend = nowMs;
        static double R(double a, int d) => Math.Round(a, d, MidpointRounding.AwayFromZero);
        return new JsonObject { ["t"] = "pose", ["x"] = R(x, 3), ["z"] = R(z, 3), ["yaw"] = R(yaw, 3), ["pitch"] = R(pitch, 3), ["v"] = R(v, 2), ["f"] = f }.ToJsonString();
    }

    public void Handle(string json, double nowMs)
    {
        JsonElement m;
        try
        {
            m = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var t)) return;
        switch (t.GetString())
        {
            case "welcome":
                Id = m.GetProperty("id").GetInt32();
                Seed = m.GetProperty("seed").GetInt32();
                foreach (var p in m.GetProperty("players").EnumerateArray())
                {
                    var peer = Add(p);
                    if (p.TryGetProperty("pose", out var pose) && pose.ValueKind == JsonValueKind.Array) Push(peer, pose, nowMs, 0);
                }
                break;
            case "full":
                Full = true;
                break;
            case "join":
                Joined?.Invoke(Add(m));
                break;
            case "leave":
                if (Peers.Remove(m.GetProperty("id").GetInt32(), out var gone)) Left?.Invoke(gone);
                break;
            case "round" or "caught" or "escaped" or "phase" or "teleport":
                Game?.Invoke(m.Clone());
                break;
            case "poses":
                foreach (var row in m.GetProperty("p").EnumerateArray())
                    if (Peers.TryGetValue(row[0].GetInt32(), out var p)) Push(p, row, nowMs, 1);
                break;
        }
    }

    Peer Add(JsonElement info)
    {
        var p = new Peer(info.GetProperty("id").GetInt32(), info.GetProperty("name").GetString() ?? "", info.GetProperty("color").GetString() ?? "#ffffff");
        Peers[p.Id] = p;
        return p;
    }

    static void Push(Peer p, JsonElement a, double nowMs, int o)
    {
        double V(int i) => a[o + i].GetDouble();
        int f = a.GetArrayLength() > o + 5 ? (int)a[o + 5].GetDouble() : 0;
        p.Snaps.Add(new Peer.Snap(nowMs, V(0), V(1), V(2), V(3), V(4), f));
        if (p.Snaps.Count > 30) p.Snaps.RemoveAt(0);
    }

    /// <summary>Interpolate every peer at (now - 120 ms).</summary>
    public void Sample(double nowMs)
    {
        double t = nowMs - DelayMs;
        foreach (var p in Peers.Values)
        {
            var s = p.Snaps;
            if (s.Count == 0) continue;
            p.Visible = true;
            Peer.Snap a = s[0], b = s[0];
            for (int i = s.Count - 1; i >= 0; i--)
                if (s[i].T <= t)
                {
                    a = s[i];
                    b = s[Math.Min(i + 1, s.Count - 1)];
                    break;
                }
            double k = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1;
            p.X = a.X + (b.X - a.X) * k;
            p.Z = a.Z + (b.Z - a.Z) * k;
            double dy = b.Yaw - a.Yaw;
            dy -= Gen.JsRound(dy / (Math.PI * 2)) * Math.PI * 2;
            p.Yaw = a.Yaw + dy * k;
            p.Pitch = a.Pitch + (b.Pitch - a.Pitch) * k;
            p.V = a.V + (b.V - a.V) * k;
            p.F = b.F;
        }
    }
}
