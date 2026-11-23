// The temporary sound of the desktop client: the recipes of web/src/audio.ts (which synthesises
// everything live with Web Audio) rendered offline, once, into mono sample buffers. Loops are
// rendered at lengths that hold whole cycles of every oscillator, and their noise is crossfaded
// across the seam, so they repeat without a click. Still no audio files.
//
// A stand-in: no HRTF, no live parameter changes inside a sound, fixed variations. The game plays
// these through Godot's 3D players and buses (Game/Audio/TempSoundscape.cs).
namespace Backrooms.Core.Audio;

public sealed class Clip(string name, float[] samples, bool loop)
{
    public string Name { get; } = name;
    public float[] Samples { get; } = samples;
    public bool Loop { get; } = loop;
    public double Seconds => (double)Samples.Length / SoundBank.Rate;
}

public static class SoundBank
{
    public const int Rate = 44100;

    // ---- tiny DSP kit ----
    sealed class Biquad
    {
        double b0, b1, b2, a1, a2, x1, x2, y1, y2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            (this.b0, this.b1, this.b2, this.a1, this.a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        // RBJ audio-EQ cookbook, as Web Audio's BiquadFilterNode
        public static Biquad Lowpass(double f, double q = 0.7071)
        {
            double w = 2 * Math.PI * f / Rate, al = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            return new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
        }

        public static Biquad Highpass(double f, double q = 0.7071)
        {
            double w = 2 * Math.PI * f / Rate, al = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            return new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
        }

        public static Biquad Bandpass(double f, double q)
        {
            double w = 2 * Math.PI * f / Rate, al = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            return new(al, 0, -al, 1 + al, -2 * c, 1 - al);
        }

        /// <summary>These coefficients, carrying over the previous filter's state (for sweeps).</summary>
        public Biquad With(Biquad prev)
        {
            (x1, x2, y1, y2) = (prev.x1, prev.x2, prev.y1, prev.y2);
            return this;
        }

        public double Run(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            (x2, x1, y2, y1) = (x1, x, y1, y);
            return y;
        }
    }

    static double Saw(double ph) => 2 * (ph - Math.Floor(ph + 0.5));
    static double Tri(double ph) => 1 - 4 * Math.Abs(ph - Math.Floor(ph + 0.5));
    static double Sine(double ph) => Math.Sin(2 * Math.PI * ph);

    /// <summary>Exponential ramp like AudioParam.exponentialRampToValueAtTime.</summary>
    static double Exp(double a, double b, double k) => a * Math.Pow(b / a, Math.Clamp(k, 0, 1));

    /// <summary>Crossfade the tail into the head so a looped buffer has no seam (length = n - fade).</summary>
    static float[] Seamless(double[] x, int fade)
    {
        int n = x.Length - fade;
        var o = new float[n];
        for (int i = 0; i < n; i++) o[i] = (float)x[i];
        for (int i = 0; i < fade; i++)
        {
            double k = (double)i / fade;
            o[i] = (float)(x[n + i] * (1 - k) + x[i] * k);
        }
        return o;
    }

    static float[] F(double[] x) => x.Select(v => (float)v).ToArray();

    static float[] Normalize(float[] x, double peak)
    {
        double m = x.Max(v => Math.Abs(v));
        if (m <= 0) return x;
        double k = peak / m;
        for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * k);
        return x;
    }

    // ---- the sounds (web/src/audio.ts) ----

    /// <summary>The mains hum: 60 Hz, the 120 Hz ballast hum and harmonics beating slowly, plus the ballasts' high whine. 5 s loop.</summary>
    static Clip Hum(Random r)
    {
        const double secs = 5, fade = 0.5;
        int n = (int)((secs + fade) * Rate);
        var x = new double[n];
        (double f, Func<double, double> w, double a)[] parts =
            [(60, Sine, 0.5), (120, Saw, 0.22), (120.4, Sine, 0.35), (180, Sine, 0.08), (240, Tri, 0.06), (360, Sine, 0.03)];
        var lp = Biquad.Lowpass(900);
        var bp = Biquad.Bandpass(7800, 12);
        // beating rates that fit the loop (k / 5 Hz)
        var lfo = parts.Select(_ => (1 + r.Next(2)) / secs).ToArray();
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, s = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                var (f, w, a) = parts[k];
                s += w(f * t) * (a + a * 0.3 * Sine(lfo[k] * t));
            }
            s += bp.Run(r.NextDouble() * 2 - 1) * 0.05;
            x[i] = lp.Run(s) * 0.05;
        }
        return new("hum", Normalize(Seamless(x, (int)(fade * Rate)), 0.5), true);
    }

    /// <summary>Room tone: brown noise under 180 Hz. 6 s loop.</summary>
    static Clip RoomTone(Random r)
    {
        const double secs = 6, fade = 1;
        int n = (int)((secs + fade) * Rate);
        var x = new double[n];
        var lp = Biquad.Lowpass(180);
        double last = 0;
        for (int i = 0; i < n; i++)
        {
            last = (last + 0.02 * (r.NextDouble() * 2 - 1)) / 1.02;
            x[i] = lp.Run(last * 3.5);
        }
        return new("roomtone", Normalize(Seamless(x, (int)(fade * Rate)), 0.6), true);
    }

    /// <summary>A light panel's buzz: a band-passed 120 Hz saw and its 240 Hz hum. 2 s loop.</summary>
    static Clip Buzz(Random r, int k)
    {
        const double secs = 2;
        int n = (int)(secs * Rate);
        var x = new double[n];
        var bp = Biquad.Bandpass(2400 + r.NextDouble() * 1600, 3);
        // two passes so the filter has settled at the loop point
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                x[i] = bp.Run(Saw(120 * t)) + 0.4 * Sine(240.5 * t);
            }
        return new($"buzz{k}", Normalize(F(x), 0.5), true);
    }

    /// <summary>Panting: noise through a 1.1 kHz band, breathing at 1.2 Hz. 5 s loop.</summary>
    static Clip Breath(Random r)
    {
        const double secs = 5, fade = 0.3;
        int n = (int)((secs + fade) * Rate);
        var x = new double[n];
        var bp = Biquad.Bandpass(1100, 1.4);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            x[i] = bp.Run(r.NextDouble() * 2 - 1) * (0.5 + 0.5 * Sine(1.2 * t));
        }
        return new("breath", Normalize(Seamless(x, (int)(fade * Rate)), 0.7), true);
    }

    /// <summary>The entity: a low detuned saw drone and rumbling noise, with a slow tremolo. 10 s loop.</summary>
    static Clip EntityDrone(Random r)
    {
        const double secs = 10, fade = 0.5;
        int n = (int)((secs + fade) * Rate);
        var x = new double[n];
        var lp = Biquad.Lowpass(340, 4);
        var bp = Biquad.Bandpass(160, 3);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double s = Saw(43.6 * t) + Saw(44.1 * t) + Saw(65.2 * t) + bp.Run(r.NextDouble() * 2 - 1);
            x[i] = lp.Run(s) * (0.6 + 0.35 * Sine(0.4 * t));
        }
        return new("entity", Normalize(Seamless(x, (int)(fade * Rate)), 0.8), true);
    }

    /// <summary>A muffled impact: low-passed noise burst and a falling low sine.</summary>
    static Clip Thump(Random r, string name, double dur, double cutoff)
    {
        int n = (int)((dur + 0.05) * Rate);
        var x = new double[n];
        var lp = Biquad.Lowpass(cutoff);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, k = t / dur;
            double env = t < 0.008 ? t / 0.008 : Exp(1, 0.0001, (t - 0.008) / (dur - 0.008));
            double f = Exp(90, 45, k);
            ph += f / Rate;
            x[i] = lp.Run(r.NextDouble() * 2 - 1) * env + Sine(ph) * 0.8 * Exp(1, 0.0001, k);
        }
        return new(name, Normalize(F(x), 0.9), false);
    }

    static Clip Knock(Random r)
    {
        int n = (int)(0.22 * Rate);
        var x = new double[n];
        var bp = Biquad.Bandpass(900, 2);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            ph += Exp(190, 120, t / 0.08) / Rate;
            double env = t < 0.004 ? Exp(0.0001, 0.7, t / 0.004) : Exp(0.7, 0.0001, (t - 0.004) / 0.156);
            x[i] = Sine(ph) * env + bp.Run(r.NextDouble() * 2 - 1) * Exp(0.4, 0.0001, t / 0.05);
        }
        return new("knock", Normalize(F(x), 0.9), false);
    }

    static Clip Zap(Random r)
    {
        int n = (int)(0.12 * Rate);
        var x = new double[n];
        var hp = Biquad.Highpass(2500);
        for (int i = 0; i < n; i++) x[i] = hp.Run(r.NextDouble() * 2 - 1) * Exp(0.25, 0.001, (double)i / Rate / 0.09);
        return new("zap", Normalize(F(x), 0.7), false);
    }

    /// <summary>A distant, swelling drone through a sweeping resonant low-pass.</summary>
    static Clip Drone(Random r, int k)
    {
        double dur = 7 + r.NextDouble() * 6;
        int n = (int)(dur * Rate);
        var x = new double[n];
        double b = new[] { 41.2, 43.65, 46.25, 55 }[k % 4];
        double[] mult = [1, 1.006, 1.498, 2.01];
        var phs = new double[mult.Length];
        var lp = Biquad.Lowpass(120, 6);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, u = t / dur;
            if (i % 64 == 0) lp = Biquad.Lowpass(u < 0.5 ? 120 + 960 * u : 600 - 1000 * (u - 0.5), 6).With(lp);
            double s = 0;
            for (int m = 0; m < mult.Length; m++)
            {
                phs[m] += b * mult[m] / Rate;
                s += Saw(phs[m]);
            }
            double env = u < 0.5 ? Exp(0.0001, 0.22, u * 2) : Exp(0.22, 0.0001, (u - 0.5) * 2);
            x[i] = lp.Run(s) * env;
        }
        return new($"drone{k}", Normalize(F(x), 0.6), false);
    }

    static Clip Creak(Random r, int k)
    {
        double dur = 0.8 + r.NextDouble() * 1.5, f0 = 60 + r.NextDouble() * 60, f1 = f0 * (0.6 + r.NextDouble() * 0.8);
        double am = 18 + r.NextDouble() * 20;
        int n = (int)((dur + 0.1) * Rate);
        var x = new double[n];
        var bp = Biquad.Bandpass(500 + r.NextDouble() * 700, 9);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            ph += (f0 + (f1 - f0) * Math.Min(1, t / dur)) / Rate;
            double env = t < 0.1 ? Exp(0.0001, 0.25, t / 0.1) : t < dur - 0.2 ? 0.25 : Exp(0.25, 0.0001, (t - dur + 0.2) / 0.2);
            env *= 1 + 0.8 * Sine(am * t); // stick-slip grain
            x[i] = bp.Run(Saw(ph)) * env;
        }
        return new($"creak{k}", Normalize(F(x), 0.8), false);
    }

    /// <summary>Breath through teeth right behind you: noise through two moving formants.</summary>
    static Clip Whisper(Random r, int k)
    {
        double dur = 1.6 + r.NextDouble();
        int n = (int)((dur + 0.1) * Rate);
        var x = new double[n];
        Biquad a = Biquad.Bandpass(700, 8), b = Biquad.Bandpass(1900, 8);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, u = Math.Min(1, t / dur);
            if (i % 64 == 0)
            {
                double fa = u < 0.6 ? 700 + 400 * u / 0.6 : 1100 - (1100 - 630) * (u - 0.6) / 0.4;
                double fb = u < 0.6 ? 1900 + 700 * u / 0.6 : 2600 - (2600 - 1710) * (u - 0.6) / 0.4;
                a = Biquad.Bandpass(fa, 8).With(a);
                b = Biquad.Bandpass(fb, 8).With(b);
            }
            double env = u < 0.35 ? Exp(0.0001, 0.09, u / 0.35) : Exp(0.09, 0.0001, (u - 0.35) / 0.65);
            double nz = r.NextDouble() * 2 - 1;
            x[i] = (a.Run(nz) + b.Run(nz)) * env;
        }
        return new($"whisper{k}", Normalize(F(x), 0.5), false);
    }

    /// <summary>Renders every clip. Deterministic for a given seed; takes a fraction of a second.</summary>
    public static Dictionary<string, Clip> Render(int seed = 1)
    {
        var r = new Random(seed);
        var all = new List<Clip>
        {
            Hum(r), RoomTone(r), Breath(r), EntityDrone(r),
            Thump(r, "step", 0.15, 450), Thump(r, "step_heavy", 0.32, 200), Thump(r, "slam", 1.2, 160),
            Knock(r), Zap(r),
        };
        for (int k = 0; k < 4; k++) all.Add(Buzz(r, k));
        for (int k = 0; k < 2; k++)
        {
            all.Add(Drone(r, k * 2 + r.Next(2)));
            all.Add(Creak(r, k));
            all.Add(Whisper(r, k));
        }
        // names of drones depend on the base picked; index them plainly
        var d = all.Where(c => c.Name.StartsWith("drone")).ToList();
        for (int i = 0; i < d.Count; i++) all[all.IndexOf(d[i])] = new Clip($"drone{i}", d[i].Samples, false);
        return all.ToDictionary(c => c.Name);
    }
}
