using System.Text.Json;
using Backrooms.Core;
using Xunit;

namespace Backrooms.Core.Tests;

public class NetStateTests
{
    const string Welcome = """{"t":"welcome","id":7,"seed":4242,"players":[{"id":3,"name":"alice","color":"#e8c547","pose":[1,2,0.5,0,1.7,0]},{"id":4,"name":"bob","color":"#4fb3d9","pose":null}]}""";

    [Fact]
    public void WelcomeSetsIdSeedAndPeers()
    {
        var n = new NetState();
        n.Handle(Welcome, 1000);
        Assert.Equal(7, n.Id);
        Assert.Equal(4242, n.Seed);
        Assert.Equal(2, n.Peers.Count);
        Assert.Single(n.Peers[3].Snaps);
        Assert.Empty(n.Peers[4].Snaps);
    }

    [Fact]
    public void JoinLeaveAndGameMessagesRaiseEvents()
    {
        var n = new NetState();
        n.Handle(Welcome, 0);
        Peer? joined = null, left = null;
        JsonElement? game = null;
        n.Joined += p => joined = p;
        n.Left += p => left = p;
        n.Game += m => game = m;
        n.Handle("""{"t":"join","id":9,"name":"carol","color":"#d9534f"}""", 0);
        Assert.Equal("carol", joined?.Name);
        n.Handle("""{"t":"leave","id":3}""", 0);
        Assert.Equal("alice", left?.Name);
        Assert.False(n.Peers.ContainsKey(3));
        n.Handle("""{"t":"teleport","x":1,"z":2}""", 0);
        Assert.Equal("teleport", game?.GetProperty("t").GetString());
        n.Handle("not json", 0);
        n.Handle("""{"t":"full"}""", 0);
        Assert.True(n.Full);
    }

    [Fact]
    public void SamplesInterpolate120msBehindAndWrapYaw()
    {
        var n = new NetState();
        n.Handle(Welcome, 0);
        n.Handle("""{"t":"poses","p":[[4,0,0,3.1,0,1,0]]}""", 1000);
        n.Handle("""{"t":"poses","p":[[4,10,0,-3.1,0,3,2]]}""", 1100);
        n.Sample(1170); // 1050: halfway between the two snapshots
        var b = n.Peers[4];
        Assert.True(b.Visible);
        Assert.Equal(5, b.X, 6);
        Assert.Equal(2, b.V, 6);
        // yaw goes the short way round through ±π, not back through 0
        Assert.True(Math.Abs(b.Yaw) > 3.1, $"yaw {b.Yaw}");
        Assert.Equal(2, b.F);
        n.Sample(5000);
        Assert.Equal(10, b.X, 6);
    }

    [Fact]
    public void PosesAreThrottledAndRounded()
    {
        var n = new NetState();
        var first = n.Pose(0, 1.23456, 2, 0.1, 0, 1.234, 1);
        Assert.NotNull(first);
        var m = JsonDocument.Parse(first!).RootElement;
        Assert.Equal("pose", m.GetProperty("t").GetString());
        Assert.Equal(1.235, m.GetProperty("x").GetDouble());
        Assert.Equal(1.23, m.GetProperty("v").GetDouble());
        Assert.Null(n.Pose(50, 0, 0, 0, 0, 0));
        Assert.NotNull(n.Pose(70, 0, 0, 0, 0, 0));
        Assert.Contains("\"seed\":null", NetState.Hello("r", "n", null));
    }
}
