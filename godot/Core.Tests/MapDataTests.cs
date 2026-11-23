using Backrooms.Core;
using Xunit;

namespace Backrooms.Core.Tests;

public class MapDataTests
{
    [Fact]
    public void RevealPaintsOpenCellsAndStopsAtWalls()
    {
        var gen = new Gen(1);
        var (x, z) = gen.FindFree(0, 0, 2);
        var map = new MapData(gen);
        map.Reveal(x, z, 0, force: true);
        int gx = (int)Math.Floor(x / Gen.Cell), gz = (int)Math.Floor(z / Gen.Cell);
        Assert.True(map.IsSeen(gx, gz));
        // every discovered cell is within range, and nothing behind the first wall of a ray is seen
        int seen = 0;
        foreach (var ((ci, cj), t) in map.Tiles)
            for (int i = 0; i < t.Seen.Length; i++)
            {
                if (!t.Seen[i]) continue;
                seen++;
                double cx = (ci * MapTile.Size + i % MapTile.Size + 0.5) * Gen.Cell, cz = (cj * MapTile.Size + i / MapTile.Size + 0.5) * Gen.Cell;
                Assert.True(Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z)) < MapData.Range + 1);
                bool wall = t.Rgba[i * 4 + 3] == 245;
                Assert.Equal(gen.Solid(ci * MapTile.Size + i % MapTile.Size, cj * MapTile.Size + i / MapTile.Size), wall);
            }
        Assert.True(seen > 50);
    }

    [Fact]
    public void RevealIsThrottled()
    {
        var gen = new Gen(1);
        var map = new MapData(gen);
        map.Reveal(0.75, 1.25, 0.05);
        Assert.Empty(map.Tiles);
        map.Reveal(0.75, 1.25, 0.06);
        Assert.NotEmpty(map.Tiles);
    }

    [Fact]
    public void MarkersAreReachedInOrderAndCanBeRemoved()
    {
        var map = new MapData(new Gen(1));
        var a = map.AddMarker(10, 0);
        var b = map.AddMarker(20, 0);
        Assert.Equal(1, a.Id);
        Assert.NotEqual(a.Color, b.Color);
        Assert.Same(a, map.Target);
        Marker? reached = null;
        map.Reached += m => reached = m;
        map.CheckReached(10.5, 0.5);
        Assert.Same(a, reached);
        Assert.Same(b, map.Target);
        Assert.False(map.RemoveNear(0, 0, 5));
        Assert.True(map.RemoveNear(19, 0, 5));
        Assert.Null(map.Target);
    }
}
