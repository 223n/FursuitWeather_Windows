using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Widget.Views;

/// <summary>小窓へ出す値。</summary>
internal sealed class WidgetViewModel : INotifyPropertyChanged
{
    /// <summary>gradeごとの記号。本体の <c>GRADE_SYMBOLS</c> と同じ。</summary>
    private static readonly string[] GradeSymbols = ["◎", "○", "△", "✕", "✕"];

    private string _headerText = "読み込んでいます";
    private string _symbol = "…";
    private string _levelLabel = "取得中";
    private string _activityText = string.Empty;
    private string _detailText = string.Empty;
    private string _attributionText = string.Empty;
    private string _statusText = string.Empty;
    private Brush _surfaceBrush = Brushes.WhiteSmoke;
    private Brush _textBrush = Brushes.Black;
    private Brush _accentBrush = Brushes.Gray;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>地点と時刻。</summary>
    public string HeaderText { get => _headerText; private set => Set(ref _headerText, value); }

    /// <summary>判定の記号。色だけに頼らないために添える。</summary>
    public string Symbol { get => _symbol; private set => Set(ref _symbol, value); }

    /// <summary>判定の日本語ラベル。</summary>
    public string LevelLabel { get => _levelLabel; private set => Set(ref _levelLabel, value); }

    /// <summary>連続活動時間の文言。</summary>
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }

    /// <summary>気温などの詳細。</summary>
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    /// <summary>出典の表記。</summary>
    public string AttributionText { get => _attributionText; private set => Set(ref _attributionText, value); }

    /// <summary>
    /// 状態の一言。
    /// </summary>
    /// <remarks>
    /// データが古いことと、公式の発表が出ていることを、ここで必ず見せる。
    /// 静かに古い値を出し続けるのがいちばん危ない。
    /// </remarks>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>判定の地の色。</summary>
    public Brush SurfaceBrush { get => _surfaceBrush; private set => Set(ref _surfaceBrush, value); }

    /// <summary>判定の文字の色。</summary>
    public Brush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    /// <summary>枠線の色。</summary>
    public Brush AccentBrush { get => _accentBrush; private set => Set(ref _accentBrush, value); }

    /// <summary>取得した内容を映す。</summary>
    /// <param name="forecast">予報。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <param name="now">いまの時刻。</param>
    public void Apply(ForecastResponse forecast, HeatAlert? alert, string placeName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        AttributionText = forecast.Attribution.WeatherData;

        var hour = ForecastView.SelectCurrentHour(forecast, now);
        if (hour is null)
        {
            LevelLabel = "表示できる時間がありません";
            Symbol = "…";
            ActivityText = string.Empty;
            DetailText = "予報の範囲から外れています。";
            StatusText = string.Empty;
            return;
        }

        var outdoor = hour.Outdoor;
        var cold = outdoor.LevelId.IsCold();
        var grade = Math.Clamp(outdoor.Grade, 0, 4);

        HeaderText = string.Create(CultureInfo.InvariantCulture, $"{placeName}　{FormatHour(hour.Time)}");
        Symbol = cold ? "❄" : GradeSymbols[grade];
        LevelLabel = outdoor.Label;

        ActivityText = outdoor.ActivityMinutes <= 0
            ? "着用中止"
            : string.Create(CultureInfo.InvariantCulture, $"連続活動 {outdoor.ActivityMinutes}分");

        DetailText = string.Create(
            CultureInfo.InvariantCulture,
            $"気温 {hour.Weather.Temperature:0.#}℃　湿度 {hour.Weather.Humidity:0}%　補正後WBGT {outdoor.SuitWbgt:0.#}℃");

        StatusText = BuildStatus(forecast, alert, now);

        var suffix = cold ? "Cold" : grade.ToString(CultureInfo.InvariantCulture);
        SurfaceBrush = Brush($"Level{suffix}Surface");
        TextBrush = Brush($"Level{suffix}Text");
        AccentBrush = Brush($"Level{suffix}Accent");
    }

    /// <summary>取りに行けなかったことを見せる。</summary>
    /// <param name="failures">続けて失敗した回数。</param>
    public void ApplyFailure(int failures)
    {
        StatusText = failures <= 1
            ? "取得に失敗しました。時間をおいて試します。"
            : string.Create(CultureInfo.InvariantCulture, $"取得に失敗しています（{failures}回続けて）。");
    }

    private static string BuildStatus(ForecastResponse forecast, HeatAlert? alert, DateTimeOffset now)
    {
        var parts = new List<string>();

        if (alert is not null)
        {
            parts.Add(alert.Special
                ? $"{alert.PrefectureName}に熱中症特別警戒アラート"
                : $"{alert.PrefectureName}に熱中症警戒アラート");
        }

        if (ForecastView.IsStale(forecast, now))
        {
            var age = ForecastView.AgeOf(forecast, now);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours}時間前の予報です"));
        }

        return string.Join("　", parts);
    }

    private static string FormatHour(string time)
    {
        var parsed = JstTime.ParseLocal(time);
        return parsed is null ? time : parsed.Value.ToString("H時ごろ", CultureInfo.InvariantCulture);
    }

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
