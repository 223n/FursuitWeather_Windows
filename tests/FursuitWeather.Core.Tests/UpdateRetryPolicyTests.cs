using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>失敗したあとの再試行の抑制を見る。</summary>
public sealed class UpdateRetryPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 失敗していなければ待たない()
    {
        Assert.Null(UpdateRetryPolicy.NextDownloadAt(new UpdateAttempts()));
        Assert.Null(UpdateRetryPolicy.NextInstallAt(new UpdateAttempts()));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 360)]
    public void 取得の待ち時間が段階的に伸びる(int failures, int expectedMinutes)
    {
        var attempts = new UpdateAttempts { DownloadFailures = failures, LastDownloadFailureAt = Now };

        Assert.Equal(Now.AddMinutes(expectedMinutes), UpdateRetryPolicy.NextDownloadAt(attempts));
    }

    [Fact]
    public void 取得の待ち時間は頭打ちにする()
    {
        // 限りなく延ばす指数のバックオフは、更新を永久に届かなくする
        var attempts = new UpdateAttempts { DownloadFailures = 99, LastDownloadFailureAt = Now };

        Assert.Equal(Now.AddHours(6), UpdateRetryPolicy.NextDownloadAt(attempts));
    }

    [Fact]
    public void 取得の回数に上限がある()
    {
        Assert.False(UpdateRetryPolicy.IsDownloadExhausted(
            new UpdateAttempts { DownloadFailures = 4, LastDownloadFailureAt = Now }, Now));
        Assert.True(UpdateRetryPolicy.IsDownloadExhausted(
            new UpdateAttempts { DownloadFailures = 5, LastDownloadFailureAt = Now }, Now));
    }

    [Fact]
    public void 取得の上限は24時間で解ける()
    {
        // 累計で数えると、回線が悪かった数日のせいで自動の取得が二度と動かない
        var attempts = new UpdateAttempts { DownloadFailures = 5, LastDownloadFailureAt = Now };

        Assert.True(UpdateRetryPolicy.IsDownloadExhausted(attempts, Now.AddHours(24).AddTicks(-1)));
        Assert.False(UpdateRetryPolicy.IsDownloadExhausted(attempts, Now.AddHours(24)));
    }

    [Fact]
    public void 失敗の時刻が無い記録では上限を効かせない()
    {
        // 窓を決められないものを止める側へ倒すと、二度と解けない
        Assert.False(UpdateRetryPolicy.IsDownloadExhausted(new UpdateAttempts { DownloadFailures = 99 }, Now));
    }

    [Fact]
    public void 窓が明けたら取得の失敗を数え直す()
    {
        var attempts = new UpdateAttempts
        {
            Version = "0.3.1",
            ExpectedSha256 = "a",
            DownloadFailures = 5,
            LastDownloadFailureAt = Now,
            InstallFailures = 2,
        };

        var within = UpdateRetryPolicy.ExpireDownloadFailures(attempts, Now.AddHours(24).AddTicks(-1));
        var after = UpdateRetryPolicy.ExpireDownloadFailures(attempts, Now.AddHours(24));

        Assert.Equal(attempts, within);
        Assert.Equal(0, after.DownloadFailures);
        Assert.Null(after.LastDownloadFailureAt);

        // インストールの記録と狙いには触れない
        Assert.Equal(2, after.InstallFailures);
        Assert.Equal("0.3.1", after.Version);
    }

    [Fact]
    public void インストールは6時間あける()
    {
        var attempts = new UpdateAttempts { InstallFailures = 1, LastInstallFailureAt = Now };

        Assert.Equal(Now.AddHours(6), UpdateRetryPolicy.NextInstallAt(attempts));
    }

    [Fact]
    public void インストールの回数に上限がある()
    {
        Assert.False(UpdateRetryPolicy.IsInstallExhausted(new UpdateAttempts { InstallFailures = 2 }));
        Assert.True(UpdateRetryPolicy.IsInstallExhausted(new UpdateAttempts { InstallFailures = 3 }));
    }

    [Fact]
    public void 異なる3つの版で失敗したら自動を止める()
    {
        Assert.False(UpdateRetryPolicy.ShouldDisableAuto(["1.0.0", "1.1.0"]));
        Assert.True(UpdateRetryPolicy.ShouldDisableAuto(["1.0.0", "1.1.0", "1.2.0"]));
    }

    [Fact]
    public void 同じ版で何度失敗しても自動は止めない()
    {
        // 数えるのは異なる版の数である。次の版で直るかもしれない
        Assert.False(UpdateRetryPolicy.ShouldDisableAuto(["1.0.0", "1.0.0", "1.0.0", "1.0.0"]));
    }

    [Fact]
    public void 空の版は数えない()
    {
        Assert.False(UpdateRetryPolicy.ShouldDisableAuto(["1.0.0", "", "", ""]));
    }

    [Fact]
    public void 狙いが同じなら記録を引き継ぐ()
    {
        var attempts = new UpdateAttempts
        {
            Version = "1.0.0",
            ExpectedSha256 = "abc",
            DownloadFailures = 2,
        };

        var rebased = UpdateRetryPolicy.Rebase(attempts, "1.0.0", "abc");

        Assert.Equal(2, rebased.DownloadFailures);
    }

    [Fact]
    public void 版が変われば記録を捨てる()
    {
        var attempts = new UpdateAttempts { Version = "1.0.0", ExpectedSha256 = "abc", DownloadFailures = 2 };

        var rebased = UpdateRetryPolicy.Rebase(attempts, "1.1.0", "abc");

        Assert.Equal(0, rebased.DownloadFailures);
        Assert.Equal("1.1.0", rebased.Version);
    }

    [Fact]
    public void 同じ版でも配布物が作り直されたら記録を捨てる()
    {
        // 直っているのに古い抑制で試さない、という状態を作らない
        var attempts = new UpdateAttempts { Version = "1.0.0", ExpectedSha256 = "abc", InstallFailures = 3 };

        var rebased = UpdateRetryPolicy.Rebase(attempts, "1.0.0", "def");

        Assert.Equal(0, rebased.InstallFailures);
        Assert.Equal("def", rebased.ExpectedSha256);
    }
}
