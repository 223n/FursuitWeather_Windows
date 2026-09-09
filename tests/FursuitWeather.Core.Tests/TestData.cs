using System.Text.Json;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Tests;

/// <summary>テストが共有する読み込み処理。</summary>
internal static class TestData
{
    /// <summary>
    /// 本体の <c>GET /api/forecast?demo=1</c> から取った実物のレスポンス。
    /// 手で書いた模造品ではなく、実際のスキーマの変化に気付けるようにする。
    /// </summary>
    public static ForecastResponse DemoForecast { get; } = Load("forecast-demo.json");

    /// <summary>デモデータの生成時刻。テストの「いま」の基準にする。</summary>
    public static DateTimeOffset DemoGeneratedAt => DemoForecast.GeneratedAt;

    private static ForecastResponse Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ForecastResponse>(json, FursuitWeatherClient.JsonOptions)
            ?? throw new InvalidOperationException($"{fileName} を読めませんでした。");
    }
}
