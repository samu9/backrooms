// The data half of web/src/chunk.ts buildChunk: everything a chunk needs, with no engine types,
// so it can be built on a worker thread and tested. Game/ChunkManager turns it into a mesh,
// textures and a material on the main thread.
//
// A chunk is 64x64 occupancy cells (32 m). It becomes:
//   - one mesh for the floor + greedy-merged ceilings + merged wall runs (soffits where heights change)
//   - a 96x96 RGBA occupancy/height texture (chunk + 16-cell border) for soft shadows and AO
//   - a 16x16 RGBA light-state texture for the ceiling-panel lattice that covers it
// Checked against the web builder by Core.Tests/ChunkParityTests.cs.
namespace Backrooms.Core;

public readonly record struct LightInfo(double X, double Z, double Y, int Kind);

public sealed class ChunkData
{
    public const int LightTex = 16;

    public int Ci { get; private init; }
    public int Cj { get; private init; }
    /// <summary>World position of the chunk's corner (x, z).</summary>
    public double OriginX { get; private init; }
    public double OriginZ { get; private init; }
    /// <summary>Lattice index of the light texture's first texel.</summary>
    public int LightBaseI { get; private init; }
    public int LightBaseJ { get; private init; }
    /// <summary>Tex x Tex RGBA8: R occupancy, G ceiling height / HScale, B chair rail, A 255.</summary>
    public required byte[] Occupancy { get; init; }
    /// <summary>16 x 16 RGBA8: R kind / 3, G ceiling height / HScale, B tint hash, A 255.</summary>
    public required byte[] Lights { get; init; }
    /// <summary>Panels (kind > 0) whose centre is inside this chunk.</summary>
    public required List<LightInfo> LightList { get; init; }
    /// <summary>Floor, ceilings and walls, in chunk-local coordinates.</summary>
    public required GeoBuilder Level { get; init; }
    /// <summary>Furniture, in chunk-local coordinates (may be empty).</summary>
    public required GeoBuilder Props { get; init; }
    /// <summary>Where each prop went (world coordinates), as the web's chunk.props.</summary>
    public required List<PropSpot> PropSpots { get; init; }

    static byte Byte(double v) => (byte)Gen.JsRound(v);

    public static ChunkData Build(Gen gen, int ci, int cj)
    {
        const int T = Gen.Tex, B = Gen.Border, N = Gen.ChunkCells;
        const double CELL = Gen.Cell;
        int gx0 = ci * N - B;
        int gz0 = cj * N - B;

        // occupancy + ceiling height over the bordered window
        var solid = new bool[T * T];
        var hGrid = new double[T * T];
        var occ = new byte[T * T * 4];
        for (int z = 0; z < T; z++)
            for (int x = 0; x < T; x++)
            {
                int i = z * T + x;
                bool s = gen.Solid(gx0 + x, gz0 + z);
                double h = gen.CeilingAt(gx0 + x, gz0 + z);
                solid[i] = s;
                hGrid[i] = h;
                occ[i * 4] = s ? (byte)255 : (byte)0;
                occ[i * 4 + 1] = Byte(h / Gen.HScale * 255);
                occ[i * 4 + 2] = gen.ZoneAt(gx0 + x, gz0 + z).Rail ? (byte)255 : (byte)0;
                occ[i * 4 + 3] = 255;
            }
        bool S(int lx, int lz) => solid[(lz + B) * T + lx + B];
        // the stored height is quantised; geometry must use the same value the shader decodes
        double Hq(int lx, int lz) => Gen.JsRound(hGrid[(lz + B) * T + lx + B] / Gen.HScale * 255) / 255 * Gen.HScale;

        // light lattice covering the chunk with a margin of two periods
        double ox = ci * Gen.Chunk;
        double oz = cj * Gen.Chunk;
        int li0 = (int)Math.Floor((ox - 2 * Gen.LightPeriod) / Gen.LightPeriod);
        int lj0 = (int)Math.Floor((oz - 2 * Gen.LightPeriod) / Gen.LightPeriod);
        var lData = new byte[LightTex * LightTex * 4];
        var lights = new List<LightInfo>();
        for (int j = 0; j < LightTex; j++)
            for (int i = 0; i < LightTex; i++)
            {
                int li = li0 + i, lj = lj0 + j;
                int k = gen.LightKind(li, lj);
                double cx = (li + 0.5) * Gen.LightPeriod;
                double cz = (lj + 0.5) * Gen.LightPeriod;
                double h = gen.CeilingAt((int)Math.Floor(cx / CELL), (int)Math.Floor(cz / CELL));
                int o = (j * LightTex + i) * 4;
                lData[o] = Byte(k / 3.0 * 255);
                lData[o + 1] = Byte(h / Gen.HScale * 255);
                lData[o + 2] = Byte(gen.Hash3(li, lj, 80) * 255);
                lData[o + 3] = 255;
                if (k > 0 && cx >= ox && cx < ox + Gen.Chunk && cz >= oz && cz < oz + Gen.Chunk) lights.Add(new LightInfo(cx, cz, h, k));
            }

        // ---- level geometry ----
        var g = new GeoBuilder();
        var none = GeoBuilder.NoColor;
        var z4 = GeoBuilder.NoDissolve;
        g.Quad(new V3(0, 0, 0), new V3(Gen.Chunk, 0, 0), new V3(0, 0, Gen.Chunk), new V3(0, 1, 0), none, 0, 0, 0, 0, 0, z4);

        // ceiling: greedy rectangles of equal height over open cells
        var used = new bool[N * N];
        for (int z = 0; z < N; z++)
            for (int x = 0; x < N; x++)
            {
                if (used[z * N + x] || S(x, z)) continue;
                double h = Hq(x, z);
                int w = 1;
                while (x + w < N && !used[z * N + x + w] && !S(x + w, z) && Hq(x + w, z) == h) w++;
                int d = 1;
                while (z + d < N)
                {
                    bool ok = true;
                    for (int k = 0; k < w && ok; k++)
                        if (used[(z + d) * N + x + k] || S(x + k, z + d) || Hq(x + k, z + d) != h) ok = false;
                    if (!ok) break;
                    d++;
                }
                for (int dz = 0; dz < d; dz++)
                    for (int k = 0; k < w; k++) used[(z + dz) * N + x + k] = true;
                g.Quad(new V3(x * CELL, h, z * CELL), new V3(w * CELL, 0, 0), new V3(0, 0, d * CELL), new V3(0, -1, 0), none, 1, 0, 0, 0, 0, z4);
            }

        // vertical faces on every cell boundary owned by this chunk, merged into runs.
        // Boundary between a = (x-1) and b = (x) along x, same for z. sign = +1: solid b, open a,
        // the wall surface faces a, normal = -axis.
        Face? FaceAt(int ax, int az, int bx, int bz)
        {
            bool sa = S(ax, az), sb = S(bx, bz);
            if (sa && sb) return null;
            if (sa) return new Face(-1, 0, Hq(bx, bz));
            if (sb) return new Face(1, 0, Hq(ax, az));
            double ha = Hq(ax, az), hb = Hq(bx, bz);
            if (ha == hb) return null;
            return ha < hb ? new Face(-1, ha, hb) : new Face(1, hb, ha);
        }
        for (int x = 0; x < N; x++)
        {
            int z = 0;
            while (z < N)
            {
                var f = FaceAt(x - 1, z, x, z);
                if (f is not { } face)
                {
                    z++;
                    continue;
                }
                int len = 1;
                while (z + len < N && FaceAt(x - 1, z + len, x, z + len) == face) len++;
                g.Quad(new V3(x * CELL, face.Y0, z * CELL), new V3(0, 0, len * CELL), new V3(0, face.Y1 - face.Y0, 0), new V3(-face.Sign, 0, 0), none, 2, 0, 0, 0, 0, z4);
                z += len;
            }
        }
        for (int z = 0; z < N; z++)
        {
            int x = 0;
            while (x < N)
            {
                var f = FaceAt(x, z - 1, x, z);
                if (f is not { } face)
                {
                    x++;
                    continue;
                }
                int len = 1;
                while (x + len < N && FaceAt(x + len, z - 1, x + len, z) == face) len++;
                g.Quad(new V3(x * CELL, face.Y0, z * CELL), new V3(len * CELL, 0, 0), new V3(0, face.Y1 - face.Y0, 0), new V3(0, 0, -face.Sign), none, 2, 0, 0, 0, 0, z4);
                x += len;
            }
        }

        var spots = new List<PropSpot>();
        var props = Backrooms.Core.Props.Build(gen, ci, cj, spots);

        return new ChunkData
        {
            Ci = ci, Cj = cj, OriginX = ox, OriginZ = oz, LightBaseI = li0, LightBaseJ = lj0,
            Occupancy = occ, Lights = lData, LightList = lights, Level = g, Props = props, PropSpots = spots,
        };
    }

    readonly record struct Face(int Sign, double Y0, double Y1);
}
