using Backrooms.Core;
using Backrooms.UI;
using Godot;

namespace Backrooms;

/// <summary>
/// Other players, from web/src/avatars.ts: a faceless office-worker figure built from boxes, lit
/// with the prop shader and the textures of whichever chunk it stands in (so it gets the panel
/// lighting, wall shadows and dark zones like everything else), a name tag, a walk cycle and
/// footsteps. In a hunt an entity swaps to the tall figure.
/// </summary>
public partial class Avatars : Node3D
{
    sealed class Avatar
    {
        public required Node3D Root;
        public required MeshInstance3D Mesh;
        public required ShaderMaterial Mat;
        public required Label3D Tag;
        public required string Color;
        public double StepDist, LastX = double.NaN, LastZ, Phase;
        public bool Entity;
    }

    readonly Dictionary<int, Avatar> list = [];
    ChunkManager chunks = null!;
    Shader propShader = null!;
    /// <summary>A footstep someone else made: x, z, heavy (an entity).</summary>
    public event Action<double, double, bool>? Footstep;

    public void Init(ChunkManager c, Shader prop)
    {
        chunks = c;
        propShader = prop;
    }

    static readonly Mat4 Turn = Mat4.Compose(new V3(0, 0, 0), Mat4.QuatYXZ(0, Math.PI, 0), new V3(1, 1, 1));

    /// <summary>The template faces +z; players face -z at yaw 0, so it's turned around.</summary>
    public static ArrayMesh Figure(string color)
    {
        // shirt colour pulled towards the beige of the place, so nobody looks like a game character
        var c = Color.FromHtml(color).SrgbToLinear(); // three.js Color(hex) is linear
        double[][] palette = [[c.R * 0.55 + 0.25, c.G * 0.55 + 0.23, c.B * 0.55 + 0.17], [0.16, 0.15, 0.14], [0.66, 0.55, 0.45], [0.1, 0.09, 0.08]];
        double[][] boxes =
        [
            [-0.1, 0.43, 0, 0.15, 0.86, 0.17, 1], [0.1, 0.43, 0, 0.15, 0.86, 0.17, 1], // legs
            [-0.1, 0.03, 0.04, 0.15, 0.06, 0.26, 3], [0.1, 0.03, 0.04, 0.15, 0.06, 0.26, 3], // shoes
            [0, 1.17, 0, 0.42, 0.62, 0.24, 0], // torso
            [-0.27, 1.13, 0, 0.1, 0.62, 0.12, 0], [0.27, 1.13, 0, 0.1, 0.62, 0.12, 0], // arms
            [-0.27, 0.78, 0, 0.08, 0.1, 0.09, 2], [0.27, 0.78, 0, 0.08, 0.1, 0.09, 2], // hands
            [0, 1.52, 0, 0.1, 0.1, 0.1, 2], // neck
            [0, 1.67, 0, 0.21, 0.25, 0.23, 2], // head (no face)
            [0, 1.81, -0.01, 0.22, 0.05, 0.24, 3], // hair
        ];
        var g = new GeoBuilder();
        Props.Emit(g, new Template(boxes, 1.85, 0.3), Turn, 0, 0.5, false, palette);
        return ChunkManager.ToMesh(g);
    }

    /// <summary>The entity: too tall, too thin, arms hanging past its knees, no face.</summary>
    public static ArrayMesh EntityFigure()
    {
        double[][] palette = [[0.035, 0.032, 0.03], [0.06, 0.055, 0.05], [0.02, 0.02, 0.02]];
        double[][] boxes =
        [
            [-0.08, 0.62, 0, 0.09, 1.24, 0.1, 0], [0.08, 0.62, 0, 0.09, 1.24, 0.1, 0], // legs
            [0, 1.62, 0.02, 0.3, 0.78, 0.15, 1], // narrow torso, hunched forward a little
            [-0.2, 1.22, 0.04, 0.06, 1.25, 0.07, 0], [0.2, 1.22, 0.04, 0.06, 1.25, 0.07, 0], // arms to below the knees
            [-0.2, 0.55, 0.05, 0.05, 0.16, 0.04, 2], [0.2, 0.55, 0.05, 0.05, 0.16, 0.04, 2], // long fingers
            [0, 2.08, 0.06, 0.06, 0.18, 0.06, 0], // neck
            [0, 2.28, 0.1, 0.16, 0.3, 0.17, 1], // small, long head
        ];
        var g = new GeoBuilder();
        Props.Emit(g, new Template(boxes, 2.45, 0.2), Turn, 0, 0.31, false, palette);
        return ChunkManager.ToMesh(g);
    }

    public ShaderMaterial LitMaterial() => new() { Shader = propShader };

    /// <summary>Light a material as if it stood in the chunk at (x, z). False when that chunk isn't loaded.</summary>
    public bool Light(ShaderMaterial mat, double x, double z)
    {
        if (chunks.At(x, z) is not { } c) return false;
        mat.SetShaderParameter("occ_tex", c.Occupancy);
        mat.SetShaderParameter("lights_tex", c.Lights);
        mat.SetShaderParameter("chunk_origin", new Vector2((float)c.Data.OriginX, (float)c.Data.OriginZ));
        mat.SetShaderParameter("light_base", new Vector2(c.Data.LightBaseI, c.Data.LightBaseJ));
        return true;
    }

    public void Add(Peer p)
    {
        var mat = LitMaterial();
        var mesh = new MeshInstance3D { Mesh = Figure(p.Color), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var tag = new Label3D
        {
            Text = p.Name, Font = Ui.Mono, FontSize = 28, PixelSize = 0.0036f, Position = new Vector3(0, 2.15f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = Ui.Paper, OutlineModulate = new Color(0, 0, 0, 0.45f), OutlineSize = 12,
        };
        // the colour bar at the left of the web's name tag
        tag.AddChild(new Label3D
        {
            Text = "▌", Font = Ui.Mono, FontSize = 28, PixelSize = 0.0036f, Position = new Vector3(-(p.Name.Length * 0.5f + 1.2f) * 28 * 0.0036f * 0.6f, 0, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = Color.FromHtml(p.Color), OutlineSize = 0,
        });
        var root = new Node3D { Name = $"peer {p.Id}", Visible = false };
        root.AddChild(mesh);
        root.AddChild(tag);
        AddChild(root);
        list[p.Id] = new Avatar { Root = root, Mesh = mesh, Mat = mat, Tag = tag, Color = p.Color };
    }

    public bool IsShown(int id) => list.TryGetValue(id, out var a) && a.Root.Visible;

    public void Remove(int id)
    {
        if (!list.Remove(id, out var a)) return;
        a.Root.QueueFree();
    }

    public void Update(IEnumerable<Peer> peers, double camX, double camZ, Func<int, bool>? isEntity = null)
    {
        double now = Time.GetTicksMsec();
        foreach (var p in peers)
        {
            if (!list.TryGetValue(p.Id, out var a)) continue;
            bool ent = isEntity?.Invoke(p.Id) ?? false;
            if (ent != a.Entity)
            {
                a.Entity = ent;
                a.Mesh.Mesh = ent ? EntityFigure() : Figure(a.Color);
            }
            bool lit = p.Visible && Light(a.Mat, p.X, p.Z);
            a.Root.Visible = lit;
            // a phasing entity only half exists
            if (a.Entity && (p.F & 2) != 0 && GD.Randf() < 0.55f) a.Root.Visible = false;
            a.Tag.Visible = !a.Entity;
            if (!lit) continue;
            // walk cycle: a bob and a sway, driven by how far they actually moved
            double moved = double.IsNaN(a.LastX) ? 0 : Math.Sqrt((p.X - a.LastX) * (p.X - a.LastX) + (p.Z - a.LastZ) * (p.Z - a.LastZ));
            (a.LastX, a.LastZ) = (p.X, p.Z);
            a.Phase += moved / (0.62 + 0.11 * p.V);
            double walking = Math.Min(1, p.V / 1.7), running = Math.Clamp((p.V - 1.7) / 3.9, 0, 1);
            if (a.Entity)
            {
                // no bob: it glides, and now and then it is somewhere slightly else for a frame
                double j = GD.Randf() < 0.04f ? 0.12 : 0;
                a.Root.Position = new Vector3((float)(p.X + (GD.Randf() - 0.5) * j), 0, (float)(p.Z + (GD.Randf() - 0.5) * j));
                a.Root.Rotation = new Vector3(0.1f, (float)(p.Yaw + (GD.Randf() - 0.5) * j), (float)(Math.Sin(now * 0.0007 + p.Id) * 0.05));
            }
            else
            {
                a.Root.Position = new Vector3((float)p.X, (float)(Math.Abs(Math.Sin(a.Phase * Math.PI)) * (0.035 + 0.05 * running) * walking), (float)p.Z);
                // runners lean forward
                a.Root.Rotation = new Vector3((float)(-0.16 * running), (float)p.Yaw, (float)(Math.Sin(a.Phase * Math.PI) * 0.025 * walking));
            }
            a.StepDist += moved;
            if (a.StepDist > (a.Entity ? 1.3 : 0.62 + 0.11 * p.V))
            {
                a.StepDist = 0;
                if ((p.X - camX) * (p.X - camX) + (p.Z - camZ) * (p.Z - camZ) < 40 * 40) Footstep?.Invoke(p.X, p.Z, a.Entity);
            }
            // name tags fade out with distance
            double dist = Math.Sqrt((p.X - camX) * (p.X - camX) + (p.Z - camZ) * (p.Z - camZ));
            float alpha = (float)Math.Clamp((22 - dist) / 8, 0, 1);
            a.Tag.Modulate = new Color(Ui.Paper, alpha);
            a.Tag.OutlineModulate = new Color(0, 0, 0, 0.45f * alpha);
            ((Label3D)a.Tag.GetChild(0)).Modulate = new Color(Color.FromHtml(a.Color), alpha);
        }
    }
}
