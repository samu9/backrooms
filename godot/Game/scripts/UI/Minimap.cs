using Backrooms.Core;
using Godot;

namespace Backrooms.UI;

/// <summary>
/// The round fog-of-war minimap of web/src/minimap.ts, rotated so the way you face is up. The disc
/// clips its child (Godot's clip_children), which draws the discovered tiles; an overlay on top
/// draws the player, the rim, marks (pins inside, arrows on the rim outside), the exit and others.
/// </summary>
public partial class Minimap : Control
{
    public const float Px = 190;
    /// <summary>Pixels per metre, defined for a 200 px map (the web's scale).</summary>
    public float MapScale = 4;
    public double X, Z, Yaw;

    MapTextures tiles = null!;
    MapOverlay overlay = null!;
    Control disc = null!, world = null!, top = null!;

    public void Init(MapTextures t, MapOverlay o)
    {
        tiles = t;
        overlay = o;
        CustomMinimumSize = Size = new Vector2(Px, Px);
        MouseFilter = MouseFilterEnum.Ignore;
        disc = new Control { ClipChildren = ClipChildrenMode.AndDraw, Size = Size, MouseFilter = MouseFilterEnum.Ignore };
        disc.Draw += () => disc.DrawCircle(new Vector2(Px / 2, Px / 2), Px / 2 - 1, new Color(14 / 255f, 12 / 255f, 5 / 255f, 0.55f));
        world = new Control { Size = Size, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Nearest };
        world.Draw += DrawWorld;
        top = new Control { Size = Size, MouseFilter = MouseFilterEnum.Ignore };
        top.Draw += DrawTop;
        disc.AddChild(world);
        AddChild(disc);
        AddChild(top);
        Modulate = new Color(1, 1, 1, 0.92f);
    }

    public void Refresh(double x, double z, double yaw)
    {
        (X, Z, Yaw) = (x, z, yaw);
        world.QueueRedraw();
        top.QueueRedraw();
    }

    float K => MapScale * Px / 200;

    void DrawWorld()
    {
        float k = K;
        // world -> map: centre on the player, forward (-sin yaw, -cos yaw) points up
        var t = new Transform2D(0, new Vector2(Px / 2, Px / 2)) * new Transform2D((float)Yaw, Vector2.Zero) * Transform2D.Identity.Scaled(new Vector2(k, k)) * new Transform2D(0, new Vector2((float)-X, (float)-Z));
        world.DrawSetTransformMatrix(t);
        double reach = Px / k;
        tiles.Blit(world, X - reach, Z - reach, X + reach, Z + reach);
    }

    void DrawTop()
    {
        var c = top;
        float u = Px / 200, k = K, S = Px;
        var mid = new Vector2(S / 2, S / 2);
        Ui.Arrow(c, mid, 0, Ui.Rec, u, tip: 7, wing: 5, tail: 5, notch: 2, stroke: 0);
        c.DrawArc(mid, S / 2 - u, 0, Mathf.Tau, 64, new Color(242 / 255f, 240 / 255f, 230 / 255f, 0.6f), 1.5f * u, true);
        float cs = Mathf.Cos((float)Yaw), sn = Mathf.Sin((float)Yaw);
        float R = S / 2 - 9 * u;
        Vector2 ToMap(double wx, double wz)
        {
            float dx = (float)(wx - X) * k, dz = (float)(wz - Z) * k;
            return new Vector2(dx * cs - dz * sn, dx * sn + dz * cs);
        }
        foreach (var m in tiles.Map.Markers)
        {
            var p = ToMap(m.X, m.Z);
            float d = p.Length();
            if (d < R) Ui.Pin(c, mid + p, m, u * 0.85f);
            else if (!m.Reached)
            {
                var a = p / d;
                var t = new Transform2D(Mathf.Atan2(a.Y, a.X) + Mathf.Pi / 2, mid + a * (S / 2 - 7 * u));
                Vector2[] tri = [t * new Vector2(0, -6 * u), t * new Vector2(5 * u, 4 * u), t * new Vector2(-5 * u, 4 * u)];
                c.DrawColoredPolygon(tri, Color.FromHtml(m.Color));
                c.DrawPolyline([.. tri, tri[0]], new Color(0, 0, 0, 0.7f), u, true);
            }
        }
        if (overlay.Exit is { } e)
        {
            var p = ToMap(e.X, e.Z);
            float d = p.Length();
            if (d > R) p = p / d * (S / 2 - 8 * u);
            Ui.Exit(c, mid + p, u * 0.8f);
        }
        foreach (var o in overlay.Others)
        {
            var p = ToMap(o.X, o.Z);
            float d = p.Length();
            if (d < R) Ui.Arrow(c, mid + p, (float)(Yaw - o.Yaw), o.Color, u * 0.8f);
            else c.DrawCircle(mid + p / d * (S / 2 - 6 * u), 3 * u, new Color(o.Color, 0.85f));
        }
    }
}
