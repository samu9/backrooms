// Geometry in plain arrays, the way web/src/props.ts GeoBuilder builds it: positions, normals,
// colours and the two attribute vectors the level/prop shaders read (aA, aB). Maths is done in
// double, like the JS, and stored as float, like Float32BufferAttribute: same bits.
namespace Backrooms.Core;

public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

public sealed class GeoBuilder
{
    public readonly List<float> Pos = [];
    public readonly List<float> Nrm = [];
    public readonly List<float> Col = [];
    /// <summary>kind, mode, u, v</summary>
    public readonly List<float> A = [];
    /// <summary>sizeU, sizeV, seed, dissolve</summary>
    public readonly List<float> B = [];
    /// <summary>Triangles, counter-clockwise front faces (three.js convention).</summary>
    public readonly List<int> Idx = [];

    public int VertexCount => Pos.Count / 3;

    static readonly (int U, int V)[] Corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
    public static readonly double[] NoColor = [0, 0, 0];
    public static readonly double[] NoDissolve = [0, 0, 0, 0];

    /// <summary>Quad from origin o with edges eu, ev. Winding chosen so the face points along n.</summary>
    public void Quad(V3 o, V3 eu, V3 ev, V3 n, double[] col, double kind, double mode, double su, double sv, double seed, double[] dis)
    {
        int b = VertexCount;
        for (int k = 0; k < 4; k++)
        {
            var (u, v) = Corners[k];
            Pos.Add((float)(o.X + eu.X * u + ev.X * v));
            Pos.Add((float)(o.Y + eu.Y * u + ev.Y * v));
            Pos.Add((float)(o.Z + eu.Z * u + ev.Z * v));
            Nrm.Add((float)n.X);
            Nrm.Add((float)n.Y);
            Nrm.Add((float)n.Z);
            Col.Add((float)col[0]);
            Col.Add((float)col[1]);
            Col.Add((float)col[2]);
            A.Add((float)kind);
            A.Add((float)mode);
            A.Add((float)(u * su));
            A.Add((float)(v * sv));
            B.Add((float)su);
            B.Add((float)sv);
            B.Add((float)seed);
            B.Add((float)dis[k]);
        }
        if (V3.Dot(V3.Cross(eu, ev), n) >= 0) Idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        else Idx.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
    }
}
