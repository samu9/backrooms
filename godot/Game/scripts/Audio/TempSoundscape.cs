using Backrooms.Core;
using Backrooms.Core.Audio;
using Godot;

namespace Backrooms.Audio;

/// <summary>What the game asks of the sound, mirroring web/src/audio.ts Soundscape. The temporary
/// implementation below is meant to be swapped for a better one later; callers only see this.</summary>
public interface ISoundscape
{
    bool Muted { get; }
    bool HunterEars { get; set; }
    void Start();
    void SetMuted(bool muted);
    /// <summary>Every frame, with the panels currently loaded (the nearest buzz).</summary>
    void Update(double x, double z, double yaw, IEnumerable<LightInfo> lights, double dt);
    void PlayerStep(double run);
    void Footstep(double x, double z, bool heavy);
    /// <summary>Keep a positional loop alive this frame: "entity" (drone) or "breath" (panting).</summary>
    void Presence(string key, string kind, double x, double z, double level = 1);
    void EndFrame();
    void OwnBreath(double level);
}

/// <summary>
/// Temporary sound: the clips of Core/Audio/SoundBank (rendered once at startup from the web's
/// recipes) played through Godot's 3D players. A "Verb" bus carries a big dead reverb; the master
/// bus a compressor. Lives inside the scene SubViewport so its 3D players hear from the camera.
/// Missing compared to the web: HRTF (Godot pans), live filter sweeps, wall muffling.
/// </summary>
public partial class TempSoundscape : Node3D, ISoundscape
{
    sealed class Loop
    {
        public required AudioStreamPlayer3D Player;
        public double Gain, Target;
        public bool Seen;
    }

    readonly Dictionary<string, AudioStreamWav> wav = [];
    readonly Dictionary<string, Loop> loops = [];
    readonly List<AudioStreamPlayer3D> spots = [];
    readonly List<(AudioStreamPlayer3D p, LightInfo? light, double gain, double crackle)> buzzers = [];
    readonly List<(double at, Action play)> scheduled = [];
    readonly RandomNumberGenerator rng = new();
    AudioStreamPlayer hum = null!, room = null!, own = null!, ownBreath = null!, drone = null!;
    int nextSpot, verbBus;
    double clock, nextEvent = double.PositiveInfinity, ownBreathGain, ownBreathNow, master;
    double lx, lz, lyaw;
    bool muted = true;

    public bool Muted => muted;
    public bool HunterEars { get; set; }

    public override void _Ready()
    {
        foreach (var (name, clip) in SoundBank.Render()) wav[name] = ToWav(clip);
        SetupBuses();
        hum = Flat("hum", -16, true);
        room = Flat("roomtone", -14, true);
        own = Flat("step", -10, false);
        ownBreath = Flat("breath", -80, true);
        drone = Flat("drone0", -12, false, "Verb");
        for (int i = 0; i < 4; i++)
        {
            var p = Spatial($"buzz{i}", true, unit: 1.2f);
            p.VolumeDb = -80;
            buzzers.Add((p, null, 0, 0));
        }
        for (int i = 0; i < 16; i++) spots.Add(Spatial("step", false, unit: 2));
        AudioServer.SetBusVolumeDb(0, -80);
    }

    /// <summary>For checks: what is sounding right now.</summary>
    public string Describe() =>
        $"buzzers {buzzers.Count(b => b.light != null)}/4, loops [{string.Join(", ", loops.Select(l => $"{l.Key} {l.Value.Gain:0.00}"))}], own breath {ownBreathNow:0.00}, master {master:0.00}, clips {wav.Count}";

    static AudioStreamWav ToWav(Clip c)
    {
        var bytes = new byte[c.Samples.Length * 2];
        for (int i = 0; i < c.Samples.Length; i++)
        {
            short v = (short)Math.Clamp(Math.Round(c.Samples[i] * 32767), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = SoundBank.Rate, Stereo = false, Data = bytes,
            LoopMode = c.Loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
            LoopBegin = 0, LoopEnd = c.Loop ? c.Samples.Length : 0,
        };
    }

    void SetupBuses()
    {
        verbBus = AudioServer.GetBusIndex("Verb");
        if (verbBus < 0)
        {
            AudioServer.AddBus();
            verbBus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(verbBus, "Verb");
            AudioServer.SetBusSend(verbBus, "Master");
            // a large, dead, carpeted space: dense reverb with absorbed highs
            AudioServer.AddBusEffect(verbBus, new AudioEffectReverb { RoomSize = 0.8f, Damping = 0.75f, Wet = 0.35f, Dry = 0.9f, Hipass = 0.05f });
            AudioServer.AddBusEffect(0, new AudioEffectCompressor { Threshold = -18, Ratio = 3 });
        }
    }

    AudioStreamPlayer Flat(string clip, float db, bool play, string bus = "Master")
    {
        var p = new AudioStreamPlayer { Stream = wav[clip], VolumeDb = db, Bus = bus };
        AddChild(p);
        if (play) p.Play();
        return p;
    }

    AudioStreamPlayer3D Spatial(string clip, bool play, float unit)
    {
        var p = new AudioStreamPlayer3D
        {
            Stream = wav[clip], Bus = "Verb", UnitSize = unit, MaxDistance = 45,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            AttenuationFilterCutoffHz = 6000, AttenuationFilterDb = -12,
        };
        AddChild(p);
        if (play) p.Play();
        return p;
    }

    static float Db(double linear) => linear <= 1e-5 ? -80 : (float)(20 * Math.Log10(linear));

    public void Start()
    {
        SetMuted(false);
        nextEvent = clock + 6;
    }

    public void SetMuted(bool m) => muted = m;

    /// <summary>A one-shot at a position (a pooled 3D player).</summary>
    void Spot(string clip, double x, double y, double z, double gain, double pitch = 1)
    {
        var p = spots[nextSpot++ % spots.Count];
        p.Stream = wav[clip];
        p.Position = new Vector3((float)x, (float)y, (float)z);
        p.VolumeDb = Db(gain);
        p.PitchScale = (float)pitch;
        p.Play();
    }

    void At(double delay, Action a) => scheduled.Add((clock + delay, a));

    public void PlayerStep(double run)
    {
        own.Stream = wav[run > 0.5 ? "step_heavy" : "step"];
        own.VolumeDb = Db(0.25 + 0.3 * run);
        own.PitchScale = rng.RandfRange(0.92f, 1.08f);
        own.Play();
    }

    public void Footstep(double x, double z, bool heavy)
    {
        if (heavy) Spot("step_heavy", x, 0.1, z, 0.9, 0.8); // an entity's step: slow, too heavy
        else Spot("step", x, 0.1, z, 0.45 * (HunterEars ? 1.8 : 1), rng.RandfRange(0.9f, 1.1f));
    }

    public void Presence(string key, string kind, double x, double z, double level = 1)
    {
        if (!loops.TryGetValue(key, out var l))
        {
            var p = Spatial(kind == "entity" ? "entity" : "breath", true, unit: kind == "entity" ? 3 : 1.2f);
            p.VolumeDb = -80;
            loops[key] = l = new Loop { Player = p };
        }
        l.Seen = true;
        l.Player.Position = l.Player.Position.Lerp(new Vector3((float)x, kind == "entity" ? 1.8f : 1.5f, (float)z), 0.3f);
        l.Target = (kind == "entity" ? 0.8 : 0.3 * (HunterEars ? 2.2 : 1)) * level;
    }

    public void EndFrame()
    {
        foreach (var (k, l) in loops.ToList())
        {
            if (!l.Seen) l.Target = 0;
            l.Seen = false;
            if (l.Target == 0 && l.Gain < 0.002)
            {
                l.Player.QueueFree();
                loops.Remove(k);
            }
        }
    }

    public void OwnBreath(double level) => ownBreathGain = 0.2 * level;

    public void Update(double x, double z, double yaw, IEnumerable<LightInfo> lights, double dt)
    {
        (lx, lz, lyaw) = (x, z, yaw);
        clock += dt;
        // master: fade in and out like the web's setTargetAtTime(…, 0.3)
        master += ((muted ? 0 : 0.9) - master) * Math.Min(1, dt / 0.3);
        AudioServer.SetBusVolumeDb(0, Db(master));

        // nearest lit panels buzz (flickering ones preferred: they crackle)
        var near = lights.Where(l => l.Kind >= 2)
            .Select(l => (d: (l.X - x) * (l.X - x) + (l.Z - z) * (l.Z - z) - (l.Kind == 3 ? 30 : 0), l))
            .Where(t => t.d < 200).OrderBy(t => t.d).Take(4).Select(t => t.l).ToList();
        for (int i = 0; i < buzzers.Count; i++)
        {
            var (p, light, gain, crackle) = buzzers[i];
            LightInfo? l = i < near.Count ? near[i] : null;
            if (l != light && l is { } nl) p.Position = new Vector3((float)nl.X, (float)(nl.Y - 0.05), (float)nl.Z);
            double target = l != null ? 0.12 : 0;
            if (l is { Kind: 3 } fl)
            {
                crackle -= dt;
                if (crackle < 0)
                {
                    crackle = 0.04 + rng.Randf() * 0.25;
                    target = rng.Randf() < 0.5 ? 0 : 0.3;
                    if (rng.Randf() < 0.08) Spot("zap", fl.X, fl.Y, fl.Z, 0.4);
                    gain = target; // a crackle snaps, it doesn't glide
                }
                else target = gain;
            }
            gain += (target - gain) * Math.Min(1, dt / 0.03);
            p.VolumeDb = Db(gain);
            buzzers[i] = (p, l, gain, crackle);
        }

        foreach (var l in loops.Values)
        {
            l.Gain += (l.Target - l.Gain) * Math.Min(1, dt / 0.2);
            l.Player.VolumeDb = Db(l.Gain);
        }
        ownBreathNow += (ownBreathGain - ownBreathNow) * Math.Min(1, dt / 0.4);
        ownBreath.VolumeDb = Db(ownBreathNow);

        for (int i = scheduled.Count - 1; i >= 0; i--)
            if (scheduled[i].at <= clock)
            {
                var a = scheduled[i].play;
                scheduled.RemoveAt(i);
                a();
            }

        if (clock > nextEvent)
        {
            nextEvent = clock + 7 + rng.Randf() * 16;
            Event();
        }
    }

    (double X, double Z) Around(double rmin, double rmax)
    {
        double a = rng.Randf() * Math.PI * 2, r = rmin + rng.Randf() * (rmax - rmin);
        return (lx + Math.Cos(a) * r, lz + Math.Sin(a) * r);
    }

    /// <summary>Something, somewhere else (web Soundscape.event).</summary>
    void Event()
    {
        double r = rng.Randf();
        if (r < 0.3)
        {
            // footsteps that are not yours, walking past at a distance
            var (px, pz) = Around(12, 26);
            double a = rng.Randf() * Math.PI * 2;
            int n = 6 + rng.RandiRange(0, 9);
            bool fast = rng.Randf() < 0.25f;
            for (int i = 0; i < n; i++)
            {
                double qx = px + Math.Cos(a) * i * 0.7, qz = pz + Math.Sin(a) * i * 0.7;
                At(i * (fast ? 0.32 : 0.58) + rng.Randf() * 0.04, () => Spot("step", qx, 0.1, qz, 0.7, rng.RandfRange(0.9f, 1.1f)));
            }
        }
        else if (r < 0.48)
        {
            // knocking inside a wall
            var (px, pz) = Around(5, 16);
            int n = 2 + rng.RandiRange(0, 3);
            double t = 0;
            for (int i = 0; i < n; i++, t += 0.22 + rng.Randf() * 0.15) At(t, () => Spot("knock", px, 1.2, pz, 0.8, rng.RandfRange(0.95f, 1.05f)));
        }
        else if (r < 0.6)
        {
            // a heavy door slamming far away
            var (px, pz) = Around(25, 40);
            Spot("slam", px, 1.2, pz, 1.0);
        }
        else if (r < 0.75)
        {
            drone.Stream = wav[rng.Randf() < 0.5f ? "drone0" : "drone1"];
            drone.Play();
        }
        else if (r < 0.87)
        {
            var (px, pz) = Around(8, 30);
            Spot(rng.Randf() < 0.5f ? "creak0" : "creak1", px, 1.2, pz, 0.8);
        }
        else
        {
            // breathing / whisper right behind you (forward is -sin yaw, -cos yaw)
            double bx = lx + Math.Sin(lyaw) * 1.6 + (rng.Randf() - 0.5), bz = lz + Math.Cos(lyaw) * 1.6 + (rng.Randf() - 0.5);
            Spot(rng.Randf() < 0.5f ? "whisper0" : "whisper1", bx, 1.6, bz, 0.9);
        }
    }
}
