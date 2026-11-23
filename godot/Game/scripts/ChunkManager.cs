using System.Diagnostics;
using Backrooms.Core;
using Godot;

namespace Backrooms;

/// <summary>
/// Keeps the chunks around the player built, like ChunkManager in web/src/chunk.ts. The difference:
/// ChunkData is built on worker threads, and only the upload (mesh, textures, material) happens on
/// the main thread, within a per-frame time budget.
/// </summary>
public partial class ChunkManager : Node3D
{
    public sealed class Loaded
    {
        public required ChunkData Data;
        public required MeshInstance3D Node;
        public required ImageTexture Occupancy;
        public required ImageTexture Lights;
    }

    public int Radius = 2;
    public double BudgetMs = 4;
    public readonly Dictionary<(int, int), Loaded> Chunks = [];

    readonly Dictionary<(int, int), Task<ChunkData>> pending = [];
    readonly int maxInFlight = Math.Max(1, System.Environment.ProcessorCount - 1);
    Gen gen = null!;
    public Gen Gen => gen;
    Shader shader = null!;
    Shader? propShader;

    public void Init(Gen g, Shader levelShader, Shader? props)
    {
        gen = g;
        shader = levelShader;
        propShader = props;
    }

    public IEnumerable<PropSpot> AllPropSpots() => Chunks.Values.SelectMany(c => c.Data.PropSpots);

    static (int, int) ChunkAt(double x, double z) => ((int)Math.Floor(x / Gen.Chunk), (int)Math.Floor(z / Gen.Chunk));

    public Loaded? At(double x, double z) => Chunks.GetValueOrDefault(ChunkAt(x, z));

    public IEnumerable<LightInfo> AllLights() => Chunks.Values.SelectMany(c => c.Data.LightList);

    /// <summary>Schedules missing chunks, uploads finished ones, drops far ones. Returns how many are still missing.</summary>
    public int UpdateAround(double x, double z)
    {
        var (ci, cj) = ChunkAt(x, z);
        var want = new List<(double d, int i, int j)>();
        for (int j = cj - Radius; j <= cj + Radius; j++)
            for (int i = ci - Radius; i <= ci + Radius; i++)
            {
                if (Chunks.ContainsKey((i, j))) continue;
                double cx = (i + 0.5) * Gen.Chunk - x, cz = (j + 0.5) * Gen.Chunk - z;
                want.Add((cx * cx + cz * cz, i, j));
            }
        want.Sort((a, b) => a.d.CompareTo(b.d));

        foreach (var (_, i, j) in want)
        {
            if (pending.Count >= maxInFlight) break;
            if (pending.ContainsKey((i, j))) continue;
            int ii = i, jj = j;
            pending[(i, j)] = Task.Run(() => ChunkData.Build(gen, ii, jj));
        }

        // upload what's ready, nearest first, at least one per frame
        var sw = Stopwatch.StartNew();
        int uploaded = 0;
        foreach (var (_, i, j) in want)
        {
            if (!pending.TryGetValue((i, j), out var task) || !task.IsCompleted) continue;
            if (uploaded > 0 && sw.Elapsed.TotalMilliseconds > BudgetMs) break;
            pending.Remove((i, j));
            if (task.IsFaulted)
            {
                GD.PushError($"chunk ({i}, {j}) failed: {task.Exception}");
                continue;
            }
            Chunks[(i, j)] = Upload(task.Result);
            uploaded++;
        }
        // finished builds that drifted out of range are dropped without uploading
        foreach (var key in pending.Keys.ToList())
            if (pending[key].IsCompleted && !InRange(key, ci, cj, Radius)) pending.Remove(key);

        foreach (var key in Chunks.Keys.ToList())
            if (!InRange(key, ci, cj, Radius + 1))
            {
                Chunks[key].Node.QueueFree();
                Chunks.Remove(key);
            }
        return want.Count - uploaded;
    }

    /// <summary>Blocks until everything within the radius is built (first frame, screenshots).</summary>
    public void BuildAllNow(double x, double z)
    {
        double budget = BudgetMs;
        BudgetMs = double.MaxValue;
        while (UpdateAround(x, z) > 0) Task.WaitAll([.. pending.Values]);
        BudgetMs = budget;
    }

    static bool InRange((int i, int j) k, int ci, int cj, int r) => Math.Abs(k.i - ci) <= r && Math.Abs(k.j - cj) <= r;

    Loaded Upload(ChunkData c)
    {
        var occ = ImageTexture.CreateFromImage(Image.CreateFromData(Gen.Tex, Gen.Tex, false, Image.Format.Rgba8, c.Occupancy));
        var lights = ImageTexture.CreateFromImage(Image.CreateFromData(ChunkData.LightTex, ChunkData.LightTex, false, Image.Format.Rgba8, c.Lights));
        ShaderMaterial Material(Shader sh)
        {
            var m = new ShaderMaterial { Shader = sh };
            m.SetShaderParameter("occ_tex", occ);
            m.SetShaderParameter("lights_tex", lights);
            m.SetShaderParameter("chunk_origin", new Vector2((float)c.OriginX, (float)c.OriginZ));
            m.SetShaderParameter("light_base", new Vector2(c.LightBaseI, c.LightBaseJ));
            return m;
        }
        var mat = Material(shader);
        var node = new MeshInstance3D
        {
            Name = $"chunk {c.Ci},{c.Cj}",
            Mesh = ToMesh(c.Level),
            MaterialOverride = mat,
            Position = new Vector3((float)c.OriginX, 0, (float)c.OriginZ),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (propShader != null && c.Props.VertexCount > 0)
        {
            // a second draw call per chunk, lit with the same textures
            node.AddChild(new MeshInstance3D
            {
                Name = "props",
                Mesh = ToMesh(c.Props),
                MaterialOverride = Material(propShader),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        AddChild(node);
        return new Loaded { Data = c, Node = node, Occupancy = occ, Lights = lights };
    }

    /// <summary>GeoBuilder arrays as a Godot surface: aCol → COLOR, aA → CUSTOM0, aB → CUSTOM1.</summary>
    public static ArrayMesh ToMesh(GeoBuilder g)
    {
        int n = g.VertexCount;
        var pos = new Vector3[n];
        var nrm = new Vector3[n];
        var col = new Color[n];
        for (int v = 0; v < n; v++)
        {
            pos[v] = new Vector3(g.Pos[v * 3], g.Pos[v * 3 + 1], g.Pos[v * 3 + 2]);
            nrm[v] = new Vector3(g.Nrm[v * 3], g.Nrm[v * 3 + 1], g.Nrm[v * 3 + 2]);
            col[v] = new Color(g.Col[v * 3], g.Col[v * 3 + 1], g.Col[v * 3 + 2]);
        }
        // three.js treats counter-clockwise triangles as front faces, Godot clockwise: flip them
        var idx = new int[g.Idx.Count];
        for (int t = 0; t < idx.Length; t += 3)
        {
            idx[t] = g.Idx[t];
            idx[t + 1] = g.Idx[t + 2];
            idx[t + 2] = g.Idx[t + 1];
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.Normal] = nrm;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Custom0] = g.A.ToArray();
        arrays[(int)Mesh.ArrayType.Custom1] = g.B.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var flags = (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
            | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        return mesh;
    }
}
