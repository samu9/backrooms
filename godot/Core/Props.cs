// Office furniture made of boxes, placed the way the Backrooms place things: sunk into the carpet,
// pushed half into a wall, hanging upside down from the ceiling, a staircase climbing into the
// tiles. Port of web/src/props.ts. The placement draws from the same rng in the same order, and
// the transforms reproduce three.js's maths operation for operation (Mat4 below), so the prop
// geometry is identical to the web's, float for float (checked by ChunkParityTests).
namespace Backrooms.Core;

public sealed record Template(double[][] Boxes, double H, double D, bool Wall = false);

public readonly record struct PropSpot(double X, double Z, string Name);

/// <summary>The bits of three.js Matrix4 / Matrix3 / Quaternion the props use, same expressions.</summary>
public sealed class Mat4
{
    /// <summary>Column-major, like three.js.</summary>
    public readonly double[] E = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>Quaternion.setFromEuler for order 'YXZ'.</summary>
    public static (double X, double Y, double Z, double W) QuatYXZ(double x, double y, double z)
    {
        double c1 = Math.Cos(x / 2), c2 = Math.Cos(y / 2), c3 = Math.Cos(z / 2);
        double s1 = Math.Sin(x / 2), s2 = Math.Sin(y / 2), s3 = Math.Sin(z / 2);
        return (s1 * c2 * c3 + c1 * s2 * s3, c1 * s2 * c3 - s1 * c2 * s3, c1 * c2 * s3 - s1 * s2 * c3, c1 * c2 * c3 + s1 * s2 * s3);
    }

    public static Mat4 Compose(V3 p, (double X, double Y, double Z, double W) q, V3 s)
    {
        var m = new Mat4();
        var te = m.E;
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double x2 = x + x, y2 = y + y, z2 = z + z;
        double xx = x * x2, xy = x * y2, xz = x * z2;
        double yy = y * y2, yz = y * z2, zz = z * z2;
        double wx = w * x2, wy = w * y2, wz = w * z2;
        te[0] = (1 - (yy + zz)) * s.X; te[1] = (xy + wz) * s.X; te[2] = (xz - wy) * s.X; te[3] = 0;
        te[4] = (xy - wz) * s.Y; te[5] = (1 - (xx + zz)) * s.Y; te[6] = (yz + wx) * s.Y; te[7] = 0;
        te[8] = (xz + wy) * s.Z; te[9] = (yz - wx) * s.Z; te[10] = (1 - (xx + yy)) * s.Z; te[11] = 0;
        te[12] = p.X; te[13] = p.Y; te[14] = p.Z; te[15] = 1;
        return m;
    }

    /// <summary>makeScale(sx, sy, sz).setPosition(cx, cy, cz).premultiply(m)</summary>
    public static Mat4 ScaleTranslateThen(double sx, double sy, double sz, double cx, double cy, double cz, Mat4 m)
    {
        var b = new Mat4();
        b.E[0] = sx; b.E[5] = sy; b.E[10] = sz;
        b.E[12] = cx; b.E[13] = cy; b.E[14] = cz;
        return Multiply(m, b);
    }

    /// <summary>a * b, summed in three.js's order (zero terms included: they decide signed zeros).</summary>
    public static Mat4 Multiply(Mat4 a, Mat4 b)
    {
        var r = new Mat4();
        double[] ae = a.E, be = b.E, te = r.E;
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                te[col * 4 + row] = ae[row] * be[col * 4] + ae[4 + row] * be[col * 4 + 1] + ae[8 + row] * be[col * 4 + 2] + ae[12 + row] * be[col * 4 + 3];
        return r;
    }

    public V3 Apply(V3 v)
    {
        var e = E;
        double w = 1 / (e[3] * v.X + e[7] * v.Y + e[11] * v.Z + e[15]);
        return new V3((e[0] * v.X + e[4] * v.Y + e[8] * v.Z + e[12]) * w, (e[1] * v.X + e[5] * v.Y + e[9] * v.Z + e[13]) * w, (e[2] * v.X + e[6] * v.Y + e[10] * v.Z + e[14]) * w);
    }

    /// <summary>Matrix3.getNormalMatrix: setFromMatrix4, invert, transpose. Column-major 3x3.</summary>
    public double[] NormalMatrix()
    {
        var me = E;
        double n11 = me[0], n21 = me[1], n31 = me[2];
        double n12 = me[4], n22 = me[5], n32 = me[6];
        double n13 = me[8], n23 = me[9], n33 = me[10];
        double t11 = n33 * n22 - n32 * n23, t12 = n32 * n13 - n33 * n12, t13 = n23 * n12 - n22 * n13;
        double det = n11 * t11 + n21 * t12 + n31 * t13;
        var te = new double[9];
        if (det == 0) return te;
        double detInv = 1 / det;
        te[0] = t11 * detInv;
        te[1] = (n31 * n23 - n33 * n21) * detInv;
        te[2] = (n32 * n21 - n31 * n22) * detInv;
        te[3] = t12 * detInv;
        te[4] = (n33 * n11 - n31 * n13) * detInv;
        te[5] = (n31 * n12 - n32 * n11) * detInv;
        te[6] = t13 * detInv;
        te[7] = (n21 * n13 - n23 * n11) * detInv;
        te[8] = (n22 * n11 - n21 * n12) * detInv;
        (te[1], te[3]) = (te[3], te[1]);
        (te[2], te[6]) = (te[6], te[2]);
        (te[5], te[7]) = (te[7], te[5]);
        return te;
    }

    public static V3 ApplyNormal(double[] e, V3 v)
    {
        var r = new V3(e[0] * v.X + e[3] * v.Y + e[6] * v.Z, e[1] * v.X + e[4] * v.Y + e[7] * v.Z, e[2] * v.X + e[5] * v.Y + e[8] * v.Z);
        double len = Math.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z);
        if (len == 0) len = 1;
        double inv = 1 / len;
        return new V3(r.X * inv, r.Y * inv, r.Z * inv);
    }
}

public static class Props
{
    public static readonly double[][] Palette =
    [
        [0.4, 0.28, 0.16], // 0 wood
        [0.56, 0.54, 0.46], // 1 beige metal
        [0.2, 0.18, 0.17], // 2 dark fabric
        [0.42, 0.42, 0.4], // 3 grey plastic
        [0.6, 0.45, 0.27], // 4 cardboard
        [0.74, 0.73, 0.68], // 5 off-white
        [0.6, 0.6, 0.6], // 6 metal
        [0.68, 0.54, 0.35], // 7 door wood
        [0.33, 0.37, 0.31], // 8 chair green
        [0.07, 0.07, 0.07], // 9 black
        [0.7, 0.8, 0.84], // 10 pale blue
        [0.55, 0.12, 0.1], // 11 faded red
    ];

    static double[][] Legs(double x, double z, double h, double w, double c) =>
    [
        [-x, h / 2, -z, w, h, w, c],
        [x, h / 2, -z, w, h, w, c],
        [-x, h / 2, z, w, h, w, c],
        [x, h / 2, z, w, h, w, c],
    ];

    static double[][] Join(params double[][][] parts) => parts.SelectMany(p => p).ToArray();

    public static readonly Dictionary<string, Template> T = new()
    {
        ["chair"] = new(Join([[0, 0.45, 0, 0.46, 0.06, 0.46, 8], [0, 0.76, -0.21, 0.44, 0.52, 0.05, 8]], Legs(0.2, 0.2, 0.42, 0.03, 6)), 1.0, 0.5),
        ["table"] = new(Join([[0, 0.73, 0, 1.4, 0.04, 0.72, 5]], Legs(0.66, 0.32, 0.71, 0.04, 6)), 0.75, 0.75),
        ["desk"] = new(
        [
            [0, 0.755, 0, 1.5, 0.04, 0.75, 0],
            [-0.73, 0.37, 0, 0.04, 0.74, 0.72, 0],
            [0.45, 0.37, 0, 0.52, 0.74, 0.7, 0],
            [0, 0.45, -0.34, 1.42, 0.55, 0.02, 0],
            [0.45, 0.6, 0.355, 0.12, 0.02, 0.02, 6],
            [0.45, 0.3, 0.355, 0.12, 0.02, 0.02, 6],
        ], 0.77, 0.75),
        ["cabinet"] = new(Join([[0, 0.66, 0, 0.46, 1.32, 0.62, 1]], Enumerable.Range(0, 4).Select(i => new double[] { 0, 0.22 + i * 0.32, 0.315, 0.12, 0.025, 0.025, 6 }).ToArray()), 1.32, 0.62),
        ["door"] = new(
        [
            [-0.48, 1.04, 0, 0.06, 2.08, 0.14, 5],
            [0.48, 1.04, 0, 0.06, 2.08, 0.14, 5],
            [0, 2.11, 0, 1.02, 0.06, 0.14, 5],
            [0, 1.02, 0, 0.9, 2.04, 0.045, 7],
            [0.36, 1.0, 0.04, 0.05, 0.05, 0.07, 6],
        ], 2.14, 0.14, Wall: true),
        ["shelf"] = new(Join(
        [
            [-0.45, 0.9, 0, 0.03, 1.8, 0.36, 1],
            [0.45, 0.9, 0, 0.03, 1.8, 0.36, 1],
            [0, 0.9, -0.17, 0.9, 1.8, 0.015, 1],
        ], Enumerable.Range(0, 5).Select(i => new double[] { 0, 0.05 + i * 0.43, 0, 0.9, 0.025, 0.36, 1 }).ToArray()), 1.8, 0.36),
        ["box"] = new([[0, 0.2, 0, 0.5, 0.4, 0.4, 4], [0, 0.4, 0, 0.5, 0.004, 0.05, 4]], 0.4, 0.4),
        ["cooler"] = new([[0, 0.5, 0, 0.32, 1.0, 0.32, 5], [0, 1.2, 0, 0.26, 0.4, 0.26, 10], [0, 0.85, 0.165, 0.05, 0.05, 0.03, 11]], 1.4, 0.32),
        ["couch"] = new([[0, 0.22, 0, 1.9, 0.44, 0.85, 2], [0, 0.62, -0.34, 1.9, 0.5, 0.18, 2], [-0.88, 0.42, 0, 0.14, 0.38, 0.85, 2], [0.88, 0.42, 0, 0.14, 0.38, 0.85, 2]], 0.85, 0.85),
        ["monitor"] = new([[0, 0.2, 0, 0.4, 0.36, 0.42, 3], [0, 0.2, 0.211, 0.32, 0.26, 0.005, 9], [0, 0.02, 0, 0.3, 0.04, 0.3, 3]], 0.4, 0.42),
        ["stairs"] = new(Enumerable.Range(0, 9).Select(i => new double[] { 0, (i + 1) * 0.09, -1.12 + i * 0.28, 1.0, (i + 1) * 0.18, 0.28, 1 }).ToArray(), 1.62, 2.5),
        ["outlet"] = new([[0, 0.3, 0, 0.08, 0.12, 0.02, 5], [-0.012, 0.32, 0.011, 0.004, 0.018, 0.003, 9], [0.012, 0.32, 0.011, 0.004, 0.018, 0.003, 9]], 0.12, 0.02, Wall: true),
        ["vent"] = new(Join([[0, 0.2, 0, 0.6, 0.4, 0.03, 5]], Enumerable.Range(0, 6).Select(i => new double[] { 0, 0.05 + i * 0.06, 0.016, 0.54, 0.012, 0.006, 3 }).ToArray()), 0.4, 0.03, Wall: true),
        ["exitsign"] = new([[0, 0.1, 0, 0.36, 0.18, 0.08, 11]], 0.2, 0.08, Wall: true),
    };

    static readonly string[] FloorSet = ["chair", "chair", "table", "desk", "cabinet", "shelf", "box", "box", "cooler", "couch", "monitor"];
    static readonly string[] WallSet = ["door", "door", "door", "desk", "cabinet", "shelf", "chair", "table", "couch", "vent", "outlet", "outlet", "exitsign"];

    // origin corner (in -0.5..0.5 box units), eu axis, ev axis, normal
    static readonly V3[][] Faces =
    [
        [new(0.5, -0.5, -0.5), new(0, 0, 1), new(0, 1, 0), new(1, 0, 0)],
        [new(-0.5, -0.5, 0.5), new(0, 0, -1), new(0, 1, 0), new(-1, 0, 0)],
        [new(-0.5, 0.5, -0.5), new(1, 0, 0), new(0, 0, 1), new(0, 1, 0)],
        [new(-0.5, -0.5, 0.5), new(1, 0, 0), new(0, 0, -1), new(0, -1, 0)],
        [new(-0.5, -0.5, 0.5), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
        [new(0.5, -0.5, -0.5), new(-1, 0, 0), new(0, 1, 0), new(0, 0, -1)],
    ];

    static readonly double[] ExitRed = [1.0, 0.25, 0.18];

    public static void Emit(GeoBuilder g, Template t, Mat4 m, double mode, double seed, bool emissive, double[][]? palette = null)
    {
        palette ??= Palette;
        var nm = m.NormalMatrix();
        foreach (var b in t.Boxes)
        {
            double cx = b[0], cy = b[1], cz = b[2], sx = b[3], sy = b[4], sz = b[5];
            int ci = (int)b[6];
            var c = emissive && ci == 11 ? ExitRed : palette[ci];
            var bm = Mat4.ScaleTranslateThen(sx, sy, sz, cx, cy, cz, m);
            foreach (var f in Faces)
            {
                V3 o = f[0], eu = f[1], ev = f[2], nn = f[3];
                var wo = bm.Apply(o);
                // edge vectors: transform endpoints and subtract
                var wu = bm.Apply(new V3(o.X + eu.X, o.Y + eu.Y, o.Z + eu.Z));
                var wv = bm.Apply(new V3(o.X + ev.X, o.Y + ev.Y, o.Z + ev.Z));
                wu = new V3(wu.X - wo.X, wu.Y - wo.Y, wu.Z - wo.Z);
                wv = new V3(wv.X - wo.X, wv.Y - wo.Y, wv.Z - wo.Z);
                var n = Mat4.ApplyNormal(nm, nn);
                double su = Math.Abs(eu.X * sx + eu.Y * sy + eu.Z * sz);
                double sv = Math.Abs(ev.X * sx + ev.Y * sy + ev.Z * sz);
                // dissolve coordinate: normalised template height of each corner
                var ys = new double[4];
                for (int k = 0; k < 4; k++)
                {
                    int u = k == 1 || k == 2 ? 1 : 0;
                    int v = k >= 2 ? 1 : 0;
                    double ly = cy + sy * (o.Y + eu.Y * u + ev.Y * v);
                    ys[k] = Math.Min(1, Math.Max(0, ly / t.H));
                }
                g.Quad(wo, wu, wv, n, c, 3, mode, su, sv, seed, ys);
            }
        }
    }

    static readonly (int dx, int dz)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    static readonly V3 One = new(1, 1, 1);

    /// <summary>The prop geometry for one chunk (chunk-local coordinates), and where each prop went.</summary>
    public static GeoBuilder Build(Gen gen, int ci, int cj, List<PropSpot>? spots = null)
    {
        const int N = Gen.ChunkCells;
        const double CELL = Gen.Cell;
        var g = new GeoBuilder();
        var r = gen.Rng(ci, cj, 777);
        int gx0 = ci * N, gz0 = cj * N;
        int count = 12 + (int)Math.Floor(r.Next() * 12);
        for (int k = 0; k < count; k++)
        {
            int lx = 2 + (int)Math.Floor(r.Next() * (N - 4));
            int lz = 2 + (int)Math.Floor(r.Next() * (N - 4));
            int gx = gx0 + lx, gz = gz0 + lz;
            if (gen.Solid(gx, gz)) continue;
            double H = gen.CeilingAt(gx, gz);
            // nearest wall within 3 cells
            (int dx, int dz)? wall = null;
            int wallDist = 0;
            for (int d = 1; d <= 3 && wall == null; d++)
            {
                int start = (int)Math.Floor(r.Next() * 4);
                for (int q = 0; q < 4; q++)
                {
                    var (dx, dz) = Dirs[(start + q) % 4];
                    if (gen.Solid(gx + dx * d, gz + dz * d))
                    {
                        wall = (dx, dz);
                        wallDist = d;
                        break;
                    }
                }
            }
            double roll = r.Next();
            double modeR = r.Next();
            double mode = modeR < 0.35 ? 0 : modeR < 0.7 ? 1 : modeR < 0.9 ? 2 : 3;
            double seed = r.Next();
            double cx = (lx + 0.5) * CELL;
            double cz = (lz + 0.5) * CELL;
            double ex = 0, ey = 0, ez = 0; // Euler, order YXZ
            double px = cx, py = 0, pz = cz;
            string name;

            if (wall is { } w && roll < 0.5)
            {
                name = WallSet[(int)Math.Floor(r.Next() * WallSet.Length)];
                var t = T[name];
                // face away from the wall; push back so the prop sinks into it
                var (dx, dz) = w;
                // negate as doubles: in JS -0 is a value, and atan2(-0, -1) is -π, not π
                double yaw = Math.Atan2(-(double)dx, -(double)dz);
                double wallFace = (wallDist - 0.5) * CELL; // distance from cell centre to wall surface
                double sink = t.Wall ? t.D * (0.1 + 0.5 * r.Next()) : t.D * (0.2 + 0.6 * r.Next());
                double back = wallFace - t.D / 2 + sink;
                px = cx + dx * back;
                pz = cz + dz * back;
                double slide = (r.Next() - 0.5) * 0.8;
                px += -dz * slide;
                pz += dx * slide;
                (ex, ey, ez) = (0, yaw, 0);
                double alt = r.Next();
                if (name is "outlet" or "vent" or "exitsign")
                {
                    py = name == "vent" ? H - 0.6 - r.Next() * 0.6 : name == "exitsign" ? H - 0.35 : r.Next() < 0.7 ? 0 : 0.6 + r.Next() * 1.0;
                }
                else if (alt < 0.25)
                {
                    // glued flat against the wall, rotated onto its back
                    (ex, ey, ez) = (-Math.PI / 2, yaw, 0);
                    py = 0.3 + r.Next() * 0.8;
                }
                else if (alt < 0.45) py = 0.5 + r.Next() * 1.2; // floating on the wall
                else if (alt < 0.6 && name == "door") py = -0.4 - r.Next() * 0.8; // door half sunk into the floor
                else if (alt < 0.7) py = H - t.H * (0.4 + 0.4 * r.Next()); // hanging from the ceiling, pushed into the wall
            }
            else if (roll < 0.62)
            {
                name = FloorSet[(int)Math.Floor(r.Next() * FloorSet.Length)];
                var t = T[name];
                py = -t.H * (0.2 + 0.55 * r.Next());
                ex = (r.Next() - 0.5) * 0.5;
                ey = r.Next() * Math.PI * 2;
                ez = (r.Next() - 0.5) * 0.5;
            }
            else if (roll < 0.74)
            {
                name = FloorSet[(int)Math.Floor(r.Next() * FloorSet.Length)];
                var t = T[name];
                // upside down from the ceiling
                ex = Math.PI + (r.Next() - 0.5) * 0.3;
                ey = r.Next() * Math.PI * 2;
                ez = (r.Next() - 0.5) * 0.2;
                py = H + t.H * (0.15 + 0.4 * r.Next());
            }
            else if (roll < 0.8)
            {
                // a staircase into the ceiling: stretched vertically to reach it
                ey = Math.Floor(r.Next() * 4) * (Math.PI / 2);
                double sy = (H + 0.25) / 1.62;
                var sm = Mat4.Compose(new V3(cx, 0, cz), Mat4.QuatYXZ(0, ey, 0), new V3(1, sy, 1));
                Emit(g, T["stairs"], sm, mode == 1 ? 1 : mode, seed, false);
                continue;
            }
            else
            {
                // "normal" placement, a little crooked; small clusters
                name = FloorSet[(int)Math.Floor(r.Next() * FloorSet.Length)];
                ey = r.Next() * Math.PI * 2;
                if (name is "table" or "desk")
                {
                    // a ring of chairs, one of them sunk too deep
                    int n = 2 + (int)Math.Floor(r.Next() * 3);
                    for (int c = 0; c < n; c++)
                    {
                        double a = (double)c / n * Math.PI * 2 + r.Next() * 0.4;
                        var cm = Mat4.Compose(new V3(cx + Math.Sin(a) * 0.85, c == 0 ? -0.3 : 0, cz + Math.Cos(a) * 0.85), Mat4.QuatYXZ(0, a + Math.PI, 0), One);
                        Emit(g, T["chair"], cm, mode, r.Next(), false);
                    }
                }
            }
            var pm = Mat4.Compose(new V3(px, py, pz), Mat4.QuatYXZ(ex, ey, ez), One);
            Emit(g, T[name], pm, name == "exitsign" ? 4 : mode, seed, name == "exitsign");
            spots?.Add(new PropSpot(px + ci * N * CELL, pz + cj * N * CELL, name));
        }
        return g;
    }
}
