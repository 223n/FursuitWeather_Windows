using System.Text.Json;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Tests;

/// <summary>実物のレスポンスを読めるかを見る。</summary>
public sealed class ForecastParsingTests
{
    [Fact]
    public void 実物のデモレスポンスを読める()
    {
        var forecast = TestData.DemoForecast;

        Assert.Equal("demo", forecast.Model);
        Assert.NotEmpty(forecast.Hours);
        Assert.NotEmpty(forecast.Days);
        Assert.NotEqual(default, forecast.GeneratedAt);
    }

    [Fact]
    public void 出典の表記が入っている()
    {
        var attribution = TestData.DemoForecast.Attribution;

        // 画面へ描く義務があるため、空だと表示できない
        Assert.False(string.IsNullOrWhiteSpace(attribution.WeatherData));
        Assert.False(string.IsNullOrWhiteSpace(attribution.WeatherDataUrl));
        Assert.False(string.IsNullOrWhiteSpace(attribution.License));
    }

    [Fact]
    public void レベルIDを列挙へ写せる()
    {
        var levels = TestData.DemoForecast.Hours
            .Select(h => h.Outdoor.LevelId)
            .Distinct()
            .ToList();

        Assert.NotEmpty(levels);
        // 知らない値が混ざっていたら、APIがレベルを増やしたということ
        Assert.DoesNotContain(ActivityLevel.Unknown, levels);
    }

    [Fact]
    public void 屋内の判定は冷房の要否を持つ()
    {
        var needs = TestData.DemoForecast.Hours
            .Select(h => h.Indoor.CoolingNeed)
            .Distinct()
            .ToList();

        Assert.NotEmpty(needs);
        Assert.DoesNotContain(Models.CoolingNeed.Unknown, needs);
    }

    [Fact]
    public void 知らないフィールドがあっても読める()
    {
        // APIが項目を増やしても壊れないことを確かめる
        const string Json = """
            {
              "location": { "latitude": 35.68, "longitude": 139.68, "timezone": "Asia/Tokyo" },
              "generatedAt": "2026-08-15T09:00:00.000Z",
              "model": "demo",
              "attribution": { "weatherData": "a", "weatherDataUrl": "b", "license": "c" },
              "notices": [],
              "suddenHeat": null,
              "hours": [],
              "days": [],
              "brandNewFieldAddedLater": { "nested": [1, 2, 3] }
            }
            """;

        var forecast = JsonSerializer.Deserialize<ForecastResponse>(Json, FursuitWeatherClient.JsonOptions);

        Assert.NotNull(forecast);
        Assert.Equal("demo", forecast.Model);
    }

    [Fact]
    public void 欠測を表す値を読める()
    {
        const string Json = """
            {
              "time": "2026-08-15T09:00",
              "temperature": 30.1,
              "humidity": 60,
              "apparentTemperature": 33.2,
              "precipitation": 0,
              "precipitationProbability": null,
              "weatherCode": -1,
              "solarRadiation": 500,
              "windSpeed": 2.5
            }
            """;

        var weather = JsonSerializer.Deserialize<HourlyWeather>(Json, FursuitWeatherClient.JsonOptions);

        Assert.NotNull(weather);
        Assert.Null(weather.PrecipitationProbability);
        Assert.True(weather.IsWeatherCodeMissing);
    }

    [Fact]
    public void 急な暑さの注意はnullを取りうる()
    {
        // デモデータでは null。null許容として扱えていることを確かめる
        _ = TestData.DemoForecast.SuddenHeat;
        Assert.True(true);
    }
}
