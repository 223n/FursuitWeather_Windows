using FursuitWeather.Core.Display;

namespace FursuitWeather.Core.Tests;

/// <summary>掲示のスライドの巡回を見る。</summary>
public sealed class DisplayRotationTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(DisplaySlide.Now, 15)]
    [InlineData(DisplaySlide.Hours, 20)]
    [InlineData(DisplaySlide.Days, 15)]
    [InlineData(DisplaySlide.National, 20)]
    [InlineData(DisplaySlide.Emergency, 20)]
    public void スライドの秒数は本体と同じ(DisplaySlide slide, int seconds)
    {
        Assert.Equal(S(seconds), DisplayRotation.Duration(slide));
    }

    [Fact]
    public void いまの判定から始める()
    {
        var state = DisplayRotation.Start(S(100));

        Assert.Equal(DisplaySlide.Now, state.Current);
        Assert.Equal(S(115), state.Deadline);
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void 期限までは進まない()
    {
        var state = DisplayRotation.Start(S(0));

        Assert.Same(state, DisplayRotation.Tick(state, S(14.9), emergency: false));
    }

    [Fact]
    public void 並びの順に巡って最初へ戻る()
    {
        var state = DisplayRotation.Start(S(0));
        var seen = new List<DisplaySlide> { state.Current };
        var now = S(0);

        for (var i = 0; i < 4; i++)
        {
            now = state.Deadline;
            state = DisplayRotation.Tick(state, now, emergency: false);
            seen.Add(state.Current);
        }

        Assert.Equal(
            [DisplaySlide.Now, DisplaySlide.Hours, DisplaySlide.Days, DisplaySlide.National, DisplaySlide.Now],
            seen);
    }

    [Fact]
    public void もしものときは全国の天気のあとに加わる()
    {
        var state = new RotationState { Current = DisplaySlide.National, Deadline = S(10) };

        state = DisplayRotation.Tick(state, S(10), emergency: true);

        Assert.Equal(DisplaySlide.Emergency, state.Current);
        Assert.Equal(S(30), state.Deadline);

        state = DisplayRotation.Tick(state, S(30), emergency: true);
        Assert.Equal(DisplaySlide.Now, state.Current);
    }

    [Fact]
    public void もしものときが出入りしても出しているスライドを保つ()
    {
        // Mac版の剰余の方式では、一周の長さが変わるたびに位置が飛んでいた
        var state = new RotationState { Current = DisplaySlide.Hours, Deadline = S(20) };

        var entered = DisplayRotation.Tick(state, S(5), emergency: true);
        var left = DisplayRotation.Tick(entered, S(6), emergency: false);

        Assert.Equal(DisplaySlide.Hours, entered.Current);
        Assert.Equal(DisplaySlide.Hours, left.Current);
        Assert.Equal(S(20), left.Deadline);
    }

    [Fact]
    public void 出していたもしものときが外れたら次へ進む()
    {
        var state = new RotationState { Current = DisplaySlide.Emergency, Deadline = S(100) };

        state = DisplayRotation.Tick(state, S(5), emergency: false);

        Assert.Equal(DisplaySlide.Now, state.Current);
        Assert.Equal(S(20), state.Deadline);
    }

    [Fact]
    public void 止めていてももしものときが外れたら次へ進む()
    {
        var state = new RotationState { Current = DisplaySlide.Emergency, Deadline = S(100), PausedUntil = S(300) };

        state = DisplayRotation.Tick(state, S(5), emergency: false);

        Assert.Equal(DisplaySlide.Now, state.Current);
        Assert.True(state.IsPaused);
    }

    [Fact]
    public void 手で送ると次へ進みそのスライドの秒数ののちに自動へ戻る()
    {
        var state = DisplayRotation.Start(S(0));

        state = DisplayRotation.Next(state, S(3), emergency: false);

        Assert.Equal(DisplaySlide.Hours, state.Current);
        Assert.Equal(S(23), state.Deadline);
        Assert.Equal(DisplaySlide.Days, DisplayRotation.Tick(state, S(23), emergency: false).Current);
    }

    [Fact]
    public void 止めると期限を過ぎても進まない()
    {
        var state = DisplayRotation.TogglePause(DisplayRotation.Start(S(0)), S(1));

        state = DisplayRotation.Tick(state, S(200), emergency: false);

        Assert.Equal(DisplaySlide.Now, state.Current);
        Assert.True(state.IsPaused);
    }

    [Fact]
    public void 一時停止は5分で自動的に解ける()
    {
        // 無人の端末で止まったままにしない
        var state = DisplayRotation.TogglePause(DisplayRotation.Start(S(0)), S(10));
        Assert.Equal(S(10) + DisplayRotation.PauseLimit, state.PausedUntil);

        var still = DisplayRotation.Tick(state, S(10) + DisplayRotation.PauseLimit - S(1), emergency: false);
        Assert.True(still.IsPaused);

        var resumed = DisplayRotation.Tick(state, S(310), emergency: false);
        Assert.False(resumed.IsPaused);
        Assert.Equal(DisplaySlide.Now, resumed.Current);

        // 解けたところから、いまのスライドを出し直す
        Assert.Equal(S(325), resumed.Deadline);
    }

    [Fact]
    public void もう一度押すと解ける()
    {
        var paused = DisplayRotation.TogglePause(DisplayRotation.Start(S(0)), S(1));

        var resumed = DisplayRotation.TogglePause(paused, S(8));

        Assert.False(resumed.IsPaused);
        Assert.Equal(S(23), resumed.Deadline);
    }

    [Fact]
    public void 止めているあいだに手で送っても止めたまま()
    {
        var paused = DisplayRotation.TogglePause(DisplayRotation.Start(S(0)), S(1));

        var next = DisplayRotation.Next(paused, S(2), emergency: false);

        Assert.Equal(DisplaySlide.Hours, next.Current);
        Assert.True(next.IsPaused);
        Assert.Equal(DisplaySlide.Hours, DisplayRotation.Tick(next, S(100), emergency: false).Current);
    }

    [Fact]
    public void スリープで期限を大きく過ぎても1つだけ進む()
    {
        var state = DisplayRotation.Start(S(0));

        state = DisplayRotation.Tick(state, S(3600), emergency: false);

        Assert.Equal(DisplaySlide.Hours, state.Current);
        Assert.Equal(S(3620), state.Deadline);
    }

    [Fact]
    public void 出してよいスライドの並び()
    {
        Assert.Equal(
            [DisplaySlide.Now, DisplaySlide.Hours, DisplaySlide.Days, DisplaySlide.National],
            DisplayRotation.ActiveSlides(emergency: false));
        Assert.Equal(DisplaySlide.Emergency, DisplayRotation.ActiveSlides(emergency: true)[^1]);
    }
}
