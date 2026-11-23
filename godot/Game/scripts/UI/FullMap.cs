using Backrooms.Core;
using Godot;

namespace Backrooms.UI;

/// <summary>
/// Full-screen map of web/src/minimap.ts FullMap: north-up, drag or WASD to pan, wheel or +/- to
/// zoom around the cursor, left click places a mark, right click removes one, C centres on you.
/// Shares the discovered tiles and the marks with the minimap.
/// </summary>
public partial class FullMap : Control
{
    public bool Open { get; private set; }
    public double Cx, Cz;
    public float MapScale = 8; // px per metre
    public double Px, Pz, Yaw;

    MapTextures tiles = null!;
    MapOverlay overlay = null!;
    Control world = null!;
    (Vector2 at, double cx, double cz, bool moved)? drag;

    public void Init(MapTextures t, MapOverlay o)
    {
        tiles = t;
        overlay = o;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Cross;
        Visible = false;
        world = new Control { MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Nearest };
        world.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        world.Draw += DrawWorld;
        AddChild(world);
    }

    public void Show(double px, double pz)
    {
        Open = Visible = true;
        (Cx, Cz) = (px, pz);
    }

    public new void Hide()
    {
        Open = Visible = false;
        drag = null;
    }

    public void Center(double px, double pz) => (Cx, Cz) = (px, pz);

    (double, double) ToWorld(Vector2 s) => (Cx + (s.X - Size.X / 2) / MapScale, Cz + (s.Y - Size.Y / 2) / MapScale);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                var (wx, wz) = ToWorld(mb.Position);
                tiles.Map.RemoveNear(wx, wz, 14 / MapScale + 0.5);
            }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) drag = (mb.Position, Cx, Cz, false);
                else
                {
                    if (drag is { moved: false })
                    {
                        var (wx, wz) = ToWorld(mb.Position);
                        tiles.Map.AddMarker(wx, wz);
                    }
                    drag = null;
                }
            }
            else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                // zoom around the cursor
                var (wx, wz) = ToWorld(mb.Position);
                MapScale = Math.Clamp(MapScale * (mb.ButtonIndex == MouseButton.WheelUp ? 1.16f : 1 / 1.16f), 0.6f, 40);
                var (nx, nz) = ToWorld(mb.Position);
                Cx += wx - nx;
                Cz += wz - nz;
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && drag is { } d)
        {
            bool moved = d.moved || mm.Position.DistanceTo(d.at) > 4;
            drag = d with { moved = moved };
            if (moved)
            {
                Cx = d.cx - (mm.Position.X - d.at.X) / MapScale;
                Cz = d.cz - (mm.Position.Y - d.at.Y) / MapScale;
            }
        }
    }

    public void Refresh(double px, double pz, double yaw, double dt)
    {
        if (!Open) return;
        (Px, Pz, Yaw) = (px, pz, yaw);
        // keyboard panning and zoom
        double pan = 500 / MapScale * dt;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) Cz -= pan;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) Cz += pan;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) Cx -= pan;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) Cx += pan;
        if (Input.IsPhysicalKeyPressed(Key.Equal) || Input.IsPhysicalKeyPressed(Key.KpAdd)) MapScale = Math.Min(40, MapScale * (1 + (float)dt * 1.5f));
        if (Input.IsPhysicalKeyPressed(Key.Minus) || Input.IsPhysicalKeyPressed(Key.KpSubtract)) MapScale = Math.Max(0.6f, MapScale / (1 + (float)dt * 1.5f));
        world.QueueRedraw();
        QueueRedraw();
    }

    Vector2 Screen(double wx, double wz) => new((float)(Size.X / 2 + (wx - Cx) * MapScale), (float)(Size.Y / 2 + (wz - Cz) * MapScale));

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(16 / 255f, 14 / 255f, 6 / 255f, 0.93f));
    }

    void DrawWorld()
    {
        float k = MapScale, W = Size.X, H = Size.Y;
        world.DrawSetTransformMatrix(new Transform2D(0, new Vector2((float)(W / 2 - Cx * k), (float)(H / 2 - Cz * k))) * Transform2D.Identity.Scaled(new Vector2(k, k)));
        double x0 = Cx - W / 2 / k, x1 = Cx + W / 2 / k, z0 = Cz - H / 2 / k, z1 = Cz + H / 2 / k;
        // faint 32 m grid (the chunk grid) under the map
        var grid = new Color(226 / 255f, 208 / 255f, 128 / 255f, 0.08f);
        for (double x = Math.Floor(x0 / Gen.Chunk) * Gen.Chunk; x <= x1; x += Gen.Chunk) world.DrawLine(new((float)x, (float)z0), new((float)x, (float)z1), grid, 1 / k);
        for (double z = Math.Floor(z0 / Gen.Chunk) * Gen.Chunk; z <= z1; z += Gen.Chunk) world.DrawLine(new((float)x0, (float)z), new((float)x1, (float)z), grid, 1 / k);
        tiles.Blit(world, x0, z0, x1, z1);

        // screen-space overlays
        world.DrawSetTransformMatrix(Transform2D.Identity);
        const float u = 1.6f;
        var route = tiles.Map.Markers.Where(m => !m.Reached).ToList();
        var prev = Screen(Px, Pz);
        foreach (var m in route)
        {
            var p = Screen(m.X, m.Z);
            world.DrawDashedLine(prev, p, new Color(242 / 255f, 240 / 255f, 230 / 255f, 0.35f), u, 4 * u);
            prev = p;
        }
        foreach (var m in tiles.Map.Markers) Ui.Pin(world, Screen(m.X, m.Z), m, u);
        if (overlay.Exit is { } e) Ui.Exit(world, Screen(e.X, e.Z), u, "EXIT");
        foreach (var o in overlay.Others)
        {
            Ui.Arrow(world, Screen(o.X, o.Z), (float)-o.Yaw, o.Color, u);
            Ui.CenteredText(world, Screen(o.X, o.Z) + new Vector2(0, -12 * u), o.Name, 12, new Color(Ui.Paper, 0.9f));
        }
        Ui.Arrow(world, Screen(Px, Pz), (float)-Yaw, Ui.Rec, u, tip: 8, wing: 5.5f, tail: 6, notch: 2.5f, stroke: 0.6f);

        // scale bar, compass, help
        var ink = new Color(Ui.Paper, 0.85f);
        var font = Ui.Mono;
        float nice = new[] { 1f, 2, 5, 10, 20, 50, 100, 200, 500 }.FirstOrDefault(v => v * k > 70, 1000);
        float bx = 24, by = H - 28;
        world.DrawRect(new Rect2(bx, by, nice * k, 2), ink);
        world.DrawRect(new Rect2(bx, by - 5, 2, 7), ink);
        world.DrawRect(new Rect2(bx + nice * k - 2, by - 5, 2, 7), ink);
        world.DrawString(font, new Vector2(bx, by - 8), $"{nice} m", HorizontalAlignment.Left, -1, 13, ink);
        Ui.CenteredText(world, new Vector2(W - 34, 30), "N", 13, ink);
        world.DrawColoredPolygon([new(W - 34, 40), new(W - 39, 54), new(W - 29, 54)], ink);
        const string help = "click: place mark · right click: remove · drag / WASD: pan · wheel / + -: zoom · C: centre · M: close";
        world.DrawString(font, new Vector2(W - 24 - 1200, H - 24), help, HorizontalAlignment.Right, 1200, 13, ink);
        world.DrawString(font, new Vector2(24, 38), "MAP — LEVEL 0", HorizontalAlignment.Left, -1, 20, ink);
        world.DrawString(font, new Vector2(24, 60), $"{tiles.Map.Markers.Count} marks · {route.Count} to reach", HorizontalAlignment.Left, -1, 13, ink);
    }
}
