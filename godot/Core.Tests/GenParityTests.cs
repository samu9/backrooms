// The C# generator must answer exactly like shared/gen.ts. The answers come from
// tests/fixtures/gen-golden.json.gz (npm run gen:fixtures). Every comparison is exact.
using System.IO.Compression;
using System.Text.Json;
using Backrooms.Core;
using Xunit;

namespace Backrooms.Core.Tests;

public sealed class Golden
{
    public static readonly Golden Instance = Load();

    public required JsonElement[] Seeds { get; init; }
    public required JsonElement[] Chunks { get; init; }

    static Golden Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "gen-golden.json.gz"))) dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("tests/fixtures/gen-golden.json.gz not found above " + AppContext.BaseDirectory + " (run npm run gen:fixtures)");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(dir.FullName, "tests", "fixtures", "gen-golden.json.gz")), CompressionMode.Decompress);
        var doc = JsonDocument.Parse(gz);
        return new Golden
        {
            Seeds = doc.RootElement.GetProperty("seeds").EnumerateArray().ToArray(),
            Chunks = doc.RootElement.GetProperty("chunks").EnumerateArray().ToArray(),
        };
    }

    public static IEnumerable<object[]> SeedIndices() => Instance.Seeds.Select((_, i) => new object[] { i });
    public static IEnumerable<object[]> ChunkIndices() => Instance.Chunks.Select((_, i) => new object[] { i });
}

public class GenParityTests
{
    static (Gen gen, JsonElement fx) Setup(int i)
    {
        var fx = Golden.Instance.Seeds[i];
        return (new Gen(fx.GetProperty("seed").GetInt32()), fx);
    }

    static bool Bit(byte[] a, int i) => (a[i >> 3] & (1 << (i & 7))) != 0;

    /// <summary>Collects mismatches and fails with the first few, so a drift points at a cell.</summary>
    sealed class Diff(string what)
    {
        readonly List<string> first = [];
        int count, total;

        public void Check(bool equal, Func<string> describe)
        {
            total++;
            if (equal) return;
            if (count++ < 5) first.Add(describe());
        }

        public void Assert()
        {
            Xunit.Assert.True(total > 0, $"{what}: nothing compared");
            Xunit.Assert.True(count == 0, $"{what}: {count}/{total} differ from gen.ts, first:\n  " + string.Join("\n  ", first));
        }
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void Hash3(int i)
    {
        var (gen, fx) = Setup(i);
        var d = new Diff($"seed {gen.Seed} hash3");
        foreach (var h in fx.GetProperty("hashes").EnumerateArray())
        {
            int x = h[0].GetInt32(), y = h[1].GetInt32(), z = h[2].GetInt32();
            double want = h[3].GetDouble(), got = gen.Hash3(x, y, z);
            d.Check(got == want, () => $"hash3({x}, {y}, {z}) = {got:R}, want {want:R}");
        }
        d.Assert();
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void SolidCeilingAndRail(int i)
    {
        var (gen, fx) = Setup(i);
        var heights = fx.GetProperty("heights").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var solid = new Diff($"seed {gen.Seed} solid");
        var ceil = new Diff($"seed {gen.Seed} ceilingAt");
        var rail = new Diff($"seed {gen.Seed} rail");
        int walls = 0, cells = 0;
        foreach (var w in fx.GetProperty("windows").EnumerateArray())
        {
            int gx0 = w.GetProperty("gx0").GetInt32(), gz0 = w.GetProperty("gz0").GetInt32(), n = w.GetProperty("w").GetInt32();
            var s = w.GetProperty("solid").GetBytesFromBase64();
            var r = w.GetProperty("rail").GetBytesFromBase64();
            var c = w.GetProperty("ceil").GetBytesFromBase64();
            for (int k = 0; k < n * n; k++)
            {
                int gx = gx0 + k % n, gz = gz0 + k / n;
                bool ws = Bit(s, k), gs = gen.Solid(gx, gz);
                solid.Check(gs == ws, () => $"solid({gx}, {gz}) = {gs}, want {ws}");
                double wh = heights[c[k]], gh = gen.CeilingAt(gx, gz);
                ceil.Check(gh == wh, () => $"ceilingAt({gx}, {gz}) = {gh:R}, want {wh:R}");
                bool wr = Bit(r, k), gr = gen.ZoneAt(gx, gz).Rail;
                rail.Check(gr == wr, () => $"zoneAt({gx}, {gz}).rail = {gr}, want {wr}");
                walls += ws ? 1 : 0;
                cells++;
            }
        }
        solid.Assert();
        ceil.Assert();
        rail.Assert();
        // a fixture of all walls or no walls would prove nothing
        Assert.InRange(walls, cells / 50, cells / 2);
        Assert.True(heights.Length >= 3, "the windows should cross zones with different ceilings");
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void Zones(int i)
    {
        var (gen, fx) = Setup(i);
        var d = new Diff($"seed {gen.Seed} zoneAt");
        var types = new HashSet<ZoneType>();
        foreach (var e in fx.GetProperty("zones").EnumerateArray())
        {
            int gx = e[0].GetInt32(), gz = e[1].GetInt32();
            var z = gen.ZoneAt(gx, gz);
            types.Add(z.Type);
            var got = new object[] { (int)z.Type, z.Id, z.S, z.Off, z.P, z.Pz, z.Pd, z.Dw, z.Split, z.Pillar, z.PillarSize, z.Partial, z.H, z.Rail };
            var want = new object[]
            {
                e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32(), e[5].GetInt32(), e[6].GetDouble(), e[7].GetDouble(), e[8].GetDouble(),
                e[9].GetInt32(), e[10].GetDouble(), e[11].GetDouble(), e[12].GetInt32(), e[13].GetBoolean(), e[14].GetDouble(), e[15].GetBoolean(),
            };
            d.Check(got.SequenceEqual(want), () => $"zoneAt({gx}, {gz}) = [{string.Join(", ", got)}], want [{string.Join(", ", want)}]");
        }
        d.Assert();
        Assert.Equal(6, types.Count);
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void LightKinds(int i)
    {
        var (gen, fx) = Setup(i);
        var l = fx.GetProperty("lights");
        int i0 = l.GetProperty("i0").GetInt32(), j0 = l.GetProperty("j0").GetInt32(), n = l.GetProperty("n").GetInt32();
        var kinds = l.GetProperty("kinds").GetBytesFromBase64();
        var d = new Diff($"seed {gen.Seed} lightKind");
        for (int j = 0; j < n; j++)
            for (int k = 0; k < n; k++)
            {
                int li = i0 + k, lj = j0 + j, want = kinds[j * n + k], got = gen.LightKind(li, lj);
                d.Check(got == want, () => $"lightKind({li}, {lj}) = {got}, want {want}");
            }
        d.Assert();
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void FindFreeAndFreeDistance(int i)
    {
        var (gen, fx) = Setup(i);
        var ff = new Diff($"seed {gen.Seed} findFree");
        foreach (var e in fx.GetProperty("findFree").EnumerateArray())
        {
            double x = e[0].GetDouble(), z = e[1].GetDouble();
            int clear = e[2].GetInt32();
            var want = (e[3].GetDouble(), e[4].GetDouble());
            var got = gen.FindFree(x, z, clear);
            ff.Check(got == want, () => $"findFree({x:R}, {z:R}, {clear}) = {got}, want {want}");
        }
        ff.Assert();

        var fd = new Diff($"seed {gen.Seed} freeDistance");
        foreach (var e in fx.GetProperty("freeDistance").EnumerateArray())
        {
            double x = e[0].GetDouble(), z = e[1].GetDouble(), dx = e[2].GetDouble(), dz = e[3].GetDouble(), want = e[4].GetDouble();
            double got = gen.FreeDistance(x, z, dx, dz, 60);
            fd.Check(got == want, () => $"freeDistance({x:R}, {z:R}, {dx:R}, {dz:R}) = {got:R}, want {want:R}");
        }
        fd.Assert();
    }

    [Theory, MemberData(nameof(Golden.SeedIndices), MemberType = typeof(Golden))]
    public void RngSequences(int i)
    {
        var (gen, fx) = Setup(i);
        var d = new Diff($"seed {gen.Seed} rng");
        foreach (var e in fx.GetProperty("rng").EnumerateArray())
        {
            int a = e[0].GetInt32(), b = e[1].GetInt32(), salt = e[2].GetInt32();
            var r = gen.Rng(a, b, salt);
            int k = 0;
            foreach (var v in e[3].EnumerateArray())
            {
                double want = v.GetDouble(), got = r.Next();
                int at = k++;
                d.Check(got == want, () => $"rng({a}, {b}, {salt}) #{at} = {got:R}, want {want:R}");
            }
        }
        d.Assert();
    }

    [Fact]
    public void JsRoundMatchesJavaScript()
    {
        // Math.round in JS: halves go up, including for negatives
        Assert.Equal(3, Gen.JsRound(2.5));
        Assert.Equal(-2, Gen.JsRound(-2.5));
        Assert.Equal(-3, Gen.JsRound(-2.51));
        Assert.Equal(0, Gen.JsRound(0.49999999999999994));
    }
}
