using Backrooms.Core;
using Godot;

namespace Backrooms.UI;

/// <summary>Shared look of the overlay (the web's "Courier New" OSD) and the map drawing helpers.</summary>
public static class Ui
{
    public static readonly Color Paper = Color.FromHtml("#f2f0e6");
    public static readonly Color Menu = Color.FromHtml("#e8dca8");
    public static readonly Color Rec = Color.FromHtml("#ff3b2f");

    static SystemFont? mono;
    public static SystemFont Mono => mono ??= new SystemFont
    {
        FontNames = ["Courier New", "Liberation Mono", "DejaVu Sans Mono", "monospace"],
        Antialiasing = TextServer.FontAntialiasing.Gray,
    };

    /// <summary>Monospace with extra letter spacing (CSS letter-spacing, in em).</summary>
    public static Font Spaced(float em, int size) => new FontVariation { BaseFont = Mono, SpacingGlyph = (int)Math.Round(em * size) };

    public static LabelSettings Text(int size, Color color, float spacingEm = 0.08f, float shadow = 2, float shadowAlpha = 0.55f) => new()
    {
        Font = Spaced(spacingEm, size),
        FontSize = size,
        FontColor = color,
        ShadowColor = new Color(0, 0, 0, shadowAlpha),
        ShadowOffset = new Vector2(shadow, shadow),
        ShadowSize = 0,
    };

    public static Label Label(string text, LabelSettings s, HorizontalAlignment align = HorizontalAlignment.Left) =>
        new() { Text = text, LabelSettings = s, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>Places a control relative to its parent, in fractions (like CSS left/top percentages).</summary>
    public static T At<T>(T c, float ax, float ay, Control.GrowDirection gx = Control.GrowDirection.End, Control.GrowDirection gy = Control.GrowDirection.End) where T : Control
    {
        c.AnchorLeft = c.AnchorRight = ax;
        c.AnchorTop = c.AnchorBottom = ay;
        c.GrowHorizontal = gx;
        c.GrowVertical = gy;
        return c;
    }

    // ---- map drawing, from web/src/minimap.ts ----

    static readonly Color Outline = new(0, 0, 0, 0.75f);

    public static Vector2[] Arc(Vector2 c, float r, float a0, float a1, int n = 20)
    {
        var p = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = a0 + (a1 - a0) * i / n;
            p[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }
        return p;
    }

    static void Closed(CanvasItem c, Vector2[] pts, Color fill, Color? stroke, float width)
    {
        if (fill.A > 0) c.DrawColoredPolygon(pts, fill);
        if (stroke is { } s) c.DrawPolyline([.. pts, pts[0]], s, width, true);
    }

    public static void CenteredText(CanvasItem c, Vector2 at, string text, int size, Color color, Font? font = null)
    {
        font ??= Mono;
        c.DrawString(font, at + new Vector2(-200, size * 0.35f), text, HorizontalAlignment.Center, 400, size, color);
    }

    /// <summary>A map pin: a teardrop with the mark's number (a tick once reached).</summary>
    public static void Pin(CanvasItem c, Vector2 p, Marker m, float u)
    {
        var col = Color.FromHtml(m.Color);
        var pts = new List<Vector2> { p, p + new Vector2(-5 * u, -9 * u) };
        pts.AddRange(Arc(p + new Vector2(0, -11 * u), 5.4f * u, Mathf.Pi * 0.82f, Mathf.Pi * 2.18f));
        var outline = m.Reached ? new Color(Outline, 0.45f * Outline.A) : Outline;
        if (m.Reached) Closed(c, [.. pts], new Color(0, 0, 0, 0), new Color(col, 0.45f), 1.5f * u);
        else Closed(c, [.. pts], col, outline, 1.5f * u);
        CenteredText(c, p + new Vector2(0, -11 * u), m.Reached ? "✓" : m.Id.ToString(), (int)Math.Max(6, 7.5f * u), m.Reached ? new Color(col, 0.45f) : Color.FromHtml("#111"));
    }

    /// <summary>The exit door: a small glowing doorway.</summary>
    public static void Exit(CanvasItem c, Vector2 p, float u, string label = "")
    {
        var glow = Color.FromHtml("#fff6d8");
        for (int i = 3; i >= 1; i--)
            c.DrawRect(new Rect2(p + new Vector2(-4 - i * 2, -7 - i * 2) * u, new Vector2(8 + i * 4, 13 + i * 4) * u), new Color(glow, 0.12f));
        var r = new Rect2(p + new Vector2(-4 * u, -7 * u), new Vector2(8 * u, 13 * u));
        c.DrawRect(r, Color.FromHtml("#fffbea"));
        c.DrawRect(r, new Color(0, 0, 0, 0.8f), false, u);
        if (label != "") CenteredText(c, p + new Vector2(0, -14 * u), label, (int)(7 * u), Color.FromHtml("#fffbea"));
    }

    /// <summary>An arrow for a player; rot is the screen rotation (0 = pointing up).</summary>
    public static void Arrow(CanvasItem c, Vector2 p, float rot, Color color, float u, float tip = 6, float wing = 4.5f, float tail = 5, float notch = 2, float stroke = 1)
    {
        var t = new Transform2D(rot, p);
        Vector2[] pts = [t * new Vector2(0, -tip * u), t * new Vector2(wing * u, tail * u), t * new Vector2(0, notch * u), t * new Vector2(-wing * u, tail * u)];
        Closed(c, pts, color, stroke > 0 ? new Color(0, 0, 0, 0.8f) : null, stroke * u);
    }
}

/// <summary>The discovered map tiles as textures, shared by the minimap and the full map.</summary>
public sealed class MapTextures(MapData map)
{
    readonly Dictionary<(int, int), ImageTexture> tex = [];
    public MapData Map => map;

    public ImageTexture? Get(int ci, int cj)
    {
        if (!map.Tiles.TryGetValue((ci, cj), out var t)) return null;
        if (!tex.TryGetValue((ci, cj), out var it) || t.Dirty)
        {
            var img = Image.CreateFromData(MapTile.Size, MapTile.Size, false, Image.Format.Rgba8, t.Rgba);
            if (it == null) tex[(ci, cj)] = it = ImageTexture.CreateFromImage(img);
            else it.Update(img);
            t.Dirty = false;
        }
        return it;
    }

    /// <summary>Draws the tiles covering a world rectangle; the canvas transform maps metres to pixels.</summary>
    public void Blit(CanvasItem c, double x0, double z0, double x1, double z1)
    {
        for (int cj = (int)Math.Floor(z0 / Gen.Chunk); cj <= (int)Math.Floor(z1 / Gen.Chunk); cj++)
            for (int ci = (int)Math.Floor(x0 / Gen.Chunk); ci <= (int)Math.Floor(x1 / Gen.Chunk); ci++)
                if (Get(ci, cj) is { } t)
                    c.DrawTextureRect(t, new Rect2((float)(ci * Gen.Chunk), (float)(cj * Gen.Chunk), (float)Gen.Chunk, (float)Gen.Chunk), false);
    }
}

/// <summary>Other players and the exit, as the maps should show them (filled in by multiplayer and the hunt).</summary>
public sealed class MapOverlay
{
    public readonly record struct Other(double X, double Z, double Yaw, Color Color, string Name);
    public List<Other> Others = [];
    public (double X, double Z)? Exit;
}
