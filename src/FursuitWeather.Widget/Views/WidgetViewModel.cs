using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Display;
using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Widget.Views;

/// <summary>小窓へ出す値。</summary>
internal sealed class WidgetViewModel : INotifyPropertyChanged
{

    /// <summary>
    /// トーストの代わりにここへ出した知らせを、いつまで残すか。
    /// </summary>
    /// <remarks>
    /// 消さずに残すと、過ぎた悪化がいつまでも出たままになる。
    /// 短すぎると席を外しているあいだに消える。次の取得を数回はさむ長さにする。
    /// </remarks>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromMinutes(45);

    private string _headerText = "読み込んでいます";
    private string _symbol = "…";
    private string _levelLabel = "取得中";
    private string _activityText = string.Empty;
    private string _detailText = string.Empty;
    private string _attributionText = string.Empty;
    private string _statusText = string.Empty;
    private string _baseStatus = string.Empty;
    private string _notice = string.Empty;
    private DateTimeOffset _noticeAt;
    private Brush _surfaceBrush = Brushes.WhiteSmoke;
    private Brush _textBrush = Brushes.Black;
    private Brush _accentBrush = Brushes.Gray;
    private bool _pending = true;

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

    /// <summary>
    /// 地点を切り替えたことを見せる。
    /// </summary>
    /// <param name="placeName">新しい地点の表示名。</param>
    /// <remarks>
    /// 前の地点の判定を残さない。
    /// 取得に失敗したまま古い判定を出し続けると、別の場所の危険度を見て行動してしまう。
    /// </remarks>
    public void ApplyLocationPending(string placeName)
    {
        _pending = true;
        HeaderText = placeName;
        Symbol = "…";
        LevelLabel = "取得中";
        ActivityText = string.Empty;
        DetailText = string.Empty;

        // 前の地点についての知らせは、新しい地点では意味を持たない
        _notice = string.Empty;
        _baseStatus = "地点を変えました。新しい地点の予報をまだ取得できていません。";
        RefreshStatus();
        SurfaceBrush = Brushes.WhiteSmoke;
        TextBrush = Brushes.Black;
        AccentBrush = Brushes.Gray;
    }

    /// <summary>取得した内容を映す。</summary>
    /// <param name="forecast">予報。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <param name="now">いまの時刻。</param>
    public void Apply(ForecastResponse forecast, HeatAlert? alert, string placeName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        AttributionText = forecast.Attribution.WeatherData;
        _pending = false;

        var hour = ForecastView.SelectCurrentHour(forecast, now);
        if (hour is null)
        {
            LevelLabel = "表示できる時間がありません";
            Symbol = "…";
            ActivityText = string.Empty;
            DetailText = "予報の範囲から外れています。";
            _baseStatus = string.Empty;
            RefreshStatus();
            return;
        }

        var outdoor = hour.Outdoor;
        var cold = outdoor.LevelId.IsCold();
        var grade = Math.Clamp(outdoor.Grade, 0, 4);

        HeaderText = string.Create(CultureInfo.InvariantCulture, $"{placeName}　{FormatHour(hour.Time)}");
        // 記号の表は掲示と共有する。低温の見分け方は小窓のまま IsCold() を使う
        Symbol = cold ? DisplayTone.ColdSymbol : DisplayTone.GradeSymbol(grade);
        LevelLabel = outdoor.Label;

        ActivityText = outdoor.ActivityMinutes <= 0
            ? "着用中止"
            : string.Create(CultureInfo.InvariantCulture, $"連続活動 {outdoor.ActivityMinutes}分");

        DetailText = string.Create(
            CultureInfo.InvariantCulture,
            $"気温 {hour.Weather.Temperature:0.#}℃　湿度 {hour.Weather.Humidity:0}%　補正後WBGT {outdoor.SuitWbgt:0.#}℃");

        ExpireNotice(now);
        _baseStatus = BuildStatus(forecast, alert, now);
        RefreshStatus();

        var suffix = cold ? "Cold" : grade.ToString(CultureInfo.InvariantCulture);
        SurfaceBrush = Brush($"Level{suffix}Surface");
        TextBrush = Brush($"Level{suffix}Text");
        AccentBrush = Brush($"Level{suffix}Accent");
    }

    /// <summary>取りに行けなかったことを見せる。</summary>
    /// <param name="failures">続けて失敗した回数。</param>
    /// <param name="now">いまの時刻。</param>
    /// <remarks>
    /// ここでも知らせの期限を見る。
    /// <see cref="Apply"/> でしか見ないと、取得が失敗し続けるあいだ知らせが消えない。
    /// </remarks>
    public void ApplyFailure(int failures, DateTimeOffset now)
    {
        ExpireNotice(now);

        if (_pending)
        {
            // まだ一度も取れていない。古い判定は出ていないが、何も分からないことを伝える
            _baseStatus = failures <= 1
                ? "予報をまだ取得できていません。時間をおいて試します。"
                : string.Create(CultureInfo.InvariantCulture, $"予報をまだ取得できていません（{failures}回続けて失敗）。");
            RefreshStatus();
            return;
        }

        _baseStatus = failures <= 1
            ? "取得に失敗しました。表示は前回の値です。"
            : string.Create(CultureInfo.InvariantCulture, $"取得に失敗しています（{failures}回続けて）。表示は前回の値です。");
        RefreshStatus();
    }

    /// <summary>
    /// トーストの代わりに、知らせを小窓へ出す。
    /// </summary>
    /// <param name="text">知らせの本文。</param>
    /// <param name="now">いまの時刻。</param>
    /// <remarks>
    /// トーストが出せなかったときの受け皿である。
    /// 通知だけが静かに壊れる状態を作らないために要る。
    /// </remarks>
    public void ApplyNotice(string text, DateTimeOffset now)
    {
        _notice = text ?? string.Empty;
        _noticeAt = now;
        RefreshStatus();
    }

    /// <summary>知らせが古くなっていれば消す。</summary>
    private void ExpireNotice(DateTimeOffset now)
    {
        if (_notice.Length > 0 && now - _noticeAt > NoticeLifetime)
        {
            _notice = string.Empty;
        }
    }

    /// <summary>
    /// 知らせと状態の一言を並べ直す。
    /// </summary>
    /// <remarks>
    /// 知らせを前へ置く。行が入りきらないときに残るのは前のほうだからである。
    /// </remarks>
    private void RefreshStatus()
    {
        StatusText = _notice.Length > 0 && _baseStatus.Length > 0
            ? $"{_notice}　{_baseStatus}"
            : _notice + _baseStatus;
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
