// ChunkData.Build must make what the web client's buildChunk makes: the same texture bytes,
// the same panel list and the same geometry, float for float (compared through FNV-1a hashes
// of the attribute arrays, see tests/parity/dump.ts).
using System.Runtime.InteropServices;
using Backrooms.Core;
using Xunit;

namespace Backrooms.Core.Tests;

public class ChunkParityTests
{
    static uint Fnv(ReadOnlySpan<byte> bytes)
    {
        uint h = 0x811c9dc5;
        foreach (byte b in bytes) h = unchecked((h ^ b) * 0x01000193);
        return h;
    }

    static uint Hash(List<float> a) => Fnv(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(a)));
    static uint Hash(List<int> a) => Fnv(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(a)));

    static string FirstDiff(byte[] got, byte[] want, int width)
    {
        if (got.Length != want.Length) return $"length {got.Length}, want {want.Length}";
        for (int i = 0; i < got.Length; i++)
            if (got[i] != want[i]) return $"first at texel ({i / 4 % width}, {i / 4 / width}) channel {"RGBA"[i % 4]}: {got[i]}, want {want[i]}";
        return "";
    }

    [Theory, MemberData(nameof(Golden.ChunkIndices), MemberType = typeof(Golden))]
    public void MatchesTheWebBuilder(int i)
    {
        var fx = Golden.Instance.Chunks[i];
        int seed = fx.GetProperty("seed").GetInt32(), ci = fx.GetProperty("ci").GetInt32(), cj = fx.GetProperty("cj").GetInt32();
        string at = $"seed {seed} chunk ({ci}, {cj})";
        var c = ChunkData.Build(new Gen(seed), ci, cj);

        Assert.True(BitConverter.IsLittleEndian);
        Assert.Equal("", FirstDiff(c.Occupancy, fx.GetProperty("occ").GetBytesFromBase64(), Gen.Tex));
        Assert.Equal("", FirstDiff(c.Lights, fx.GetProperty("lights").GetBytesFromBase64(), ChunkData.LightTex));
        Assert.Equal(fx.GetProperty("lightBase")[0].GetInt32(), c.LightBaseI);
        Assert.Equal(fx.GetProperty("lightBase")[1].GetInt32(), c.LightBaseJ);

        var want = fx.GetProperty("lightList").EnumerateArray()
            .Select(e => new LightInfo(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble(), e[3].GetInt32())).ToList();
        Assert.Equal(want, c.LightList);

        CheckGeometry($"{at} level", c.Level, fx.GetProperty("vertices").GetInt32(), fx.GetProperty("indices").GetInt32(), fx.GetProperty("hash"));

        var wantSpots = fx.GetProperty("propSpots").EnumerateArray()
            .Select(e => new PropSpot(e[0].GetDouble(), e[1].GetDouble(), e[2].GetString()!)).ToList();
        Assert.Equal(wantSpots, c.PropSpots);
        var p = fx.GetProperty("props");
        if (p.ValueKind == System.Text.Json.JsonValueKind.Null) Assert.Equal(0, c.Props.VertexCount);
        else CheckGeometry($"{at} props", c.Props, p.GetProperty("vertices").GetInt32(), p.GetProperty("indices").GetInt32(), p.GetProperty("hash"));
    }

    static void CheckGeometry(string at, GeoBuilder g, int vertices, int indices, System.Text.Json.JsonElement h)
    {
        Assert.True(vertices == g.VertexCount, $"{at}: {g.VertexCount} vertices, want {vertices}");
        Assert.True(indices == g.Idx.Count, $"{at}: {g.Idx.Count} indices, want {indices}");
        var got = new Dictionary<string, uint>
        {
            ["position"] = Hash(g.Pos), ["normal"] = Hash(g.Nrm), ["aCol"] = Hash(g.Col),
            ["aA"] = Hash(g.A), ["aB"] = Hash(g.B), ["index"] = Hash(g.Idx),
        };
        foreach (var (name, value) in got)
            Assert.True(h.GetProperty(name).GetUInt32() == value, $"{at}: {name} hash differs from the web builder");
    }
}
