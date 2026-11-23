using Godot;

namespace Backrooms.UI;

/// <summary>
/// The title menu of web/index.html, shown whenever the mouse is free: click anywhere to go in,
/// or join a room / play alone against the entity (which restart the world, as the web reloads).
/// </summary>
public partial class Menu : Control
{
    public event Action? Enter;
    public event Action<string, string>? Join;
    public event Action<string>? Solo;

    LineEdit room = null!, name = null!;
    Label go = null!;
    public Label Hint { get; private set; } = null!;
    double blink;

    public void Init(string roomName, string playerName)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;

        var bg = new ColorRect { MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        bg.Material = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                shader_type canvas_item;
                void fragment() {
                    float d = length((UV - 0.5) * vec2(1.0, 1.0)) / 0.7071;
                    COLOR = mix(vec4(40.0, 34.0, 14.0, 0.55 * 255.0) / 255.0, vec4(8.0, 7.0, 3.0, 0.92 * 255.0) / 255.0, clamp(d, 0.0, 1.0));
                }
                """,
            },
        };
        AddChild(bg);

        var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        col.AddThemeConstantOverride("separation", 0);
        AddChild(col);
        var ink = Ui.Menu;
        LabelSettings S(int size, float alpha = 1, float em = 0) => new()
        {
            Font = Ui.Spaced(em, size), FontSize = size, FontColor = new Color(ink, alpha),
            ShadowColor = new Color(1, 0.9f, 0.55f, 0.18f), ShadowSize = 3, ShadowOffset = Vector2.Zero,
        };
        Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0, h), MouseFilter = MouseFilterEnum.Ignore };
        col.AddChild(Ui.Label("THE BACKROOMS", S(46, 1, 0.35f), HorizontalAlignment.Center));
        col.AddChild(Ui.Label("LEVEL 0 · ~600 MILLION SQ. MILES OF RANDOMLY SEGMENTED EMPTY ROOMS", S(15, 0.65f, 0.2f), HorizontalAlignment.Center));
        col.AddChild(Gap(36));
        go = Ui.Label("[ CLICK TO NOCLIP IN ]", S(18), HorizontalAlignment.Center);
        col.AddChild(go);
        col.AddChild(Gap(36));

        // the multiplayer / solo form: clicks here don't enter
        var form = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Arrow };
        form.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0), BorderColor = new Color(ink, 0.25f), BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        var rows = new VBoxContainer();
        form.AddChild(rows);
        var r1 = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        room = Field(roomName, "level-0", 32);
        name = Field(playerName, "", 20);
        r1.AddChild(Ui.Label("MULTIPLAYER   room", S(15)));
        r1.AddChild(room);
        r1.AddChild(Ui.Label(" name", S(15)));
        r1.AddChild(name);
        r1.AddChild(Btn("JOIN", () => Join?.Invoke(room.Text.Trim(), name.Text.Trim())));
        room.TextSubmitted += _ => Join?.Invoke(room.Text.Trim(), name.Text.Trim());
        rows.AddChild(r1);
        var r2 = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        r2.AddChild(Ui.Label("ALONE, VS THE ENTITY  ", S(15)));
        foreach (var d in new[] { "easy", "normal", "nightmare" }) r2.AddChild(Btn(d.ToUpperInvariant(), () => Solo?.Invoke(d)));
        rows.AddChild(r2);
        Hint = Ui.Label("share the room name: everyone in it walks the same Backrooms", S(13, 0.55f), HorizontalAlignment.Center);
        rows.AddChild(Hint);
        col.AddChild(form);
        col.AddChild(Gap(30));

        var keys = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0.75f) };
        keys.AddThemeConstantOverride("h_separation", 18);
        foreach (var (what, how) in new[]
        {
            ("look", "mouse"), ("walk", "W A S D · shift to run"), ("noclip", "N (not during a hunt)"),
            ("the hunt: start · phase through walls", "R · E"), ("camcorder fx / overlay", "V / H"),
            ("full map (marks) · minimap · zoom", "M · Tab · [ ]"), ("sound · stats · fullscreen", "K · P · F"), ("menu", "Esc"),
        })
        {
            keys.AddChild(Ui.Label(what, S(15, 0.7f), HorizontalAlignment.Right));
            keys.AddChild(Ui.Label(how, S(15)));
        }
        col.AddChild(keys);

        LineEdit Field(string text, string placeholder, int max)
        {
            var f = new LineEdit { Text = text, PlaceholderText = placeholder, MaxLength = max, CustomMinimumSize = new Vector2(150, 0) };
            f.AddThemeFontOverride("font", Ui.Mono);
            f.AddThemeFontSizeOverride("font_size", 15);
            f.AddThemeColorOverride("font_color", ink);
            f.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), BorderColor = new Color(ink, 0.35f), BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1, ContentMarginLeft = 8, ContentMarginRight = 8 });
            return f;
        }
        Button Btn(string text, Action click)
        {
            var b = new Button { Text = text, MouseDefaultCursorShape = CursorShape.PointingHand };
            b.AddThemeFontOverride("font", Ui.Mono);
            b.AddThemeFontSizeOverride("font_size", 15);
            var face = new StyleBoxFlat { BgColor = ink, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4 };
            foreach (var st in new[] { "normal", "hover", "pressed", "focus" }) b.AddThemeStyleboxOverride(st, face);
            foreach (var st in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) b.AddThemeColorOverride(st, Color.FromHtml("#1a160a"));
            b.Pressed += click;
            return b;
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            AcceptEvent();
            Enter?.Invoke();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        blink = (blink + delta) % 1.6;
        go.Modulate = new Color(1, 1, 1, blink < 0.8 ? 1 : 0);
    }
}
