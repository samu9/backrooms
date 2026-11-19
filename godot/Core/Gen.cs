// Deterministic, infinite layout: a C# port of shared/gen.ts, which stays the source of truth.
// Everything here is a pure function of (seed, global cell coords), and must give the same
// answers as the TypeScript bit for bit: the relay decides catches with the TS solid().
// Checked by Core.Tests/GenParityTests.cs against tests/fixtures/gen-golden.json.gz.
//
// Porting rules (see docs/godot-port-plan.html, "Determinism & parity"):
//   - Math.imul / >>> become unchecked int / uint arithmetic, which wraps the same way;
//   - JS numbers are doubles, so floating point stays double, in the same operation order;
//   - Math.floor stays Math.Floor (never a cast: casts truncate toward zero);
//   - Math.round is JsRound (ties toward +infinity), not Math.Round (ties to even).
using System.Collections.Concurrent;

namespace Backrooms.Core;

public enum ZoneType { Office = 0, Maze = 1, Hall = 2, Void = 3, Weird = 4, Corridor = 5 }

public sealed class Zone
{
    public ZoneType Type;
    public int Id;
    public int S; // wall-line spacing (cells)
    public int Off;
    public double P; // segment probability (x-lines)
    public double Pz; // segment probability (z-lines)
    public double Pd; // door probability per segment
    public int Dw; // door width (cells)
    public double Split; // chance of an inner wall per block
    public double Pillar; // chance of a pillar at a line crossing
    public int PillarSize;
    public bool Partial; // segments may stop short
    public double H; // ceiling height (m)
    public bool Rail; // wooden chair rail on the walls
}

/// <summary>The world for one seed. Thread-safe: chunks can be built on worker threads.</summary>
public sealed class Gen
{
    public const double Cell = 0.5; // metres per occupancy cell
    public const int ChunkCells = 64;
    public const double Chunk = Cell * ChunkCells; // 32 m
    public const int Border = 16; // cells of neighbour data baked around each chunk texture
    public const int Tex = ChunkCells + 2 * Border; // 96
    public const double LightPeriod = 3.0; // metres between ceiling panels
    public const double HScale = 8.0; // ceiling height encoding range in the texture (metres)

    const int ZoneSize = 52; // cells (26 m), before warping

    public int Seed { get; }
    readonly ConcurrentDictionary<long, Zone> zoneCache = new();

    public Gen(int seed) => Seed = seed;

    /// <summary>32-bit integer hash -> [0,1). Identical to hash3 in gen.ts and in the shaders.</summary>
    public double Hash3(int x, int y, int z)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + (z + Seed * 1013) * -2048144777;
            h = (int)((uint)h ^ ((uint)h >> 13)) * 1274126177;
            h ^= (int)((uint)h >> 16);
            return (uint)h / 4294967296.0;
        }
    }

    double VNoise1(double x, int salt)
    {
        double i = Math.Floor(x);
        double f = x - i;
        double u = f * f * (3 - 2 * f);
        int ii = (int)i;
        return (Hash3(ii, 0, salt) * (1 - u) + Hash3(ii + 1, 0, salt) * u) * 2 - 1;
    }

    static int Mod(int a, int b) => ((a % b) + b) % b;
    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>JavaScript's Math.round: halves go toward +infinity.</summary>
    public static double JsRound(double x)
    {
        double f = Math.Floor(x);
        return x - f >= 0.5 ? f + 1 : f;
    }

    Zone MakeZone(int zi, int zj)
    {
        int id = unchecked(zi * 73856093 ^ zj * 19349663);
        double r = Hash3(zi, zj, 1);
        double r2 = Hash3(zi, zj, 2);
        double r3 = Hash3(zi, zj, 3);
        ZoneType type;
        if (r < 0.34) type = ZoneType.Office;
        else if (r < 0.5) type = ZoneType.Maze;
        else if (r < 0.64) type = ZoneType.Corridor;
        else if (r < 0.78) type = ZoneType.Hall;
        else if (r < 0.9) type = ZoneType.Weird;
        else type = ZoneType.Void;
        var z = new Zone
        {
            Type = type, Id = id, S = 12, Off = 0, P = 0.7, Pz = 0.7, Pd = 0.7, Dw = 2, Split = 0.3,
            Pillar = 0, PillarSize = 1, Partial = false, H = 2.75, Rail = Hash3(zi, zj, 5) < 0.3,
        };
        switch (type)
        {
            case ZoneType.Office:
                z.S = 10 + 2 * (int)Math.Floor(r2 * 4);
                z.P = z.Pz = 0.72;
                z.Pd = 0.8;
                z.Dw = 2 + (int)Math.Floor(r3 * 2);
                z.Split = 0.45;
                z.H = r3 < 0.15 ? 2.35 : 2.75;
                break;
            case ZoneType.Maze:
                z.S = 4 + (int)Math.Floor(r2 * 3);
                z.P = z.Pz = 0.55;
                z.Pd = 0.3;
                z.Dw = 2;
                z.Split = 0;
                z.H = 2.6;
                break;
            case ZoneType.Corridor:
            {
                z.S = 4 + (int)Math.Floor(r2 * 3);
                bool ax = r3 < 0.5;
                z.P = ax ? 0.93 : 0.1;
                z.Pz = ax ? 0.1 : 0.93;
                z.Pd = 0.22;
                z.Dw = 2;
                z.Split = 0;
                z.H = 2.6;
                break;
            }
            case ZoneType.Hall:
                z.S = 10 + 2 * (int)Math.Floor(r2 * 4);
                z.P = z.Pz = 0.1;
                z.Pd = 0.5;
                z.Dw = 3;
                z.Pillar = 0.9;
                z.PillarSize = r3 < 0.5 ? 2 : 1;
                z.Split = 0.05;
                z.H = r3 < 0.35 ? 4.6 : 3.1;
                break;
            case ZoneType.Weird:
                z.S = 6 + (int)Math.Floor(r2 * 4);
                z.P = z.Pz = 0.62;
                z.Pd = 0.55;
                z.Dw = 1 + (int)Math.Floor(r3 * 4);
                z.Split = 0.6;
                z.Partial = true;
                z.H = r3 < 0.3 ? 2.2 : r3 < 0.6 ? 3.4 : 2.75;
                break;
            case ZoneType.Void:
                z.S = 22;
                z.P = z.Pz = 0.06;
                z.Pd = 0.2;
                z.Dw = 3;
                z.Pillar = 0.25;
                z.PillarSize = 2;
                z.H = r3 < 0.5 ? 6.0 : 2.75;
                break;
        }
        z.Off = (int)Math.Floor(Hash3(zi, zj, 4) * z.S);
        return z;
    }

    public Zone ZoneAt(int gx, int gz)
    {
        int wx = gx + (int)Math.Floor(14 * VNoise1(gz * 0.035, 11));
        int wz = gz + (int)Math.Floor(14 * VNoise1(gx * 0.031, 12));
        int zi = FloorDiv(wx, ZoneSize);
        int zj = FloorDiv(wz, ZoneSize);
        long key = ((long)zi << 32) | (uint)zj;
        if (zoneCache.TryGetValue(key, out var z)) return z;
        z = MakeZone(zi, zj);
        if (zoneCache.Count > 4096) zoneCache.Clear();
        zoneCache[key] = z;
        return z;
    }

    /// <summary>Is a segment of a wall line present at `local` (cells within segment 0..S)? Handles doors / partial walls.</summary>
    bool Segment(Zone z, int line, int seg, int local, int salt, double p)
    {
        int s = unchecked(z.Id + salt);
        if (Hash3(line, seg, s) >= p) return false;
        if (z.Partial)
        {
            int a = (int)Math.Floor(Hash3(line, seg, unchecked(s + 7)) * z.S * 0.6);
            int b = z.S - (int)Math.Floor(Hash3(line, seg, unchecked(s + 8)) * z.S * 0.6);
            if (local < a || local > b) return false;
        }
        if (local == 0) return true;
        if (Hash3(line, seg, unchecked(s + 2)) < z.Pd)
        {
            int room = Math.Max(1, z.S - 1 - z.Dw);
            int start = 1 + (int)Math.Floor(Hash3(line, seg, unchecked(s + 3)) * room);
            if (local >= start && local < start + z.Dw) return false;
        }
        return true;
    }

    /// <summary>Occupancy: true where the cell is wall.</summary>
    public bool Solid(int gx, int gz)
    {
        var z = ZoneAt(gx, gz);
        int S = z.S;
        int lx = Mod(gx - z.Off, S);
        int lz = Mod(gz - z.Off, S);
        int kx = FloorDiv(gx - z.Off, S);
        int kz = FloorDiv(gz - z.Off, S);
        int id = z.Id;

        if (z.Pillar > 0 && lx < z.PillarSize && lz < z.PillarSize && Hash3(kx, kz, unchecked(id + 40)) < z.Pillar) return true;

        bool onX = lx == 0; // wall line of constant x, running along z
        bool onZ = lz == 0;
        if (onX && onZ)
        {
            // crossing: solid if any of the four adjoining segments is
            return Hash3(kx, kz, unchecked(id + 10)) < z.P || Hash3(kx, kz - 1, unchecked(id + 10)) < z.P
                || Hash3(kz, kx, unchecked(id + 20)) < z.Pz || Hash3(kz, kx - 1, unchecked(id + 20)) < z.Pz;
        }
        if (onX && Segment(z, kx, kz, lz, 10, z.P)) return true;
        if (onZ && Segment(z, kz, kx, lx, 20, z.Pz)) return true;

        // inner split wall within this block
        if (z.Split > 0 && S >= 6 && Hash3(kx, kz, unchecked(id + 30)) < z.Split)
        {
            bool alongX = Hash3(kx, kz, unchecked(id + 31)) < 0.5;
            int pos = 2 + (int)Math.Floor(Hash3(kx, kz, unchecked(id + 32)) * (S - 4));
            int local = alongX ? lx : lz;
            int across = alongX ? lz : lx;
            if (across == pos && local > 0)
            {
                int dw = Math.Min(z.Dw, S - 2);
                int start = 1 + (int)Math.Floor(Hash3(kx, kz, unchecked(id + 33)) * Math.Max(1, S - 1 - dw));
                if (!(local >= start && local < start + dw)) return true;
            }
        }
        return false;
    }

    public double CeilingAt(int gx, int gz) => ZoneAt(gx, gz).H;

    // ---- ceiling light lattice (mirrored in the shaders) ----
    public static (double X, double Z) LightCenter(int i, int j) => ((i + 0.5) * LightPeriod, (j + 0.5) * LightPeriod);

    /// <summary>0 = no panel, 1 = dead panel, 2 = lit, 3 = flickering.</summary>
    public int LightKind(int i, int j)
    {
        var (x, z) = LightCenter(i, j);
        int gx = (int)Math.Floor(x / Cell);
        int gz = (int)Math.Floor(z / Cell);
        // the panel spans ±0.6 m in x: any wall under it removes it
        for (int dx = -2; dx <= 1; dx++)
            for (int dz = -1; dz <= 0; dz++)
                if (Solid(gx + dx, gz + dz)) return 0;
        if (Hash3(i, j, 51) < 0.05) return 0;
        if (Hash3(FloorDiv(i, 5), FloorDiv(j, 5), 53) < 0.08) return 1; // dead block
        if (Hash3(i, j, 52) < 0.08) return 1;
        if (Hash3(i, j, 54) < 0.07) return 3;
        return 2;
    }

    /// <summary>Find a free spot near (x,z) with clearance.</summary>
    public (double X, double Z) FindFree(double x, double z, int clear = 2)
    {
        int gx0 = (int)Math.Floor(x / Cell);
        int gz0 = (int)Math.Floor(z / Cell);
        for (int r = 0; r < 200; r++)
        {
            int n = Math.Max(1, r * 8);
            for (int a = 0; a < n; a++)
            {
                double t = (double)a / n * Math.PI * 2;
                int gx = gx0 + (int)JsRound(Math.Cos(t) * r);
                int gz = gz0 + (int)JsRound(Math.Sin(t) * r);
                bool ok = true;
                for (int dx = -clear; dx <= clear && ok; dx++)
                    for (int dz = -clear; dz <= clear && ok; dz++)
                        if (Solid(gx + dx, gz + dz)) ok = false;
                if (ok) return ((gx + 0.5) * Cell, (gz + 0.5) * Cell);
            }
        }
        return (x, z);
    }

    /// <summary>Grid ray march: free distance from (x,z) along direction (dx,dz), up to max metres.</summary>
    public double FreeDistance(double x, double z, double dx, double dz, double max = 60)
    {
        const double step = Cell * 0.5;
        for (double d = 0; d < max; d += step)
            if (Solid((int)Math.Floor((x + dx * d) / Cell), (int)Math.Floor((z + dz * d) / Cell))) return d;
        return max;
    }

    /// <summary>Small deterministic PRNG for per-chunk decisions (mulberry32, as in gen.ts).</summary>
    public Rng Rng(int a, int b, int salt) => new((uint)Math.Floor(Hash3(a, b, salt) * 4294967296));
}

public sealed class Rng(uint state)
{
    uint s = state;

    public double Next()
    {
        unchecked
        {
            s += 0x6d2b79f5;
            uint t = s;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}
