using FursuitWeather.Core.Display;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>上と下の帯に出すものの組み方を見る。</summary>
public sealed class DisplayBandTests
{
    private static DateTimeOffset At(string local) => JstTime.ToInstant(local)!.Value;

    private static HourForecast Hour(string time, int minutes, string level = "warning", int grade = 2) => new()
    {
        Time = time,
        Outdoor = new ActivityAssessment { Level = level, Grade = grade, ActivityMinutes = minutes },
    };

    private static ForecastResponse Forecast(params HourForecast[] hours) => new() { Hours = hours };

    // ---- 先読み

    [Fact]
    public void この先3時間にいまより短い時間があれば選ぶ()
    {
        var current = Hour("2026-08-15T10:00", 30);
        var forecast = Forecast(current, Hour("2026-08-15T11:00", 20), Hour("2026-08-15T12:00", 5, "danger", 4));

        var target = DisplayBand.Lookahead(forecast, current, At("2026-08-15T10:30"));

        Assert.Equal("2026-08-15T12:00", target?.Time);
    }

    [Fact]
    public void いまより短い時間が無ければ出さない()
    {
        // いまの行を「最も厳しい時間」として帯に重ねない
        var current = Hour("2026-08-15T10:00", 20);
        var forecast = Forecast(current, Hour("2026-08-15T11:00", 20), Hour("2026-08-15T12:00", 30));

        Assert.Null(DisplayBand.Lookahead(forecast, current, At("2026-08-15T10:30")));
    }

    [Fact]
    public void 先読みは3時間より先を見ない()
    {
        var current = Hour("2026-08-15T10:00", 30);
        var forecast = Forecast(current, Hour("2026-08-15T14:00", 0, "danger", 4));

        Assert.Null(DisplayBand.Lookahead(forecast, current, At("2026-08-15T10:30")));
    }

    [Fact]
    public void 先読みはgradeではなく連続活動時間で比べる()
    {
        // optimal（grade 0）から coldCaution（grade 1）へは grade が上がるが、連続活動時間は変わらない
        var current = Hour("2026-08-15T05:00", 45, "optimal", 0);
        var forecast = Forecast(current, Hour("2026-08-15T06:00", 45, "coldCaution", 1));

        Assert.Null(DisplayBand.Lookahead(forecast, current, At("2026-08-15T05:10")));
    }

    [Fact]
    public void 同じ判定でも連続活動時間が短くなれば出す()
    {
        // grade は同じ2のままでも、連続して着ていられる時間が縮む
        var current = Hour("2026-08-15T10:00", 30, "warning", 2);
        var forecast = Forecast(current, Hour("2026-08-15T11:00", 20, "warning", 2));

        Assert.Equal("2026-08-15T11:00", DisplayBand.Lookahead(forecast, current, At("2026-08-15T10:30"))?.Time);
    }

    [Fact]
    public void いまの行が無ければ先読みの行をそのまま出す()
    {
        var forecast = Forecast(Hour("2026-08-15T11:00", 20));

        Assert.Equal("2026-08-15T11:00", DisplayBand.Lookahead(forecast, null, At("2026-08-15T10:30"))?.Time);
    }

    // ---- 公式の発表

    [Fact]
    public void 公式の発表は環境省発表と県名を出す()
    {
        var alert = new HeatAlert { PrefectureName = "東京都", TargetDate = "2026-08-15" };

        Assert.Equal("環境省発表: 東京都に熱中症警戒アラート", DisplayBand.AlertText(alert, At("2026-08-15T10:00")));
    }

    [Fact]
    public void 特別警戒アラートを書き分ける()
    {
        var alert = new HeatAlert { PrefectureName = "沖縄県", Special = true, TargetDate = "2026-08-15" };

        Assert.Equal("環境省発表: 沖縄県に熱中症特別警戒アラート", DisplayBand.AlertText(alert, At("2026-08-15T10:00")));
    }

    [Fact]
    public void 対象日を過ぎた発表は出さない()
    {
        var alert = new HeatAlert { PrefectureName = "東京都", TargetDate = "2026-08-14" };

        Assert.Null(DisplayBand.AlertText(alert, At("2026-08-15T00:10")));
    }

    [Fact]
    public void 対象日を読めない発表は出す()
    {
        // 黙って隠すより、出すほうが安全側である
        var alert = new HeatAlert { PrefectureName = "東京都", TargetDate = "こわれた値" };

        Assert.NotNull(DisplayBand.AlertText(alert, At("2026-08-15T10:00")));
    }

    [Fact]
    public void 県名が無ければ発表地域とする()
    {
        var alert = new HeatAlert { TargetDate = "2026-08-15" };

        Assert.Equal("環境省発表: 発表地域に熱中症警戒アラート", DisplayBand.AlertText(alert, At("2026-08-15T10:00")));
        Assert.Null(DisplayBand.AlertText(null, At("2026-08-15T10:00")));
    }

    // ---- 文

    [Theory]
    [InlineData(0, "着用中止")]
    [InlineData(-5, "着用中止")]
    [InlineData(30, "連続30分まで")]
    public void 連続活動時間の文(int minutes, string expected)
    {
        Assert.Equal(expected, DisplayBand.ActivityText(minutes));
    }

    [Fact]
    public void 時刻の文は翌日なら翌を添える()
    {
        var now = At("2026-08-15T22:30");

        Assert.Equal("23時", DisplayBand.HourText(new HourForecast { Time = "2026-08-15T23:00" }, now));
        Assert.Equal("翌1時", DisplayBand.HourText(new HourForecast { Time = "2026-08-16T01:00" }, now));
        Assert.Equal(string.Empty, DisplayBand.HourText(new HourForecast { Time = "こわれた値" }, now));
    }

    [Fact]
    public void 時点の文は日本時間で出す()
    {
        // 実物のレスポンスはミリ秒の付いたUTCで来る
        var generatedAt = DateTimeOffset.Parse("2026-09-11T13:39:53.989Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("22:39時点", DisplayBand.AsOfText(generatedAt));
    }

    // ---- 鮮度

    [Fact]
    public void 一時間を超えたら古い()
    {
        var now = At("2026-08-15T12:00");

        Assert.Equal(Freshness.Fresh, DisplayBand.FreshnessOf(now.AddMinutes(-60), now));
        Assert.Equal(Freshness.Stale, DisplayBand.FreshnessOf(now.AddMinutes(-61), now));
    }

    [Fact]
    public void 未来へ5分を超えてずれていたら時計の遅れを疑う()
    {
        var now = At("2026-08-15T12:00");

        Assert.Equal(Freshness.Fresh, DisplayBand.FreshnessOf(now.AddMinutes(5), now));
        Assert.Equal(Freshness.ClockBehind, DisplayBand.FreshnessOf(now.AddMinutes(6), now));
    }

    // ---- 注意

    [Fact]
    public void 当てはまる注意をすべて積む()
    {
        var now = At("2026-08-15T12:00");
        var inputs = new DisplayNoticeInputs
        {
            ForecastGeneratedAt = now.AddMinutes(-90),
            ForecastFailed = true,
            DefaultLocation = true,
            DefaultPlaceName = "東京駅の周辺",
            NationalGeneratedAt = now.AddMinutes(-75),
            MonitorMoved = true,
        };

        Assert.Equal(
        [
            "予報が90分前のままです",
            "予報を取り直せていません。回線を確かめてください",
            "地点が設定されていません。東京駅の周辺を表示しています",
            "全国の天気が75分前のままです",
            "掲示先のモニターが外れたため、ほかのモニターに出しています",
        ],
            DisplayBand.Notices(inputs, now));
    }

    [Fact]
    public void 何も無ければ注意を出さない()
    {
        var now = At("2026-08-15T12:00");

        Assert.Empty(DisplayBand.Notices(new DisplayNoticeInputs { ForecastGeneratedAt = now }, now));
    }

    [Fact]
    public void 時計の遅れを知らせる()
    {
        var now = At("2026-08-15T12:00");

        var notices = DisplayBand.Notices(new DisplayNoticeInputs { ForecastGeneratedAt = now.AddMinutes(10) }, now);

        Assert.Equal(["端末の時計が遅れている可能性があります。時計を合わせてください"], notices);
    }

    [Fact]
    public void 選んだモニターが無いことを知らせる()
    {
        var now = At("2026-08-15T12:00");

        var notices = DisplayBand.Notices(new DisplayNoticeInputs { MonitorMissing = true }, now);

        Assert.Equal(["選んだモニターが見つからないため、主モニターに出しています"], notices);
    }

    [Fact]
    public void 運営者向けの注意は出してよいあいだだけ出す()
    {
        // 来場者の見る画面に残し続けない
        var now = At("2026-08-15T12:00");
        var inputs = new DisplayNoticeInputs { HotkeyFailed = true, UpdateResult = "0.4.0 に更新しました" };

        Assert.Empty(DisplayBand.Notices(inputs, now));
        Assert.Equal(
            ["掲示を終えるホットキーを登録できませんでした。Escキーかトレイから終えてください", "0.4.0 に更新しました"],
            DisplayBand.Notices(inputs with { OperatorWindow = true }, now));
    }
}
