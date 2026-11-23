// Fog-of-war map state, from web/src/minimap.ts: every ~100 ms a fan of rays is cast through the
// occupancy grid from the player; each cell a ray reaches is marked as discovered and painted, one
// pixel per cell, into a per-chunk RGBA tile. Also the marks you place on the full map and walk to.
// No engine types: Game/UI draws the tiles and the marks.
namespace Backrooms.Core;

public sealed class Marker(int id, double x, double z, string color)
{
    public int Id { get; } = id;
    public double X { get; } = x;
    public double Z { get; } = z;
    public string Color { get; } = color;
    public bool Reached { get; set; }
}

public sealed class MapTile
{
    public const int Size = Gen.ChunkCells;
    public readonly byte[] Rgba = new byte[Size * Size * 4];
    public readonly bool[] Seen = new bool[Size * Size];
    /// <summary>Set when cells were painted; the drawer uploads and clears it.</summary>
    public bool Dirty;
}

public sealed class MapData(Gen gen)
{
    public const int Rays = 160;
    public const double Range = 18; // metres
    public const double ReachDist = 1.4;
    static readonly byte[] Floor = [226, 208, 128, 170];
    static readonly byte[] Wall = [58, 48, 18, 245];
    static readonly string[] Colors = ["#ff3b2f", "#2fd3ff", "#7dff4a", "#ff9f1c", "#e14bff", "#ffffff"];

    public readonly Dictionary<(int, int), MapTile> Tiles = [];
    public readonly List<Marker> Markers = [];
    public event Action<Marker>? Reached;
    int nextId = 1;
    double acc;

    public Marker AddMarker(double x, double z)
    {
        var m = new Marker(nextId, x, z, Colors[(nextId - 1) % Colors.Length]);
        nextId++;
        Markers.Add(m);
        return m;
    }

    /// <summary>Remove the marker nearest to (x, z) within r metres.</summary>
    public bool RemoveNear(double x, double z, double r)
    {
        int best = -1;
        double bd = r * r;
        for (int i = 0; i < Markers.Count; i++)
        {
            double d = (Markers[i].X - x) * (Markers[i].X - x) + (Markers[i].Z - z) * (Markers[i].Z - z);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }
        if (best < 0) return false;
        Markers.RemoveAt(best);
        return true;
    }

    /// <summary>Next unreached marker, in the order they were placed.</summary>
    public Marker? Target => Markers.FirstOrDefault(m => !m.Reached);

    public void CheckReached(double x, double z)
    {
        foreach (var m in Markers)
            if (!m.Reached && (m.X - x) * (m.X - x) + (m.Z - z) * (m.Z - z) < ReachDist * ReachDist)
            {
                m.Reached = true;
                Reached?.Invoke(m);
            }
    }

    MapTile Tile(int ci, int cj)
    {
        if (!Tiles.TryGetValue((ci, cj), out var t)) Tiles[(ci, cj)] = t = new MapTile();
        return t;
    }

    void Mark(int gx, int gz, bool wall)
    {
        int ci = (int)Math.Floor((double)gx / MapTile.Size), cj = (int)Math.Floor((double)gz / MapTile.Size);
        var t = Tile(ci, cj);
        int i = (gz - cj * MapTile.Size) * MapTile.Size + gx - ci * MapTile.Size;
        if (t.Seen[i]) return;
        t.Seen[i] = true;
        Array.Copy(wall ? Wall : Floor, 0, t.Rgba, i * 4, 4);
        t.Dirty = true;
    }

    public bool IsSeen(int gx, int gz)
    {
        int ci = (int)Math.Floor((double)gx / MapTile.Size), cj = (int)Math.Floor((double)gz / MapTile.Size);
        return Tiles.TryGetValue((ci, cj), out var t) && t.Seen[(gz - cj * MapTile.Size) * MapTile.Size + gx - ci * MapTile.Size];
    }

    /// <summary>Reveal what is visible from (x, z). Throttled to every 100 ms unless forced.</summary>
    public void Reveal(double x, double z, double dt, bool force = false)
    {
        acc += dt;
        if (!force && acc < 0.1) return;
        acc = 0;
        const double step = Gen.Cell * 0.5;
        for (int r = 0; r < Rays; r++)
        {
            double a = (double)r / Rays * Math.PI * 2;
            double dx = Math.Cos(a), dz = Math.Sin(a);
            int lastX = int.MinValue, lastZ = int.MinValue;
            for (double d = 0; d < Range; d += step)
            {
                int gx = (int)Math.Floor((x + dx * d) / Gen.Cell);
                int gz = (int)Math.Floor((z + dz * d) / Gen.Cell);
                if (gx == lastX && gz == lastZ) continue;
                lastX = gx;
                lastZ = gz;
                if (gen.Solid(gx, gz))
                {
                    Mark(gx, gz, true);
                    break;
                }
                Mark(gx, gz, false);
            }
        }
    }
}
