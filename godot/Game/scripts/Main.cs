using System.Globalization;
using Backrooms.Core;
using Backrooms.Core.AI;
using Backrooms.Core.Hunt;
using Backrooms.Audio;
using Backrooms.Net;
using Backrooms.UI;
using Godot;

namespace Backrooms;

/// <summary>
/// Entry point. Command-line options go after "--" (godot --path godot/Game -- --seed=123):
///   --seed=N            world seed (default: random)
///   --view=K            spawn at the K-th long-sight-line viewpoint (same as the web's ?view=K)
///   --x= --z= --yaw= --pitch=   spawn position / direction
///   --room=NAME --name=N --server=ws://host:5191/ws   join a multiplayer room on the relay
///   --solo=easy|normal|nightmare   alone against the CPU entity (--autostart, --hunt-seconds=S,
///                       --quit-on-end print the outcome and quit: for checks without playing)
///   --snap=PATH --snap-at=SECS   save a live frame (HUD included) after SECS and quit
///   --record=PATH       record the master bus to a WAV until quitting (checks without ears)
///   --radius=R          chunks kept around the player (default 2)
///   --fog=D --q=0|1 --t=SECS   fog density, shadow quality, shader time at start (as the web's ?fog ?q ?t)
///   --look=debug        the flat placeholder look instead of the level shader
///   --vhs=0|1 --exp=E --scale=S --msaa=N   camcorder effects, exposure, render scale (fixed), MSAA
///   --prop=K [--pname=NAME]   stand next to the K-th prop near the spawn (the web's ?prop=K&pname=)
///   --screenshot=PATH   build everything in range, render, save a PNG and quit
///   --autowalk=SECS     sprint forward for SECS, print where we ended up and quit (headless check)
/// Keys: click the menu to go in, WASD/arrows, Shift run, N noclip, V camcorder, H overlay, Tab minimap, [ ] zoom,
/// M full map (C centre), R start the hunt, E phase, K sound, P stats, F fullscreen, Esc menu.
///   --osd=1 --map=1      keep the OSD / minimap in screenshots (the web's ?osd=1 &map=1)
///   --menu=1 --fullmap=1 --mark=X,Z;X,Z   screenshots of the menu / the full map / with marks
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
    Hud hud = null!;
    Menu menu = null!;
    MapData map = null!;
    readonly MapOverlay overlay = new();
    bool started, shot, showMenuInShot, soundOff;
    ISession? net;
    HuntClient hunt = null!;
    ExitDoor exitDoor = null!;
    double clockMs;
    bool quitOnEnd, snapping;
    ISoundscape audio = null!;
    string netError = "", room = "", playerName = "";
    Avatars avatars = null!;
    bool fixedScale;
    double frameAvg = 16, statT;

    public override void _Ready()
    {
        var args = ParseArgs();
        int seed = args.TryGetValue("seed", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : (int)(GD.Randi() % 1_000_000);
        double Num(string k, double d) => args.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : d;

        // multiplayer: join a room before building anything, the room decides the seed
        room = args.GetValueOrDefault("room", "").Trim();
        playerName = PlayerName(args);
        if (room != "" && !args.ContainsKey("screenshot"))
        {
            var url = args.GetValueOrDefault("server") is { Length: > 0 } u ? u : LoadSetting("server", NetClient.DefaultUrl);
            if (args.ContainsKey("server")) SaveSetting("server", url);
            var n = new NetClient();
            int? want = args.ContainsKey("seed") ? seed : null;
            try
            {
                // Connect never resumes on the main thread, so waiting here can't deadlock
                seed = Task.Run(() => n.Connect(url, room, playerName, want)).GetAwaiter().GetResult();
                net = new RelaySession(n, room);
                SaveSetting("name", playerName);
            }
            catch (Exception e)
            {
                netError = e.Message;
                n.Dispose();
                GD.PushWarning($"multiplayer unavailable, playing alone: {netError}");
            }
        }

        var gen = new Gen(seed);
        // single player against the CPU entity: same rules, run in the game
        string solo = args.GetValueOrDefault("solo", "");
        if (net == null && room == "" && !args.ContainsKey("screenshot") && Enum.TryParse<Difficulty>(solo, true, out var difficulty))
            net = new LocalSession(difficulty, gen);
        if (args.TryGetValue("hunt-seconds", out var hs)) HuntRules.HuntS = double.Parse(hs, CultureInfo.InvariantCulture);
        var motion = new PlayerMotion(gen);
        if (args.TryGetValue("view", out var view))
            (motion.X, motion.Z, motion.Yaw) = Viewpoints.Find(gen, int.Parse(view, CultureInfo.InvariantCulture));
        else
        {
            // in a room, spread arrivals out a little so people don't spawn inside each other
            double jitter = net is RelaySession ? 5 : 0;
            (motion.X, motion.Z) = gen.FindFree(Num("x", 0) + (GD.Randf() - 0.5) * jitter, Num("z", 0) + (GD.Randf() - 0.5) * jitter, 2);
        }
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

        avatars = new Avatars { Name = "Avatars" };
        world.AddChild(avatars);
        avatars.Init(chunks, GD.Load<Shader>("res://shaders/prop.gdshader"));
        // temporary sound (Core/Audio/SoundBank rendered at startup), behind ISoundscape
        var sound = new TempSoundscape { Name = "Sound" };
        world.AddChild(sound);
        audio = sound;
        avatars.Footstep += audio.Footstep;
        exitDoor = new ExitDoor { Name = "Exit" };
        world.AddChild(exitDoor);
        exitDoor.Init(avatars);

        // overlay: OSD, minimap, full map, toasts, menu (web/index.html)
        map = new MapData(gen);
        var mapTex = new MapTextures(map);
        hud = new Hud { Name = "Hud" };
        AddChild(hud);
        hud.Init(mapTex, overlay);
        stats = hud.Stats;
        map.Reached += m => hud.Toast($"MARK {m.Id} REACHED");
        menu = new Menu { Name = "Menu" };
        var menuLayer = new CanvasLayer { Name = "MenuLayer", Layer = 2 };
        menuLayer.AddChild(menu);
        AddChild(menuLayer);
        menu.Init(room, playerName);
        // the hunt (web/src/hunt.ts): roles, stamina, phasing, banners
        hunt = new HuntClient(net?.State, net != null ? net.Send : null) { Solo = net is LocalSession };
        hunt.Toast += hud.Toast;
        hunt.Teleport += (x, z) =>
        {
            (motion.X, motion.Z) = (x, z);
            motion.Vx = motion.Vz = 0;
        };
        // surfacing inside a wall: step out to the nearest free spot
        hunt.PhaseEnded += () =>
        {
            if (motion.Collides(motion.X, motion.Z)) (motion.X, motion.Z) = gen.FindFree(motion.X, motion.Z, 1);
        };
        motion.SprintGate = (want, moving, dt) => hunt.SprintGate(want, moving, dt) && !hunt.IsEntity;
        motion.FixedSpeed = () => hunt.IsEntity ? HuntClient.EntitySpeed : null;
        motion.Ghost = () => hunt.Phasing;
        player.Blocked = () => hunt.Frozen;
        player.NoclipAllowed = () => !hunt.Active;
        quitOnEnd = args.ContainsKey("quit-on-end");
        // a live frame with the HUD, some seconds in (screenshots of a hunt, which --screenshot can't freeze)
        // what the speakers would get: record the master bus until quitting (checks without ears)
        if (args.TryGetValue("record", out var rec))
        {
            var recorder = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
            AudioServer.AddBusEffect(0, recorder);
            recorder.SetRecordingActive(true);
            TreeExiting += () =>
            {
                recorder.SetRecordingActive(false);
                var err = recorder.GetRecording()?.SaveToWav(rec);
                GD.Print($"record: {rec} {err}");
            };
        }
        if (args.TryGetValue("snap", out var snap))
        {
            started = snapping = true;
            audio.Start();
            double at = Num("snap-at", 5);
            GetTree().CreateTimer(at).Timeout += () =>
            {
                var img = GetViewport().GetTexture().GetImage();
                GD.Print(img.SavePng(snap) == Error.Ok ? $"snap: {snap} at {at}s" : "snap failed");
                if (audio is TempSoundscape ts) GD.Print($"audio: {ts.Describe()}");
                GetTree().Quit();
            };
        }

        if (net is LocalSession ls)
        {
            menu.Hint.Text = $"alone, {ls.Difficulty.ToString().ToLowerInvariant()}: something else is in here with you";
            foreach (var p in net.State.Peers.Values) avatars.Add(p);
            if (args.ContainsKey("autostart")) StartSoloSoon();
        }
        else if (net != null)
        {
            net.Tick(clockMs);
            menu.Hint.Text = $"in room \"{room}\" with {net.State.Peers.Count} other(s) · seed {seed}";
            foreach (var p in net.State.Peers.Values) avatars.Add(p);
            net.State.Joined += p =>
            {
                avatars.Add(p);
                hud.Toast($"{p.Name.ToUpperInvariant()} ENTERED");
            };
            net.State.Left += p =>
            {
                avatars.Remove(p.Id);
                hud.Toast($"{p.Name.ToUpperInvariant()} NOCLIPPED OUT");
            };
            net.Closed += () => hud.Toast("SIGNAL LOST");
        }
        else if (netError != "")
        {
            menu.Hint.Text = $"could not reach the relay ({netError})";
            GetTree().CreateTimer(0.5).Timeout += () => hud.Toast("RELAY UNREACHABLE · ALONE");
        }
        menu.Enter += () =>
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            if (!started) audio.Start();
            // single player: the first time in, the hunt starts on its own
            if (net is LocalSession && !started) StartSoloSoon();
            started = true;
        };
        menu.Join += (r, n) => Relaunch(new() { ["room"] = r, ["name"] = n });
        menu.Solo += d => Relaunch(new() { ["solo"] = d });

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
            // a clean frame, like the web's ?shot: no menu, OSD or minimap unless asked for
            shot = true;
            hud.Osd.Visible = args.GetValueOrDefault("osd") == "1";
            hud.Minimap.GetParent<Control>().Visible = args.GetValueOrDefault("map") == "1";
            map.Reveal(motion.X, motion.Z, 0, force: true);
            showMenuInShot = args.GetValueOrDefault("menu") == "1";
            foreach (var mk in args.GetValueOrDefault("mark", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xz = mk.Split(',');
                map.AddMarker(double.Parse(xz[0], CultureInfo.InvariantCulture), double.Parse(xz[1], CultureInfo.InvariantCulture));
            }
            if (args.GetValueOrDefault("fullmap") == "1") hud.FullMap.Show(motion.X, motion.Z);
        }
        if (args.GetValueOrDefault("hud") == "0") shot = true;
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

    /// <summary>Options from the command line, replaced by the menu's choice after a relaunch.</summary>
    static Dictionary<string, string>? relaunch;

    /// <summary>Joining a room or playing alone starts over in a new world, as the web reloads the page.</summary>
    void Relaunch(Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("name", out var n) && n != "") SaveSetting("name", n);
        relaunch = opts;
        GetTree().ReloadCurrentScene();
    }

    const string SettingsPath = "user://settings.cfg";

    static string LoadSetting(string key, string fallback)
    {
        var cfg = new ConfigFile();
        return cfg.Load(SettingsPath) == Error.Ok ? cfg.GetValue("player", key, fallback).AsString() : fallback;
    }

    public override void _ExitTree() => net?.Dispose();

    void StartSoloSoon() => GetTree().CreateTimer(1.5).Timeout += hunt.RequestStart;

    static void SaveSetting(string key, string value)
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        cfg.SetValue("player", key, value);
        cfg.Save(SettingsPath);
    }

    static string PlayerName(Dictionary<string, string> args)
    {
        if (args.TryGetValue("name", out var n) && n != "") return n[..Math.Min(20, n.Length)];
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) == Error.Ok && cfg.GetValue("player", "name", "").AsString() is { Length: > 0 } saved) return saved;
        return $"wanderer-{GD.Randi() % 900 + 100}";
    }

    static Dictionary<string, string> ParseArgs()
    {
        if (relaunch != null) return relaunch;
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
        var fm = hud.FullMap;
        var mini = hud.Minimap.GetParent<Control>();
        switch (k.PhysicalKeycode)
        {
            case Key.R when started: hunt.RequestStart(); break;
            case Key.E: hunt.RequestPhase(); break;
            case Key.K when started:
                soundOff = !soundOff;
                hud.Toast(soundOff ? "SOUND OFF" : "SOUND ON");
                break;
            case Key.H: hud.Osd.Visible = !hud.Osd.Visible; break;
            case Key.Tab: mini.Visible = !mini.Visible; break;
            case Key.Bracketleft: hud.Minimap.MapScale = Math.Max(1.5f, hud.Minimap.MapScale / 1.4f); break;
            case Key.Bracketright: hud.Minimap.MapScale = Math.Min(14, hud.Minimap.MapScale * 1.4f); break;
            case Key.M when started:
                if (fm.Open) CloseFullMap(true);
                else
                {
                    fm.Show(player.Motion.X, player.Motion.Z);
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                }
                break;
            case Key.C when fm.Open: fm.Center(player.Motion.X, player.Motion.Z); break;
            case Key.Escape when fm.Open: CloseFullMap(false); break;
        }
        if (k.PhysicalKeycode == Key.P) stats.Visible = !stats.Visible;
        if (k.PhysicalKeycode == Key.V) post.Set("vhs", post.Get("vhs") > 0.5f ? 0 : 1);
        if (k.PhysicalKeycode == Key.F)
            DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
    }

    void CloseFullMap(bool resume)
    {
        hud.FullMap.Hide();
        Input.MouseMode = resume ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }

    public override void _Process(double delta)
    {
        var m = player.Motion;
        // the menu shows whenever the mouse is free and the map isn't open (pointer lock on the web)
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        menu.Visible = (!shot || showMenuInShot) && !snapping && !captured && !hud.FullMap.Open;
        player.InputEnabled = captured && !hud.FullMap.Open;
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

        double dtc = Math.Clamp(delta, 0, 0.1);
        map.Reveal(m.X, m.Z, dtc, shot);
        map.CheckReached(m.X, m.Z);
        hud.Tick(time, dtc);
        if (hud.Minimap.IsVisibleInTree()) hud.Minimap.Refresh(m.X, m.Z, m.Yaw);
        hud.FullMap.Refresh(m.X, m.Z, m.Yaw, dtc);
        if (map.Target is { } tgt)
        {
            double d = Math.Sqrt((tgt.X - m.X) * (tgt.X - m.X) + (tgt.Z - m.Z) * (tgt.Z - m.Z));
            hud.Waypoint.Text = $"◆ MARK {tgt.Id} · {(d < 1000 ? $"{d:0} M" : $"{d / 1000:0.0} KM")}";
            hud.Waypoint.LabelSettings.FontColor = Color.FromHtml(tgt.Color);
        }
        else hud.Waypoint.Text = "";

        // sound: quiet behind the menu (the web mutes when the pointer is released), K toggles
        audio.SetMuted(soundOff || !started || menu.Visible);
        if (player.Last.Footstep) audio.PlayerStep(m.Sprint);
        audio.Update(m.X, m.Z, m.Yaw, chunks.AllLights(), dtc);
        clockMs += Math.Clamp(delta, 0, 0.25) * 1000;
        hunt.Update(dtc, clockMs);
        hud.ShowHunt(hunt);
        if (net != null)
        {
            if (hunt.Active) m.Noclip = false;
            net.Pose(clockMs, m.X, m.Z, m.Yaw, m.Pitch, player.Last.Moving, hunt.Flags);
            net.Tick(clockMs);
            bool IsEnt(int id) => hunt.Active && hunt.RoleOf(id) == Role.Entity;
            avatars.Update(net.State.Peers.Values, m.X, m.Z, IsEnt);
            // in a hunt you only see your own side on the maps; wanderers also see the exit
            bool mine = hunt.MyRole == Role.Entity;
            overlay.Others = net.State.Peers.Values.Where(p => p.Visible && (!hunt.Active || IsEnt(p.Id) == mine))
                .Select(p => new MapOverlay.Other(p.X, p.Z, p.Yaw, Color.FromHtml(p.Color), p.Name)).ToList();
            overlay.Exit = hunt.Active && !mine ? hunt.Exit : null;
            exitDoor.Show(hunt.Active ? hunt.Exit : null);
            // entities kill the lights around them (theirs and ours)
            var ents = net.State.Peers.Values.Where(p => p.Visible && IsEnt(p.Id)).Select(p => (p.X, p.Z)).ToList();
            if (hunt.IsEntity) ents.Insert(0, (m.X, m.Z));
            for (int i = 0; i < 4; i++)
                RenderingServer.GlobalShaderParameterSet($"br_ent{i}", i < ents.Count ? new Vector4((float)ents[i].X, (float)ents[i].Z, 1, 0) : Vector4.Zero);
            // dread: the tape fails as an entity closes in on a wanderer
            double near = double.PositiveInfinity;
            if (!hunt.IsEntity)
                foreach (var (x, z) in ents) near = Math.Min(near, Math.Sqrt((x - m.X) * (x - m.X) + (z - m.Z) * (z - m.Z)));
            double dread = hunt.IsWanderer && hunt.Phase == Phase.Hunt ? Math.Clamp((14 - near) / 12, 0, 1) : 0;
            post.Set("dread", post.Get("dread") + (float)((dread - post.Get("dread")) * Math.Min(1, dtc * 3)));
            post.Set("hunter", post.Get("hunter") + (float)(((hunt.IsEntity ? 1 : 0) - post.Get("hunter")) * Math.Min(1, dtc * 2)));
            post.Set("glitch", (float)Math.Max(Math.Min(1, hunt.CaughtFx / 1.2), hunt.Phasing ? 0.3 : 0));
            hud.Room.Text = net.Label;
            audio.HunterEars = hunt.IsEntity;
            if (hunt.Active)
                foreach (var p in net.State.Peers.Values)
                {
                    if (!p.Visible) continue;
                    if (IsEnt(p.Id)) audio.Presence($"ent:{p.Id}", "entity", p.X, p.Z);
                    else if ((p.F & 1) != 0) audio.Presence($"br:{p.Id}", "breath", p.X, p.Z);
                }
            audio.EndFrame();
            audio.OwnBreath(hunt.IsWanderer ? Math.Max(0, 1 - hunt.Stamina / 3) : 0);
            if (quitOnEnd && hunt.Phase == Phase.End)
            {
                GD.Print($"hunt: winner {hunt.Winner}, my role {Names.Of(hunt.MyRole)}, banner \"{hunt.BannerTitle}\"");
                GetTree().Quit();
            }
        }
        else hud.Room.Text = netError != "" ? "OFFLINE" : "";

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
            if (net is LocalSession sol) GD.Print($"solo: phase {Names.Of(hunt.Phase)}, role {Names.Of(hunt.MyRole)}, entity {sol.Ai.State} at ({sol.Ai.X:0.0}, {sol.Ai.Z:0.0})");
            if (net != null) GD.Print($"hunt: phase {Names.Of(hunt.Phase)} role {Names.Of(hunt.MyRole)} roles {string.Join(",", hunt.Roles.Select(r => $"{r.Key}={Names.Of(r.Value)}"))}");
            GD.Print($"autowalk: ended at ({m.X:0.00}, {m.Z:0.00}), inside a wall: {m.Collides(m.X, m.Z)}, chunks {chunks.Chunks.Count}");
            if (net != null)
                GD.Print($"net: room {room} seed {chunks.Gen.Seed} id {net.State.Id} connected {net.Connected}, peers: "
                    + string.Join(", ", net.State.Peers.Values.Select(p => $"{p.Name} at ({p.X:0.00}, {p.Z:0.00}) visible {p.Visible} rendered {avatars.IsShown(p.Id)}")));
            // free distances from here in 8 directions: the same numbers mean the same world (mixedtest.mjs)
            var probe = Enumerable.Range(0, 8).Select(i => chunks.Gen.FreeDistance(m.X, m.Z, Math.Cos(i / 8.0 * Math.PI * 2), Math.Sin(i / 8.0 * Math.PI * 2), 40));
            GD.Print(FormattableString.Invariant($"probe {m.X:R} {m.Z:R}: {string.Join(" ", probe.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))}"));
            GetTree().Quit();
        }
    }
}
