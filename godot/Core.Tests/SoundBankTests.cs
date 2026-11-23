using System.Diagnostics;
using Backrooms.Core.Audio;
using Xunit;

namespace Backrooms.Core.Tests;

public class SoundBankTests
{
    static readonly Dictionary<string, Clip> Bank = SoundBank.Render();

    [Fact]
    public void EverySoundTheGameAsksForExists()
    {
        foreach (var name in new[] { "hum", "roomtone", "breath", "entity", "step", "step_heavy", "slam", "knock", "zap", "buzz0", "buzz3", "drone0", "drone1", "creak0", "creak1", "whisper0", "whisper1" })
            Assert.True(Bank.ContainsKey(name), name);
    }

    [Fact]
    public void ClipsAreAudibleAndInRange()
    {
        foreach (var c in Bank.Values)
        {
            Assert.All(c.Samples, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1, c.Name));
            double rms = Math.Sqrt(c.Samples.Average(s => (double)s * s));
            Assert.True(rms > 0.005, $"{c.Name} is silent (rms {rms})");
            Assert.InRange(c.Seconds, 0.05, 14);
        }
    }

    [Fact]
    public void LoopsHaveNoSeam()
    {
        foreach (var c in Bank.Values.Where(c => c.Loop))
        {
            // the jump across the loop point is no bigger than the clip's ordinary sample-to-sample steps
            var s = c.Samples;
            double typical = 0;
            for (int i = 1; i < s.Length; i++) typical = Math.Max(typical, Math.Abs(s[i] - s[i - 1]));
            Assert.True(Math.Abs(s[0] - s[^1]) <= typical * 1.01 + 1e-6, $"{c.Name}: seam {Math.Abs(s[0] - s[^1])} vs max step {typical}");
        }
    }

    [Fact]
    public void RendersQuicklyAndDeterministically()
    {
        var sw = Stopwatch.StartNew();
        var again = SoundBank.Render();
        Assert.True(sw.Elapsed.TotalSeconds < 5, $"render took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(Bank["hum"].Samples, again["hum"].Samples);
    }
}
