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
    public void いまの時間が欠測なら当日の直近の未来へ落とす()
    {
        // 10時が欠測で配列から落ちている。
        // 本体の pickCurrentHour と同じく、過ぎた9時ではなく11時を選ぶ
        var forecast = WithHours("2026-08-15T09:00", "2026-08-15T12:00", "2026-08-15T11:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal("2026-08-15T11:00", hour.Time);
    }

    [Fact]
    public void 未来の行が並びの後ろにあっても直近を選ぶ()
    {
        // 最後に見た未来の行ではなく、いちばん近い未来の行を選ぶ
        var forecast = WithHours("2026-08-15T11:00", "2026-08-15T13:00", "2026-08-15T12:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        Assert.Equal("2026-08-15T11:00", ForecastView.SelectCurrentHour(forecast, now)?.Time);
    }

    [Fact]
    public void 過ぎた時間へは落とさない()
    {
        // すでに過ぎた時間を「いま」として掲げない
        var forecast = WithHours("2026-08-15T08:00", "2026-08-15T09:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        Assert.Null(ForecastView.SelectCurrentHour(forecast, now));
    }

    [Fact]
    public void 日付をまたいだ先の行は選ばない()
    {
        // 23時が欠測でも、翌日の0時を今日の「いま」にしない
        var forecast = WithHours("2026-08-15T22:00", "2026-08-16T00:00");
        var now = JstTime.ToInstant("2026-08-15T23:30")!.Value;

        Assert.Null(ForecastView.SelectCurrentHour(forecast, now));
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
    public void 当日の行が無ければ選べない()
    {
        var forecast = WithHours("2026-08-16T10:00", "2026-08-16T11:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        Assert.Null(ForecastView.SelectCurrentHour(forecast, now));
    }

    [Fact]
    public void まだ始まっていない当日の予報なら直近の行を選ぶ()
    {
        var forecast = WithHours("2026-08-15T13:00", "2026-08-15T12:00");
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        var hour = ForecastView.SelectCurrentHour(forecast, now);

        Assert.NotNull(hour);
        Assert.Equal("2026-08-15T12:00", hour.Time);
    }

    [Fact]
    public void 同じ時間の行が2つあれば先のものを選ぶ()
    {
        var forecast = new ForecastResponse
        {
            Hours =
            [
                new HourForecast { Time = "2026-08-15T10:00", WeatherLabel = "先" },
                new HourForecast { Time = "2026-08-15T10:00", WeatherLabel = "後" },
            ],
        };
        var now = JstTime.ToInstant("2026-08-15T10:30")!.Value;

        Assert.Equal("先", ForecastView.SelectCurrentHour(forecast, now)?.WeatherLabel);
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
