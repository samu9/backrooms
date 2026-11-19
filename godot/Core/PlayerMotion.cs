// First-person movement and camera feel, ported from step() in web/src/main.ts: momentum, a sprint
// that builds up, sub-stepped collision against the occupancy grid that slides along walls, head
// bob, handheld sway, strafe lean and FOV widening. No engine types: Game/PlayerNode feeds it input
// and applies the CameraPose. Hunt-only parts (stamina gate, entity speed, phasing) come with M8.
namespace Backrooms.Core;

/// <summary>Camera position (metres) and rotation (radians, YXZ order), vertical FOV in degrees.</summary>
public readonly record struct CameraPose(double X, double Y, double Z, double Pitch, double Yaw, double Roll, double Fov);

public readonly record struct MotionResult(double Moving, bool Footstep, CameraPose Camera);

public sealed class PlayerMotion(Gen gen)
{
    public const double Walk = 1.7;
    public const double Run = 5.6;
    public const double NoclipSpeed = 7;
    public const double Eye = 1.62;
    public const double Radius = 0.26;
    public const double LookSensitivity = 0.0022; // radians per mouse count
    public const double MaxPitch = 1.45;

    public double BaseFov = 66;
    public double X, Z, Yaw, Pitch, Vx, Vz, Bob, Sprint;
    public bool Noclip;

    public bool Collides(double x, double z)
    {
        const double R = Radius, C = Gen.Cell;
        int g0x = (int)Math.Floor((x - R) / C), g1x = (int)Math.Floor((x + R) / C);
        int g0z = (int)Math.Floor((z - R) / C), g1z = (int)Math.Floor((z + R) / C);
        for (int gz = g0z; gz <= g1z; gz++)
            for (int gx = g0x; gx <= g1x; gx++)
            {
                if (!gen.Solid(gx, gz)) continue;
                double cx = Math.Max(gx * C, Math.Min(x, (gx + 1) * C));
                double cz = Math.Max(gz * C, Math.Min(z, (gz + 1) * C));
                if ((cx - x) * (cx - x) + (cz - z) * (cz - z) < R * R) return true;
            }
        return false;
    }

    public void Look(double dx, double dy)
    {
        Yaw -= dx * LookSensitivity;
        Pitch = Math.Clamp(Pitch - dy * LookSensitivity, -MaxPitch, MaxPitch);
    }

    /// <param name="ix">strafe: -1 left, +1 right</param>
    /// <param name="iz">-1 forward, +1 back</param>
    /// <param name="time">seconds since start, for the handheld sway</param>
    public MotionResult Step(double dt, int ix, int iz, bool runHeld, double time)
    {
        bool input = ix != 0 || iz != 0;
        // sprint builds up over ~half a second and dies off faster; only really works going forward
        bool wantRun = input && iz <= 0 && runHeld;
        Sprint += ((wantRun ? 1 : 0) - Sprint) * (1 - Math.Exp(-dt * (wantRun ? 2.6 : 5)));
        bool ghost = Noclip;
        double speed = Noclip ? NoclipSpeed : Walk + (Run - Walk) * Sprint;
        if (iz > 0) speed *= 0.65; // backpedalling
        else if (iz == 0 && ix != 0) speed *= 0.85; // pure strafe
        double il = Math.Sqrt(ix * ix + iz * iz);
        if (il == 0) il = 1;
        double s = Math.Sin(Yaw), c = Math.Cos(Yaw);
        double tx = input ? (ix * c + iz * s) / il * speed : 0;
        double tz = input ? (-ix * s + iz * c) / il * speed : 0;
        // quick to start, a little slower to stop: momentum without feeling floaty
        double k = 1 - Math.Exp(-dt * (input ? 9 : 6.5));
        Vx += (tx - Vx) * k;
        Vz += (tz - Vz) * k;
        // sub-step so a sprint at a low frame rate can't skip through a 0.5 m wall
        double dist = Math.Sqrt(Vx * Vx + Vz * Vz) * dt;
        int n = Math.Max(1, (int)Math.Ceiling(dist / 0.12));
        double travelled = 0;
        for (int i = 0; i < n; i++)
        {
            double ox = X, oz = Z;
            double nx = X + Vx * dt / n;
            if (ghost || !Collides(nx, Z)) X = nx;
            else Vx *= 0.2; // slide along the wall instead of stopping dead
            double nz = Z + Vz * dt / n;
            if (ghost || !Collides(X, nz)) Z = nz;
            else Vz *= 0.2;
            travelled += Math.Sqrt((X - ox) * (X - ox) + (Z - oz) * (Z - oz));
        }
        double moving = dt > 0 ? travelled / dt : 0;

        // head bob: one cycle per step, strides lengthen when running
        double stride = 0.62 + 0.11 * moving;
        double before = Math.Floor(Bob);
        Bob += travelled / stride;
        bool footstep = Math.Floor(Bob) != before && moving > 0.4;
        double amt = Math.Min(1, moving / Walk);
        double ph = Bob * Math.PI;
        double bobY = (Math.Abs(Math.Sin(ph)) - 0.6) * (0.045 + 0.05 * Sprint) * amt;
        double bobX = Math.Sin(ph) * (0.022 + 0.012 * Sprint) * amt;
        // lean into strafes
        double strafeV = Vx * c - Vz * s;
        // handheld camcorder: slow drift always
        double Sway(double f, double p) => Math.Sin(time * f + p) * 0.6 + Math.Sin(time * f * 2.3 + p * 1.7) * 0.4;
        var pose = new CameraPose(
            X + bobX * c,
            Eye + bobY - 0.04 * Sprint,
            Z - bobX * s,
            Pitch + Sway(0.7, 1) * 0.006 - Math.Abs(Math.Sin(ph)) * 0.008 * Sprint * amt,
            Yaw + Sway(0.5, 4) * 0.008,
            Sway(0.6, 2) * 0.006 + bobX * 0.35 - strafeV * 0.006,
            // FOV widens as the sprint builds: the corridor stretches away from you
            BaseFov + 9 * Sprint * Math.Min(1, moving / Run));
        return new MotionResult(moving, footstep, pose);
    }
}
