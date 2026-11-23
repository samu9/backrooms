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
///   --fog=D --q=0|1 --t=SECS   fog density, shadow quality, shader time at start (as the web's ?fog ?q ?t)
///   --look=debug        the flat placeholder look instead of the level shader
///   --vhs=0|1 --exp=E --scale=S --msaa=N   camcorder effects, exposure, render scale (fixed), MSAA
///   --prop=K [--pname=NAME]   stand next to the K-th prop near the spawn (the web's ?prop=K&pname=)
///   --screenshot=PATH   build everything in range, render, save a PNG and quit
///   --autowalk=SECS     sprint forward for SECS, print where we ended up and quit (headless check)
/// Keys: click to capture the mouse, WASD/arrows, Shift run, N noclip, V camcorder, P stats, F fullscreen, Esc release.
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
    double time;
    bool frozenTime;
    Post post = null!;
    bool fixedScale;
    double frameAvg = 16, statT;

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

        // the level shader's per-frame inputs (project.godot [shader_globals]), as the web's shared uniforms
        RenderingServer.GlobalShaderParameterSet("br_seed", seed);
        RenderingServer.GlobalShaderParameterSet("br_fog_color", new Vector3(FogColor.R, FogColor.G, FogColor.B));
        RenderingServer.GlobalShaderParameterSet("br_fog_density", Num("fog", 0.022));
        RenderingServer.GlobalShaderParameterSet("br_quality", Num("q", 1));
        for (int i = 0; i < 4; i++) RenderingServer.GlobalShaderParameterSet($"br_ent{i}", Vector4.Zero);
        time = Num("t", 0);
        frozenTime = args.ContainsKey("screenshot"); // like the web's ?shot: a still frame at time t
        RenderingServer.GlobalShaderParameterSet("br_time", time % 4096);

        // the 3D world renders into the post pipeline's HDR viewport; the camcorder pass does the rest
        post = new Post { Name = "Post" };
        AddChild(post);
        fixedScale = args.ContainsKey("scale") || frozenTime;
        post.Scale = (float)Num("scale", frozenTime ? 1 : 0.85);
        post.Init((int)Num("msaa", 4));
        post.Set("vhs", (float)Num("vhs", 1));
        post.Set("exposure", (float)Num("exp", 1));
        var world = post.Scene;

        // the shaders output linear HDR: no tone mapping here (the camcorder pass does ACES).
        // The background colour is linear in the web (fog * 0.4); Godot's is sRGB.
        world.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = (FogColor * 0.4f).LinearToSrgb(),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });

        var look = args.GetValueOrDefault("look", "level");
        chunks = new ChunkManager { Name = "Chunks", Radius = (int)Num("radius", 2) };
        chunks.Init(gen, GD.Load<Shader>(look == "debug" ? "res://shaders/level_debug.gdshader" : "res://shaders/level.gdshader"),
            look == "debug" ? null : GD.Load<Shader>("res://shaders/prop.gdshader"));
        world.AddChild(chunks);

        player = new PlayerNode { Name = "Player" };
        world.AddChild(player);
        player.Init(motion);

        stats = GetNode<Label>("UI/Info");
        stats.Visible = false;

        chunks.BuildAllNow(motion.X, motion.Z);
        if (args.TryGetValue("prop", out var propK))
        {
            StandByProp(motion, int.Parse(propK, CultureInfo.InvariantCulture), args.GetValueOrDefault("pname"));
            chunks.BuildAllNow(motion.X, motion.Z);
        }
        GD.Print($"seed {seed}: spawn ({motion.X:0.##}, {motion.Z:0.##}) yaw {motion.Yaw:0.###}, {chunks.Chunks.Count} chunks, zone {gen.ZoneAt((int)Math.Floor(motion.X / Gen.Cell), (int)Math.Floor(motion.Z / Gen.Cell)).Type}");

        if (args.TryGetValue("screenshot", out var path))
        {
            screenshot = path;
            screenshotFrames = 15; // the bloom chain settles over a few frames
            player.Frozen = true;
            stats.Visible = args.ContainsKey("stats");
        }
        if (args.TryGetValue("autowalk", out var secs))
        {
            player.AutoWalk = double.Parse(secs, CultureInfo.InvariantCulture);
            autoWalking = true;
        }
    }

    /// <summary>Debug view (the web's ?prop=K&pname=): stand a few metres from the K-th prop around the spawn.</summary>
    void StandByProp(PlayerMotion m, int k, string? name)
    {
        var gen = chunks.Gen;
        var all = chunks.AllPropSpots().Where(p => string.IsNullOrEmpty(name) || p.Name == name)
            .OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[(k + i) % all.Count];
            for (int a = 0; a < 16; a++)
            {
                double t = a / 16.0 * Math.PI * 2;
                foreach (double d in new[] { 1.7, 2.2, 2.8 })
                {
                    double x = p.X + Math.Cos(t) * d, z = p.Z + Math.Sin(t) * d;
                    if (m.Collides(x, z)) continue;
                    if (gen.FreeDistance(x, z, -Math.Cos(t), -Math.Sin(t), d) < d - 0.9) continue;
                    (m.X, m.Z, m.Yaw, m.Pitch) = (x, z, Math.Atan2(Math.Cos(t), Math.Sin(t)), -0.32);
                    return;
                }
            }
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
        // the player lives inside the scene SubViewport, which gets no input events of its own
        player.HandleInput(e);
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.PhysicalKeycode == Key.P) stats.Visible = !stats.Visible;
        if (k.PhysicalKeycode == Key.V) post.Set("vhs", post.Get("vhs") > 0.5f ? 0 : 1);
        if (k.PhysicalKeycode == Key.F)
            DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
    }

    public override void _Process(double delta)
    {
        var m = player.Motion;
        if (!frozenTime) time += Math.Clamp(delta, 0, 0.1);
        RenderingServer.GlobalShaderParameterSet("br_time", time % 4096);
        post.Set("time", (float)time);
        chunks.UpdateAround(m.X, m.Z);

        // dynamic resolution: keep frames under ~18 ms (same thresholds as the web)
        frameAvg = frameAvg * 0.95 + delta * 1000 * 0.05;
        if (!fixedScale && (statT += delta) > 1.5)
        {
            statT = 0;
            float ns = frameAvg > 19 ? Math.Max(0.5f, post.Scale - 0.1f) : frameAvg < 13 ? Math.Min(1f, post.Scale + 0.05f) : post.Scale;
            if (ns != post.Scale)
            {
                post.Scale = ns;
                post.Resize();
            }
        }

        if (stats.Visible)
            stats.Text = $"{1000 / frameAvg:0} fps · scale {post.Scale:0.00} · chunks {chunks.Chunks.Count} · calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}"
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
