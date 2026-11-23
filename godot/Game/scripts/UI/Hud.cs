using Backrooms.Core.Hunt;
using Godot;

namespace Backrooms.UI;

/// <summary>
/// The overlay of web/index.html: the camcorder OSD (REC, tape speed, date, timecode, room), the
/// minimap with the waypoint line under it, toasts, the stats line and the full map.
/// </summary>
public partial class Hud : CanvasLayer
{
    public Control Osd { get; private set; } = null!;
    public Minimap Minimap { get; private set; } = null!;
    public FullMap FullMap { get; private set; } = null!;
    public Label Stats { get; private set; } = null!;
    public Label Room { get; private set; } = null!;
    public Label Waypoint { get; private set; } = null!;
    /// <summary>Top centre: the hunt's status line (M8).</summary>
    public Label HuntLine { get; private set; } = null!;

    Label rec = null!, timecode = null!, toast = null!;
    // the hunt: stamina / phase bar, banner, the entity's countdown screen
    Control bar = null!, frozen = null!, banner = null!;
    Label barLabel = null!, bannerTitle = null!, bannerSub = null!, frozenN = null!;
    ColorRect barFill = null!;
    double toastT, blinkT;

    public void Init(MapTextures tiles, MapOverlay overlay)
    {
        Layer = 1;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var osd = Ui.Text(22, Ui.Paper);
        Osd = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        Osd.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Osd);
        var tl = Ui.At(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0.05f, 0.06f);
        var recRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rec = Ui.Label("●", Ui.Text(22, Ui.Rec));
        recRow.AddChild(rec);
        recRow.AddChild(Ui.Label("REC", osd));
        tl.AddChild(recRow);
        Room = Ui.Label("", Ui.Text(15, new Color(Ui.Paper, 0.85f)));
        tl.AddChild(Room);
        Osd.AddChild(tl);
        Osd.AddChild(Ui.At(Ui.Label("NOV. 18 1996", osd), 0.05f, 0.93f, gy: Control.GrowDirection.Begin));
        timecode = Ui.At(Ui.Label("0:00:00", osd, HorizontalAlignment.Right), 0.95f, 0.93f, Control.GrowDirection.Begin, Control.GrowDirection.Begin);
        Osd.AddChild(timecode);

        Minimap = new Minimap();
        Minimap.Init(tiles, overlay);
        var mapHolder = Ui.At(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0.955f, 0.05f, Control.GrowDirection.Begin);
        mapHolder.AddChild(Minimap);
        Waypoint = Ui.Label("", Ui.Text(15, Ui.Paper), HorizontalAlignment.Center);
        Waypoint.CustomMinimumSize = new Vector2(Minimap.Px, 20);
        mapHolder.AddChild(Waypoint);
        // tape speed sits under the minimap
        mapHolder.AddChild(Ui.Label("SP ▮▮▮▯", Ui.Text(16, Ui.Paper), HorizontalAlignment.Right));
        root.AddChild(mapHolder);

        var hunt = Ui.At(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0.5f, 0.05f, Control.GrowDirection.Both);
        HuntLine = Ui.Label("", Ui.Text(16, Ui.Paper, 0.12f, shadowAlpha: 0.6f), HorizontalAlignment.Center);
        hunt.AddChild(HuntLine);
        bar = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        barLabel = Ui.Label("", Ui.Text(12, Ui.Paper, 0.12f), HorizontalAlignment.Center);
        bar.AddChild(barLabel);
        var track = new Panel { CustomMinimumSize = new Vector2(220, 8), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        track.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color(Ui.Paper, 0.4f), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1 });
        barFill = new ColorRect { Color = Ui.Menu, Position = new Vector2(1, 1), Size = new Vector2(218, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        track.AddChild(barFill);
        bar.AddChild(track);
        hunt.AddChild(bar);
        root.AddChild(hunt);

        banner = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, AnchorLeft = 0, AnchorRight = 1, AnchorTop = 0.34f, AnchorBottom = 0.34f };
        bannerTitle = Ui.Label("", Ui.Text(56, Ui.Paper, 0.3f, 3, 0.6f), HorizontalAlignment.Center);
        bannerSub = Ui.Label("", Ui.Text(18, new Color(Ui.Paper, 0.8f), 0.15f, 3, 0.6f), HorizontalAlignment.Center);
        banner.AddChild(bannerTitle);
        banner.AddChild(bannerSub);
        root.AddChild(banner);

        frozen = new ColorRect { Color = Color.FromHtml("#050402"), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        frozen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var fcol = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        fcol.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        fcol.AddChild(Ui.Label("THE WANDERERS ARE HIDING", Ui.Text(18, Color.FromHtml("#8a826a"), 0.3f, 0), HorizontalAlignment.Center));
        frozenN = Ui.Label("", Ui.Text(64, Color.FromHtml("#d9534f"), 0.3f, 0), HorizontalAlignment.Center);
        fcol.AddChild(frozenN);
        frozen.AddChild(fcol);

        toast = Ui.At(Ui.Label("", Ui.Text(26, Ui.Paper, 0.15f, shadowAlpha: 0.6f), HorizontalAlignment.Center), 0.5f, 0.22f, Control.GrowDirection.Both);
        toast.Visible = false;
        root.AddChild(toast);

        root.AddChild(frozen);
        root.MoveChild(frozen, 0); // under the OSD, like the web's z-order

        Stats = Ui.At(Ui.Label("", new LabelSettings { Font = Ui.Mono, FontSize = 11, FontColor = new Color(1, 1, 1, 0.53f) }), 0, 0);
        Stats.Position = new Vector2(8, 8);
        Stats.Visible = false;
        root.AddChild(Stats);

        FullMap = new FullMap();
        FullMap.Init(tiles, overlay);
        AddChild(FullMap);
    }

    /// <summary>The hunt's part of the HUD (web hunt.ts update()).</summary>
    public void ShowHunt(HuntClient h)
    {
        HuntLine.Text = h.Line();
        frozen.Visible = h.Frozen;
        if (h.Frozen) frozenN.Text = Math.Ceiling(h.SecondsLeft).ToString();
        if (h.Bar() is { } b)
        {
            bar.Visible = true;
            barLabel.Text = b.Label;
            barFill.Size = new Vector2(218 * (float)Math.Clamp(b.Fill, 0, 1), 6);
            barFill.Color = b.Bad ? Color.FromHtml("#d9534f") : Ui.Menu;
        }
        else bar.Visible = false;
        banner.Visible = h.BannerT > 0;
        if (banner.Visible)
        {
            bannerTitle.Text = h.BannerTitle;
            bannerSub.Text = h.BannerSub;
        }
    }

    public void Toast(string msg)
    {
        toast.Text = msg;
        toast.Visible = true;
        toastT = 3;
    }

    public void Tick(double time, double dt)
    {
        int s = (int)Math.Floor(time);
        timecode.Text = $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}";
        blinkT = (blinkT + dt) % 1.2;
        rec.Modulate = new Color(1, 1, 1, blinkT < 0.6 ? 1 : 0);
        if (toastT > 0 && (toastT -= dt) <= 0) toast.Visible = false;
    }
}
