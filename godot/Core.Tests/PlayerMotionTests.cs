using Backrooms.Core;
using Xunit;

namespace Backrooms.Core.Tests;

public class PlayerMotionTests
{
    /// <summary>A spawn with a wall straight ahead (towards -z, yaw 0), a few metres away.</summary>
    static (Gen gen, PlayerMotion p, double wallZ) FacingAWall()
    {
        var gen = new Gen(1);
        for (int gx = -200; gx < 200; gx += 7)
            for (int gz = -200; gz < 200; gz += 7)
            {
                var p = new PlayerMotion(gen);
                var (x, z) = gen.FindFree(gx * Gen.Cell, gz * Gen.Cell, 2);
                double d = gen.FreeDistance(x, z, 0, -1, 30);
                if (d < 3 || d > 10 || p.Collides(x, z)) continue;
                p.X = x;
                p.Z = z;
                return (gen, p, z - d);
            }
        throw new InvalidOperationException("no spawn facing a wall");
    }

    [Fact]
    public void WalkingIntoAWallStopsAtTheWall()
    {
        var (_, p, wallZ) = FacingAWall();
        double x0 = p.X;
        for (int i = 0; i < 600; i++) p.Step(1 / 60.0, 0, -1, true, i / 60.0);
        Assert.False(p.Collides(p.X, p.Z));
        Assert.True(p.Z > wallZ, $"went through the wall: z {p.Z}, wall at {wallZ}");
        // pressed against something: a few centimetres further would collide
        Assert.True(p.Collides(p.X, p.Z - 0.05), $"stopped short at z {p.Z}, wall at {wallZ}");
        Assert.Equal(x0, p.X, 6);
    }

    [Fact]
    public void LowFrameRatesDontTunnelThroughWalls()
    {
        var (_, p, wallZ) = FacingAWall();
        p.Sprint = 1;
        p.Vz = -PlayerMotion.Run;
        for (int i = 0; i < 20; i++) p.Step(0.1, 0, -1, true, i * 0.1); // 10 fps, full sprint
        Assert.True(p.Z > wallZ, $"went through the wall: z {p.Z}, wall at {wallZ}");
    }

    [Fact]
    public void NoclipWalksThroughWalls()
    {
        var (_, p, wallZ) = FacingAWall();
        p.Noclip = true;
        for (int i = 0; i < 300; i++) p.Step(1 / 60.0, 0, -1, false, i / 60.0);
        Assert.True(p.Z < wallZ - 1);
    }

    [Fact]
    public void SprintBuildsUpAndWidensTheView()
    {
        var gen = new Gen(1);
        var p = new PlayerMotion(gen) { Noclip = true };
        MotionResult r = default;
        for (int i = 0; i < 120; i++) r = p.Step(1 / 60.0, 0, -1, true, 0);
        Assert.True(p.Sprint > 0.9);
        Assert.True(r.Camera.Fov > p.BaseFov);
        // backpedalling never sprints
        var q = new PlayerMotion(gen) { Noclip = true };
        for (int i = 0; i < 120; i++) q.Step(1 / 60.0, 0, 1, true, 0);
        Assert.True(q.Sprint < 0.01);
    }

    [Fact]
    public void PitchIsClamped()
    {
        var p = new PlayerMotion(new Gen(1));
        p.Look(0, -1e6);
        Assert.Equal(PlayerMotion.MaxPitch, p.Pitch);
    }
}
