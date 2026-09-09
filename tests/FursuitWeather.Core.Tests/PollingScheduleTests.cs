using FursuitWeather.Core.Polling;

namespace FursuitWeather.Core.Tests;

/// <summary>取りに行く時期の判定を見る。</summary>
public sealed class PollingScheduleTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = PollInterval.Forecast;

    [Fact]
    public void 一度も成功していなければすぐ取りに行く()
    {
        var schedule = new PollingSchedule();

        Assert.True(schedule.IsDue(Interval, Origin, TimeSpan.Zero));
    }

    [Fact]
    public void 間隔に達していなければ取りに行かない()
    {
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.False(schedule.IsDue(Interval, Origin.AddMinutes(10), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void 間隔に達したら取りに行く()
    {
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.True(schedule.IsDue(Interval, Origin.AddMinutes(11), TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void 単調時刻が止まっていても壁時計で救われる()
    {
        // .NET 11 では TickCount64 がスリープ中の時間を含まなくなる。
        // 長く眠ったあと、単調時刻はほとんど進んでいない
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.True(schedule.IsDue(Interval, Origin.AddHours(8), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void 壁時計が巻き戻っても単調時刻で救われる()
    {
        // 利用者が時計を戻した場合
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.True(schedule.IsDue(Interval, Origin.AddHours(-3), TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void 壁時計が巻き戻り単調時刻も足りなければ取りに行かない()
    {
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.False(schedule.IsDue(Interval, Origin.AddHours(-3), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void 失敗のあとは待ち時間が明けるまで試さない()
    {
        var schedule = new PollingSchedule()
            .Succeeded(Origin, TimeSpan.Zero)
            .Failed(Origin.AddMinutes(11), TimeSpan.FromMinutes(11));

        Assert.False(schedule.IsDue(Interval, Origin.AddMinutes(11.5), TimeSpan.FromMinutes(11.5)));
        Assert.True(schedule.IsDue(Interval, Origin.AddMinutes(13), TimeSpan.FromMinutes(12.5)));
    }

    [Fact]
    public void 失敗の待ち時間も単調時刻が止まれば壁時計で明ける()
    {
        // 失敗した直後に長く眠った場合。単調時刻は進まないが壁時計は進む。
        // ここを単調時刻だけで見ると、待ち時間が永久に明けず取りに行けなくなる
        var schedule = new PollingSchedule()
            .Succeeded(Origin, TimeSpan.Zero)
            .Failed(Origin.AddMinutes(11), TimeSpan.FromMinutes(11));

        Assert.True(schedule.IsDue(Interval, Origin.AddHours(8), TimeSpan.FromMinutes(11.1)));
    }

    [Fact]
    public void 失敗の待ち時間は両方が未達なら明けない()
    {
        var schedule = new PollingSchedule()
            .Succeeded(Origin, TimeSpan.Zero)
            .Failed(Origin.AddMinutes(11), TimeSpan.FromMinutes(11));

        Assert.False(schedule.IsDue(Interval, Origin.AddMinutes(11.5), TimeSpan.FromMinutes(11.5)));
    }

    [Fact]
    public void 失敗が続くと待ち時間が倍になる()
    {
        var s1 = new PollingSchedule().Failed(Origin, TimeSpan.Zero);
        var s2 = s1.Failed(Origin, TimeSpan.Zero);
        var s3 = s2.Failed(Origin, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(60), s1.RetryNotBefore);
        Assert.Equal(TimeSpan.FromSeconds(120), s2.RetryNotBefore);
        Assert.Equal(TimeSpan.FromSeconds(240), s3.RetryNotBefore);
        Assert.Equal(3, s3.ConsecutiveFailures);
    }

    [Fact]
    public void 壁時計側の待ち時間も同じだけ延びる()
    {
        var schedule = new PollingSchedule().Failed(Origin, TimeSpan.Zero).Failed(Origin, TimeSpan.Zero);

        Assert.Equal(Origin.AddSeconds(120), schedule.RetryNotBeforeWallClock);
    }

    [Fact]
    public void 待ち時間には上限がある()
    {
        var schedule = new PollingSchedule();
        for (var i = 0; i < 20; i++)
        {
            schedule = schedule.Failed(Origin, TimeSpan.Zero);
        }

        Assert.Equal(PollInterval.MaxBackoff, schedule.RetryNotBefore);
    }

    [Fact]
    public void ゆらぎを足せる()
    {
        var none = new PollingSchedule().Failed(Origin, TimeSpan.Zero, jitter: 0d);
        var full = new PollingSchedule().Failed(Origin, TimeSpan.Zero, jitter: 1d);

        Assert.Equal(TimeSpan.FromSeconds(60), none.RetryNotBefore);
        // 最大で5割増しにする
        Assert.Equal(TimeSpan.FromSeconds(90), full.RetryNotBefore);
    }

    [Fact]
    public void ゆらぎを足しても上限の五割増しを超えない()
    {
        var schedule = new PollingSchedule();
        for (var i = 0; i < 20; i++)
        {
            schedule = schedule.Failed(Origin, TimeSpan.Zero, jitter: 1d);
        }

        Assert.Equal(PollInterval.MaxBackoff * 1.5, schedule.RetryNotBefore);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(2d)]
    public void 範囲の外のゆらぎは丸める(double jitter)
    {
        var schedule = new PollingSchedule().Failed(Origin, TimeSpan.Zero, jitter);

        Assert.NotNull(schedule.RetryNotBefore);
        Assert.InRange(schedule.RetryNotBefore.Value, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void 成功すると失敗の記録が消える()
    {
        var schedule = new PollingSchedule()
            .Failed(Origin, TimeSpan.Zero)
            .Failed(Origin, TimeSpan.Zero)
            .Succeeded(Origin, TimeSpan.FromMinutes(5));

        Assert.Equal(0, schedule.ConsecutiveFailures);
        Assert.Null(schedule.RetryNotBefore);
        Assert.Null(schedule.RetryNotBeforeWallClock);
    }

    [Fact]
    public void 復帰のきっかけで待ち時間を解く()
    {
        var schedule = new PollingSchedule()
            .Succeeded(Origin, TimeSpan.Zero)
            .Failed(Origin.AddMinutes(11), TimeSpan.FromMinutes(11));

        Assert.False(schedule.IsDue(Interval, Origin.AddMinutes(11.5), TimeSpan.FromMinutes(11.5)));

        var resumed = schedule.Resumed();

        Assert.True(resumed.IsDue(Interval, Origin.AddMinutes(11.5), TimeSpan.FromMinutes(11.5)));
        Assert.Null(resumed.RetryNotBeforeWallClock);
        // 失敗の回数は残す。次に失敗したときの待ち時間を短くしないため
        Assert.Equal(1, resumed.ConsecutiveFailures);
    }

    [Fact]
    public void アラートは予報より長い間隔で取る()
    {
        var schedule = new PollingSchedule().Succeeded(Origin, TimeSpan.Zero);

        Assert.True(schedule.IsDue(PollInterval.Forecast, Origin.AddMinutes(11), TimeSpan.FromMinutes(11)));
        Assert.False(schedule.IsDue(PollInterval.Alert, Origin.AddMinutes(11), TimeSpan.FromMinutes(11)));
        Assert.True(schedule.IsDue(PollInterval.Alert, Origin.AddMinutes(31), TimeSpan.FromMinutes(31)));
    }
}
