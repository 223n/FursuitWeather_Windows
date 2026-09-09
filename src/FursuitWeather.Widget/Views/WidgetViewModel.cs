using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Widget.Views;

/// <summary>
/// 小窓へ出す値。
/// </summary>
/// <remarks>
/// Step 1 の確認では、手で書いた値を描く。
/// APIへ繋ぐのは Step 2 で行う。
/// </remarks>
internal sealed class WidgetViewModel
{
    private WidgetViewModel(HourForecast hour, string place, Attribution attribution)
    {
        var outdoor = hour.Outdoor;
        var cold = outdoor.LevelId.IsCold();
        var grade = Math.Clamp(outdoor.Grade, 0, 4);

        HeaderText = string.Create(
            CultureInfo.InvariantCulture,
            $"{place}　{FormatHour(hour.Time)}");

        Symbol = cold ? "❄" : GradeSymbols[grade];
        LevelLabel = outdoor.Label;

        ActivityText = outdoor.ActivityMinutes <= 0
            ? "着用中止"
            : string.Create(CultureInfo.InvariantCulture, $"連続活動 {outdoor.ActivityMinutes}分");

        DetailText = string.Create(
            CultureInfo.InvariantCulture,
            $"気温 {hour.Weather.Temperature:0.#}℃　湿度 {hour.Weather.Humidity:0}%　補正後WBGT {outdoor.SuitWbgt:0.#}℃");

        AttributionText = attribution.WeatherData;

        var suffix = cold ? "Cold" : grade.ToString(CultureInfo.InvariantCulture);
        SurfaceBrush = Brush($"Level{suffix}Surface");
        TextBrush = Brush($"Level{suffix}Text");
        AccentBrush = Brush($"Level{suffix}Accent");
    }

    /// <summary>gradeごとの記号。本体の <c>GRADE_SYMBOLS</c> と同じ。</summary>
    private static readonly string[] GradeSymbols = ["◎", "○", "△", "✕", "✕"];

    /// <summary>地点と時刻。</summary>
    public string HeaderText { get; }

    /// <summary>判定の記号。色だけに頼らないために添える。</summary>
    public string Symbol { get; }

    /// <summary>判定の日本語ラベル。</summary>
    public string LevelLabel { get; }

    /// <summary>連続活動時間の文言。</summary>
    public string ActivityText { get; }

    /// <summary>気温などの詳細。</summary>
    public string DetailText { get; }

    /// <summary>出典の表記。</summary>
    public string AttributionText { get; }

    /// <summary>判定の地の色。</summary>
    public Brush SurfaceBrush { get; }

    /// <summary>判定の文字の色。</summary>
    public Brush TextBrush { get; }

    /// <summary>枠線の色。</summary>
    public Brush AccentBrush { get; }

    /// <summary>
    /// Step 1 の確認に使う、手で書いた値を作る。
    /// </summary>
    /// <remarks>
    /// 読みにくさが最も出るのは、濃い文字色と薄い地の組み合わせである。
    /// もっとも危険な段階（危険・grade 4）を既定にして確かめる。
    /// </remarks>
    public static WidgetViewModel CreateSample() => new(
        new HourForecast
        {
            Time = "2026-08-15T15:00",
            Weather = new HourlyWeather { Temperature = 34.2, Humidity = 62 },
            Outdoor = new ActivityAssessment
            {
                Level = "danger",
                Label = "危険",
                Grade = 4,
                ActivityMinutes = 0,
                SuitWbgt = 38.4,
            },
        },
        "東京都千代田区",
        new Attribution("Weather data by Open-Meteo.com（気象庁MSM/GSMモデル）", "https://open-meteo.com/", "CC BY 4.0"));

    /// <summary>確認のために別の段階へ切り替えた値を作る。</summary>
    public static WidgetViewModel CreateSample(string level, string label, int grade, int minutes) => new(
        new HourForecast
        {
            Time = "2026-08-15T15:00",
            Weather = new HourlyWeather { Temperature = 28.1, Humidity = 55 },
            Outdoor = new ActivityAssessment
            {
                Level = level,
                Label = label,
                Grade = grade,
                ActivityMinutes = minutes,
                SuitWbgt = 30.2,
            },
        },
        "東京都千代田区",
        new Attribution("Weather data by Open-Meteo.com（気象庁MSM/GSMモデル）", "https://open-meteo.com/", "CC BY 4.0"));

    private static string FormatHour(string time)
    {
        var parsed = Core.Time.JstTime.ParseLocal(time);
        return parsed is null
            ? time
            : parsed.Value.ToString("H時ごろ", CultureInfo.InvariantCulture);
    }

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
