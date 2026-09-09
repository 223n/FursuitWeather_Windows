using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>朝のブリーフィングの判定を見る。</summary>
public sealed class BriefingSchedulerTests
{
    private static DateTimeOffset At(string localTime) => JstTime.ToInstant(localTime)!.Value;

    private static DayForecast Day(
        string date,
        string level = "danger",
        string label = "危険",
        int grade = 4,
        bool coolingRequired = true,
        params string[] recommendedHours) => new()
        {
            Date = date,
            TemperatureMax = 35d,
            TemperatureMin = 27d,
            OutdoorWorst = new LevelSummary { Level = level, Label = label, Grade = grade },
            OutdoorBest = new LevelSummary { Level = "severe", Label = "厳重警戒", Grade = 3 },
            RecommendedHours = recommendedHours,
            CoolingRequired = coolingRequired,
        };

    private static ForecastResponse Forecast(params DayForecast[] days) => new()
    {
        GeneratedAt = At("2026-08-15T08:00"),
        Days = days,
    };

    [Fact]
    public void 出す時刻になれば作る()
    {
        var content = BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T08:00"));

        Assert.NotNull(content);
        Assert.Equal(new DateOnly(2026, 8, 15), content.Date);
        Assert.Equal("danger", content.Worst.Level);
        Assert.True(content.CoolingRequired);
    }

    [Fact]
    public void 出す時刻より前は作らない()
    {
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T07:59")));
    }

    [Fact]
    public void 遅すぎるときは作らない()
    {
        // 昼を回ってから「今日の予定」を知らせても遅い
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T12:01")));
    }

    [Fact]
    public void 夜に起動しても持ち越さない()
    {
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T23:00")));
    }

    [Fact]
    public void 今日すでに出していれば作らない()
    {
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, new DateOnly(2026, 8, 15), At("2026-08-15T09:00")));
    }

    [Fact]
    public void 前日に出していれば今日は作る()
    {
        var content = BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, new DateOnly(2026, 8, 14), At("2026-08-15T08:00"));

        Assert.NotNull(content);
    }

    [Fact]
    public void 切っていれば作らない()
    {
        var options = BriefingOptions.Default with { Enabled = false };

        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T08:00"), options));
    }

    [Fact]
    public void 出す時刻を変えられる()
    {
        var options = BriefingOptions.Default with { DeliverAt = new TimeOnly(6, 30) };

        Assert.NotNull(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T06:30"), options));
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T06:29"), options));
    }

    [Fact]
    public void 今日の予報が無ければ作らない()
    {
        Assert.Null(BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-16")), false, null, At("2026-08-15T08:00")));
    }

    [Fact]
    public void 判定が変わらなくても作る()
    {
        // これがこの層の目的。変化の検知だけでは飽和期間に何日も無音になる
        var forecast = Forecast(Day("2026-08-15"), Day("2026-08-16"));

        var day1 = BriefingScheduler.TryBuild(forecast, false, null, At("2026-08-15T08:00"));
        var day2 = BriefingScheduler.TryBuild(forecast, false, new DateOnly(2026, 8, 15), At("2026-08-16T08:00"));

        Assert.NotNull(day1);
        Assert.NotNull(day2);
        Assert.Equal(day1.Worst.Level, day2.Worst.Level);
    }

    [Fact]
    public void 過ぎた時間帯は勧めない()
    {
        var forecast = Forecast(Day("2026-08-15", recommendedHours: ["07:00", "09:00", "17:00"]));

        var content = BriefingScheduler.TryBuild(forecast, false, null, At("2026-08-15T09:30"));

        Assert.NotNull(content);
        // 9時台はまだ使えるので残す
        Assert.Equal(["09:00", "17:00"], content.RecommendedHours);
    }

    [Fact]
    public void 読めない時間帯は落とさない()
    {
        var forecast = Forecast(Day("2026-08-15", recommendedHours: ["こわれた値", "17:00"]));

        var content = BriefingScheduler.TryBuild(forecast, false, null, At("2026-08-15T09:00"));

        Assert.NotNull(content);
        Assert.Contains("こわれた値", content.RecommendedHours);
    }

    [Fact]
    public void 適した時間帯が無いことを伝えられる()
    {
        var content = BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), false, null, At("2026-08-15T08:00"));

        Assert.NotNull(content);
        Assert.True(content.HasNoRecommendedHours);
    }

    [Fact]
    public void 公式のアラートの状況を含める()
    {
        var content = BriefingScheduler.TryBuild(
            Forecast(Day("2026-08-15")), alertActive: true, null, At("2026-08-15T08:00"));

        Assert.NotNull(content);
        Assert.True(content.AlertActive);
    }

    [Fact]
    public void 急な暑さの注意を含める()
    {
        var forecast = Forecast(Day("2026-08-15")) with
        {
            SuddenHeat = new SuddenHeatWarning { Date = "2026-08-15", RecentAverageMax = 28d, TargetMax = 35d },
        };

        var content = BriefingScheduler.TryBuild(forecast, false, null, At("2026-08-15T08:00"));

        Assert.NotNull(content);
        Assert.NotNull(content.SuddenHeat);
        Assert.Equal(35d, content.SuddenHeat.TargetMax);
    }

    [Fact]
    public void 低温側かどうかを伝える()
    {
        var cold = Forecast(Day("2026-01-15", level: "coldDanger", label: "低温危険"));

        var content = BriefingScheduler.TryBuild(cold, false, null, At("2026-01-15T08:00"));

        Assert.NotNull(content);
        Assert.True(content.IsCold);
    }

    [Fact]
    public void 実物のデモデータからも作れる()
    {
        var forecast = TestData.DemoForecast;
        var first = forecast.Days[0];
        var localMorning = JstTime.ToInstant($"{first.Date}T08:00")!.Value;

        var content = BriefingScheduler.TryBuild(forecast, false, null, localMorning);

        Assert.NotNull(content);
        Assert.Equal(first.OutdoorWorst.Level, content.Worst.Level);
    }
}
