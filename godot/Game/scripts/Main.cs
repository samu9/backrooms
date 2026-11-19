using System.Globalization;
using Backrooms.Core;
using Godot;

namespace Backrooms;

/// <summary>
/// Entry point. Command-line options go after "--" (godot --path godot/Game -- --seed=123):
///   --seed=N            world seed (default: random)
///   --view=K            spawn at the K-th long-sight-line viewpoint (same as the web's ?view=K)
///   --x= --z= --yaw= --pitch=   spawn position / direction
///   --radius=R          chunks kept around the player (default 2)
///   --screenshot=PATH   build everything in range, render, save a PNG and quit
///   --autowalk=SECS     sprint forward for SECS, print where we ended up and quit (headless check)
/// Keys: click to capture the mouse, WASD/arrows, Shift run, N noclip, P stats, F fullscreen, Esc release.
/// </summary>
public partial class Main : Node3D
{
    static readonly Color FogColor = new(0.62f, 0.55f, 0.34f);

    ChunkManager chunks = null!;
    PlayerNode player = null!;
    Label stats = null!;
    string? screenshot;
    int screenshotFrames = -1;
    bool autoWalking;

    public override void _Ready()
    {
        var args = ParseArgs();
        int seed = args.TryGetValue("seed", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : (int)(GD.Randi() % 1_000_000);
        double Num(string k, double d) => args.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : d;

        var gen = new Gen(seed);
        var motion = new PlayerMotion(gen);
        if (args.TryGetValue("view", out var view))
            (motion.X, motion.Z, motion.Yaw) = Viewpoints.Find(gen, int.Parse(view, CultureInfo.InvariantCulture));
        else
            (motion.X, motion.Z) = gen.FindFree(Num("x", 0), Num("z", 0), 2);
        if (args.ContainsKey("x") && args.ContainsKey("z"))
            (motion.X, motion.Z) = (Num("x", 0), Num("z", 0));
        motion.Yaw = Num("yaw", motion.Yaw);
        motion.Pitch = Num("pitch", 0);

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = FogColor * 0.4f },
        });

        chunks = new ChunkManager { Name = "Chunks", Radius = (int)Num("radius", 2) };
        chunks.Init(gen, GD.Load<Shader>("res://shaders/level_debug.gdshader"));
        AddChild(chunks);

        player = new PlayerNode { Name = "Player" };
        AddChild(player);
        player.Init(motion);

        stats = GetNode<Label>("UI/Info");
        stats.Visible = false;

        chunks.BuildAllNow(motion.X, motion.Z);
        GD.Print($"seed {seed}: spawn ({motion.X:0.##}, {motion.Z:0.##}) yaw {motion.Yaw:0.###}, {chunks.Chunks.Count} chunks, zone {gen.ZoneAt((int)Math.Floor(motion.X / Gen.Cell), (int)Math.Floor(motion.Z / Gen.Cell)).Type}");

        if (args.TryGetValue("screenshot", out var path))
        {
            screenshot = path;
            screenshotFrames = 3;
            stats.Visible = args.ContainsKey("stats");
        }
        if (args.TryGetValue("autowalk", out var secs))
        {
            player.AutoWalk = double.Parse(secs, CultureInfo.InvariantCulture);
            autoWalking = true;
        }
    }

    static Dictionary<string, string> ParseArgs()
    {
        var d = new Dictionary<string, string>();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (!a.StartsWith("--")) continue;
            int eq = a.IndexOf('=');
            if (eq < 0) d[a[2..]] = "";
            else d[a[2..eq]] = a[(eq + 1)..];
        }
        return d;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.PhysicalKeycode == Key.P) stats.Visible = !stats.Visible;
        if (k.PhysicalKeycode == Key.F)
            DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
    }

    public override void _Process(double delta)
    {
        var m = player.Motion;
        chunks.UpdateAround(m.X, m.Z);

        if (stats.Visible)
            stats.Text = $"{Engine.GetFramesPerSecond():0} fps · chunks {chunks.Chunks.Count} · calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}"
                + $" · tris {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:0.0}k · {m.X:0.0}, {m.Z:0.0}{(m.Noclip ? " · noclip" : "")}";

        if (screenshot != null && screenshotFrames >= 0 && screenshotFrames-- == 0)
        {
            var img = GetViewport().GetTexture().GetImage();
            var err = img.SavePng(screenshot);
            GD.Print(err == Error.Ok ? $"screenshot: {screenshot} ({img.GetWidth()}x{img.GetHeight()})" : $"screenshot failed: {err}");
            GetTree().Quit();
        }

        if (autoWalking && player.AutoWalk <= 0)
        {
            autoWalking = false;
            GD.Print($"autowalk: ended at ({m.X:0.00}, {m.Z:0.00}), inside a wall: {m.Collides(m.X, m.Z)}, chunks {chunks.Chunks.Count}");
            GetTree().Quit();
        }
    }
}
