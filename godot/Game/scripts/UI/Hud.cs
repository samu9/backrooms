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

        HuntLine = Ui.At(Ui.Label("", Ui.Text(16, Ui.Paper, 0.12f), HorizontalAlignment.Center), 0.5f, 0.05f, Control.GrowDirection.Both);
        root.AddChild(HuntLine);

        toast = Ui.At(Ui.Label("", Ui.Text(26, Ui.Paper, 0.15f, shadowAlpha: 0.6f), HorizontalAlignment.Center), 0.5f, 0.22f, Control.GrowDirection.Both);
        toast.Visible = false;
        root.AddChild(toast);

        Stats = Ui.At(Ui.Label("", new LabelSettings { Font = Ui.Mono, FontSize = 11, FontColor = new Color(1, 1, 1, 0.53f) }), 0, 0);
        Stats.Position = new Vector2(8, 8);
        Stats.Visible = false;
        root.AddChild(Stats);

        FullMap = new FullMap();
        FullMap.Init(tiles, overlay);
        AddChild(FullMap);
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
