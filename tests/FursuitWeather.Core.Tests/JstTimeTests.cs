using System.Globalization;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>
/// タイムゾーンなしの日本時間の扱いを見る。
/// </summary>
/// <remarks>
/// ここが崩れると、日本時間の開発機では正しく動き、UTCのCIでだけ9時間ずれる。
/// テストが通ってしまう種類の不具合になるため、機械のタイムゾーンに依存しない形で確かめる。
/// </remarks>
public sealed class JstTimeTests
{
    [Fact]
    public void 日本時間として解釈される()
    {
        var instant = JstTime.ToInstant("2026-08-15T09:00");

        Assert.NotNull(instant);
        // 日本時間の9時は世界標準時の0時
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), instant.Value.ToUniversalTime());
    }

    [Fact]
    public void 実行するマシンのタイムゾーンに依存しない()
    {
        // DateTimeOffset.Parse はオフセットが無いときローカルの値を補うため、
        // 機械のタイムゾーン次第で答えが変わる。こちらはそうならない。
        var ours = JstTime.ToInstant("2026-01-01T00:00");
        Assert.NotNull(ours);

        var expected = new DateTimeOffset(2025, 12, 31, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ours.Value.ToUniversalTime());
    }

    [Fact]
    public void 秒まである形も読める()
    {
        var instant = JstTime.ToInstant("2026-08-15T09:30:45");

        Assert.NotNull(instant);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 30, 45, TimeSpan.Zero), instant.Value.ToUniversalTime());
    }

    [Fact]
    public void 読めない値はnullになる()
    {
        Assert.Null(JstTime.ToInstant(null));
        Assert.Null(JstTime.ToInstant(string.Empty));
        Assert.Null(JstTime.ToInstant("   "));
        Assert.Null(JstTime.ToInstant("2026-08-15"));
        Assert.Null(JstTime.ToInstant("きょう"));
    }

    [Fact]
    public void 読んだ値はタイムゾーンを持たない()
    {
        var local = JstTime.ParseLocal("2026-08-15T09:00");

        Assert.NotNull(local);
        Assert.Equal(DateTimeKind.Unspecified, local.Value.Kind);
    }

    [Fact]
    public void 文化圏に依存しない()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 和暦や別の暦を既定に持つ文化圏でも同じ結果になること
            CultureInfo.CurrentCulture = new CultureInfo("ja-JP-u-ca-japanese");
            var instant = JstTime.ToInstant("2026-08-15T09:00");

            Assert.NotNull(instant);
            Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), instant.Value.ToUniversalTime());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 絶対時刻から日本時間へ戻せる()
    {
        var local = JstTime.ToLocal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTime(2026, 8, 15, 9, 0, 0), local);
    }
}
