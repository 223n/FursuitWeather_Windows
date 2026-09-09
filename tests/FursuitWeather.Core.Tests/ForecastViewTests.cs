using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>表示する1件の選び方と、鮮度の判定を見る。</summary>
public sealed class ForecastViewTests
{
    private static ForecastResponse WithHours(params string[] times) => new()
    {
        GeneratedAt = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
        Hours = [.. times.Select(t => new HourForecast { Time = t })],
    };

    [Fact]
    public void いまの時間帯の行を選ぶ()
    {
        var forecast = WithHours("2026-08-15T09:00", "2026-08-15T10:00", "2026-08-15T11:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal("2026-08-15T10:00", hour.Time);
    }

    [Fact]
    public void 欠測で歯抜けになっていても添字に頼らない()
    {
        // 10時が欠測で配列から落ちている。添字で数えると11時を10時とみなしてしまう
        var forecast = WithHours("2026-08-15T09:00", "2026-08-15T11:00", "2026-08-15T12:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        // 直前の時間へ落ちる。11時を先取りしない
        Assert.Equal("2026-08-15T09:00", hour.Time);
    }

    [Fact]
    public void 並びが時刻順でなくても選べる()
    {
        var forecast = WithHours("2026-08-15T11:00", "2026-08-15T09:00", "2026-08-15T10:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal("2026-08-15T10:00", hour.Time);
    }

    [Fact]
    public void 読めない時刻の行は飛ばす()
    {
        var forecast = WithHours("こわれた値", "2026-08-15T10:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal("2026-08-15T10:00", hour.Time);
    }

    [Fact]
    public void まだ始まっていない予報では選べない()
    {
        var forecast = WithHours("2026-08-15T12:00", "2026-08-15T13:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        Assert.Null(ForecastView.SelectCurrentHour(forecast, now));
    }

    [Fact]
    public void 実物のデモデータからも選べる()
    {
        var forecast = TestData.DemoForecast;
        var now = JstTime.ToInstant(forecast.Hours[10].Time)!.Value.AddMinutes(30);

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal(forecast.Hours[10].Time, hour.Time);
    }

    [Fact]
    public void 日付で日別のまとめを選べる()
    {
        var forecast = TestData.DemoForecast;
        var first = forecast.Days[0];

        var day = ForecastView.SelectDay(forecast, DateOnly.Parse(first.Date, System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotNull(day);
        Assert.Equal(first.Date, day.Date);
    }

    [Fact]
    public void 範囲の外の日付では選べない()
    {
        Assert.Null(ForecastView.SelectDay(TestData.DemoForecast, new DateOnly(2000, 1, 1)));
    }

    [Fact]
    public void 一時間を超えると古いとみなす()
    {
        var forecast = WithHours("2026-08-15T09:00");
        var generated = forecast.GeneratedAt;

        Assert.False(ForecastView.IsStale(forecast, generated.AddMinutes(59)));
        Assert.True(ForecastView.IsStale(forecast, generated.AddMinutes(61)));
    }

    [Fact]
    public void 経過は負にならない()
    {
        var forecast = WithHours("2026-08-15T09:00");

        // 端末の時計が戻された場合を想定する
        var age = ForecastView.AgeOf(forecast, forecast.GeneratedAt.AddHours(-5));

        Assert.Equal(TimeSpan.Zero, age);
    }
}
