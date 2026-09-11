using FursuitWeather.Core.Display;

namespace FursuitWeather.Core.Tests;

/// <summary>焼き付き対策のずらし方と、モニターの選び方を見る。</summary>
public sealed class DisplayScreenTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(179, 0, 0)]
    [InlineData(180, 1, 0)]
    [InlineData(360, 1, 1)]
    [InlineData(540, 0, 1)]
    [InlineData(720, 0, 0)]
    [InlineData(-60, 0, 0)]
    public void 焼き付き対策で3分ごとに4か所を巡る(int seconds, int x, int y)
    {
        Assert.Equal((x, y), BurnInShift.Offset(TimeSpan.FromSeconds(seconds)));
    }

    private static readonly DisplayMonitor Primary = new("\\\\?\\DISPLAY#A", IsPrimary: true);
    private static readonly DisplayMonitor Second = new("\\\\?\\DISPLAY#B", IsPrimary: false);

    [Fact]
    public void 選んだモニターがあればそれに出す()
    {
        var decision = MonitorChoice.Resolve(Second.Id.ToUpperInvariant(), [Primary, Second]);

        Assert.Equal(Second, decision.Monitor);
        Assert.False(decision.FellBack);
    }

    [Fact]
    public void 選んだモニターが無ければ主モニターへ落とす()
    {
        var decision = MonitorChoice.Resolve("\\\\?\\DISPLAY#Z", [Second, Primary]);

        Assert.Equal(Primary, decision.Monitor);
        Assert.True(decision.FellBack);
    }

    [Fact]
    public void 選んでいなければ主モニターへ出す()
    {
        var decision = MonitorChoice.Resolve(null, [Second, Primary]);

        Assert.Equal(Primary, decision.Monitor);
        Assert.False(decision.FellBack);
    }

    [Fact]
    public void 主モニターが分からなければ先頭へ出す()
    {
        Assert.Equal(Second, MonitorChoice.Resolve(null, [Second]).Monitor);
    }

    [Fact]
    public void モニターが1台も無ければ出さない()
    {
        var decision = MonitorChoice.Resolve("x", []);

        Assert.Null(decision.Monitor);
        Assert.False(decision.FellBack);
    }
}
