using FursuitWeather.Core.Display;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>掲示へ出す行と日の選び方を見る。</summary>
public sealed class DisplayForecastTests
{
    private static DateTimeOffset At(string local) => JstTime.ToInstant(local)!.Value;

    private static ForecastResponse WithHours(params string[] times) => new()
    {
        Hours = [.. times.Select(t => new HourForecast { Time = t })],
    };

    private static ForecastResponse WithDays(params string[] dates) => new()
    {
        Days = [.. dates.Select(d => new DayForecast { Date = d })],
    };

    [Fact]
    public void いまの時間の頭から6時間を選ぶ()
    {
        var forecast = WithHours(
            "2026-08-15T09:00", "2026-08-15T10:00", "2026-08-15T11:00", "2026-08-15T12:00",
            "2026-08-15T13:00", "2026-08-15T14:00", "2026-08-15T15:00", "2026-08-15T16:00");

        var hours = DisplayForecast.UpcomingHours(forecast, At("2026-08-15T10:30"));

        Assert.Equal(
            ["2026-08-15T10:00", "2026-08-15T11:00", "2026-08-15T12:00", "2026-08-15T13:00", "2026-08-15T14:00", "2026-08-15T15:00"],
            hours.Select(h => h.Time));
    }

    [Fact]
    public void 欠測があっても6時間より先の行を出さない()
    {
        // WebとMac版は配列の6件を取るため、ここで17時まで出してしまう
        var forecast = WithHours(
            "2026-08-15T10:00", "2026-08-15T12:00", "2026-08-15T13:00",
            "2026-08-15T15:00", "2026-08-15T16:00", "2026-08-15T17:00");

        var hours = DisplayForecast.UpcomingHours(forecast, At("2026-08-15T10:30"));

        Assert.Equal(["2026-08-15T10:00", "2026-08-15T12:00", "2026-08-15T13:00", "2026-08-15T15:00"], hours.Select(h => h.Time));
    }

    [Fact]
    public void 日付をまたいで翌日の行も選ぶ()
    {
        var forecast = WithHours("2026-08-15T23:00", "2026-08-16T00:00", "2026-08-16T01:00");

        var hours = DisplayForecast.UpcomingHours(forecast, At("2026-08-15T22:30"));

        Assert.Equal(["2026-08-15T23:00", "2026-08-16T00:00", "2026-08-16T01:00"], hours.Select(h => h.Time));
        Assert.True(DisplayForecast.IsAfterToday(hours[1], At("2026-08-15T22:30")));
        Assert.False(DisplayForecast.IsAfterToday(hours[0], At("2026-08-15T22:30")));
    }

    [Fact]
    public void 時刻の順に並べ読めない行は飛ばす()
    {
        var forecast = WithHours("2026-08-15T12:00", "こわれた値", "2026-08-15T11:00");

        var hours = DisplayForecast.UpcomingHours(forecast, At("2026-08-15T10:30"));

        Assert.Equal(["2026-08-15T11:00", "2026-08-15T12:00"], hours.Select(h => h.Time));
    }

    [Fact]
    public void 日ごとの予報は今日から日付で3日を選ぶ()
    {
        // 配列の先頭が昨日でも、添字ではなく日付で選ぶ
        var forecast = WithDays("2026-08-14", "2026-08-15", "2026-08-16", "2026-08-17", "2026-08-18");

        var days = DisplayForecast.NextDays(forecast, At("2026-08-15T10:30"));

        Assert.Equal(["2026-08-15", "2026-08-16", "2026-08-17"], days.Select(d => d.Date));
    }

    [Fact]
    public void 欠けた日は飛ばす()
    {
        var forecast = WithDays("2026-08-15", "2026-08-17");

        var days = DisplayForecast.NextDays(forecast, At("2026-08-15T10:30"));

        Assert.Equal(["2026-08-15", "2026-08-17"], days.Select(d => d.Date));
    }

    [Fact]
    public void 今日の予報を選ぶ()
    {
        var forecast = WithDays("2026-08-14", "2026-08-15");

        Assert.Equal("2026-08-15", DisplayForecast.TodayOf(forecast, At("2026-08-15T00:10"))?.Date);
        Assert.Null(DisplayForecast.TodayOf(forecast, At("2026-08-16T00:10")));
    }

    [Fact]
    public void 実物のデモデータでも選べる()
    {
        var forecast = TestData.DemoForecast;
        var now = JstTime.ToInstant(forecast.Hours[10].Time)!.Value.AddMinutes(30);

        var hours = DisplayForecast.UpcomingHours(forecast, now);
        var days = DisplayForecast.NextDays(forecast, now);

        Assert.InRange(hours.Count, 1, 6);
        Assert.Equal(forecast.Hours[10].Time, hours[0].Time);
        Assert.NotEmpty(days);
    }
}
