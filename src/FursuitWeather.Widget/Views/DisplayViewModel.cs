using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FursuitWeather.Core.Display;
using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Widget.Views;

/// <summary>判定のバッジ。記号とラベルと配色を1組で持つ。</summary>
/// <param name="Symbol">記号。低温なら印に <c>grade</c> の記号を添えたもの。</param>
/// <param name="Label">日本語のラベル。</param>
/// <param name="Surface">地の色。</param>
/// <param name="Text">文字の色。</param>
/// <param name="Accent">枠の色。</param>
internal sealed record DisplayBadge(string Symbol, string Label, Brush Surface, Brush Text, Brush Accent)
{
    /// <summary>まだ判定が無いときのバッジ。</summary>
    public static DisplayBadge Pending { get; } =
        new("…", "取得中", Brushes.WhiteSmoke, Brushes.Black, Brushes.Gray);

    /// <summary>判定からバッジを組む。</summary>
    /// <param name="level">レベルの名前。</param>
    /// <param name="label">日本語のラベル。</param>
    /// <param name="grade">深刻度。</param>
    /// <returns>バッジ。</returns>
    public static DisplayBadge Of(string? level, string label, int grade)
    {
        var tone = DisplayTone.From(level, grade);
        var suffix = tone == LevelTone.Cold
            ? "Cold"
            : ((int)tone).ToString(CultureInfo.InvariantCulture);

        return new DisplayBadge(
            DisplayTone.Symbol(level, grade),
            label,
            Find($"Level{suffix}Surface"),
            Find($"Level{suffix}Text"),
            Find($"Level{suffix}Accent"));
    }

    /// <summary>まとめの判定からバッジを組む。</summary>
    /// <param name="summary">日や都市の代表の判定。</param>
    /// <returns>バッジ。</returns>
    public static DisplayBadge Of(LevelSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Of(summary.Level, summary.Label, summary.Grade);
    }

    private static Brush Find(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>この後の予報の1こま。</summary>
/// <param name="Time">時刻。日付が変われば「翌」を添えたもの。</param>
/// <param name="Weather">天気の日本語。</param>
/// <param name="Temperature">気温。</param>
/// <param name="Badge">屋外の判定。</param>
internal sealed record DisplayHourCell(string Time, string Weather, string Temperature, DisplayBadge Badge);

/// <summary>3日間の天気の1こま。</summary>
/// <param name="Date">日付。「今日 8/19（水）」の形。</param>
/// <param name="Weather">天気の日本語。</param>
/// <param name="Temperature">最高と最低。</param>
/// <param name="Laundry">洗濯の文字。無ければ空。</param>
/// <param name="Badge">日中でもっとも厳しい屋外の判定。</param>
internal sealed record DisplayDayCell(
    string Date,
    string Weather,
    string Temperature,
    string Laundry,
    DisplayBadge Badge);

/// <summary>全国の天気の1こま。</summary>
/// <param name="Name">都市名。</param>
/// <param name="Weather">天気の日本語。</param>
/// <param name="Temperature">最高気温。</param>
/// <param name="Badge">日中でもっとも厳しい屋外の判定。</param>
internal sealed record DisplayCityCell(string Name, string Weather, string Temperature, DisplayBadge Badge);

/// <summary>もしものときの1つの手順。</summary>
/// <param name="Number">番号。</param>
/// <param name="Title">見出し。</param>
/// <param name="Detail">中身。</param>
internal sealed record DisplayEmergencyItem(string Number, string Title, string Detail);

/// <summary>掲示へ流し込む材料。</summary>
internal sealed record DisplayInputs
{
    /// <summary>予報。まだ取れていなければ null。</summary>
    public ForecastResponse? Forecast { get; init; }

    /// <summary>公式の発表。無ければ null。</summary>
    public HeatAlert? Alert { get; init; }

    /// <summary>全国の天気。出せる状態でなければ null。</summary>
    /// <remarks>対象日が今日でない応答は、ここへ渡す前に落とす。</remarks>
    public NationalResponse? National { get; init; }

    /// <summary>地点の表示名。</summary>
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>上の帯に積む注意の材料。</summary>
    public DisplayNoticeInputs Notices { get; init; } = new();
}

/// <summary>
/// 掲示へ出す値。
/// </summary>
/// <remarks>
/// <para>
/// 判定は複製しない。
/// Coreの <see cref="DisplayBand"/> と <see cref="DisplayForecast"/> が選んだものを、文字と色へ写すだけである。
/// </para>
/// <para>
/// 出す内容は本体のWebの掲示の画面に倣う（<c>docs/display.md</c>）。
/// 同じ会場でWebの画面と並べたときに、食い違わないようにするためである。
/// </para>
/// </remarks>
internal sealed class DisplayViewModel : INotifyPropertyChanged
{
    private string _placeName = string.Empty;
    private string _clockText = "--:--";
    private DisplayBadge _nowBadge = DisplayBadge.Pending;
    private string _nowMinutesText = string.Empty;
    private bool _hasNow;
    private bool _hasHours;
    private bool _hasDays;
    private bool _hasCities;
    private string _stripNote = "予報を取得しています";
    private DisplayBadge _lookaheadBadge = DisplayBadge.Pending;
    private string _lookaheadText = string.Empty;
    private bool _hasLookahead;
    private string _alertText = string.Empty;
    private bool _hasAlert;
    private string _asOfText = "--:--時点";
    private string _attributionText = string.Empty;

    private bool _showNow = true;
    private bool _showHours;
    private bool _showDays;
    private bool _showNational;
    private bool _showEmergency;

    private string _nowTimeLine = string.Empty;
    private string _nowTemperature = string.Empty;
    private string _nowAdvice = string.Empty;
    private string _nowWbgtNote = string.Empty;
    private string _nowExtraLine = string.Empty;
    private string _emptyNow = "予報を取得しています";
    private string _emptyHours = "予報を取得しています";
    private string _emptyDays = "予報を取得しています";
    private bool _isPaused;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>地点の表示名。</summary>
    public string PlaceName { get => _placeName; private set => Set(ref _placeName, value); }

    /// <summary>端末の時計。</summary>
    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }

    /// <summary>いまの屋外の判定。</summary>
    public DisplayBadge NowBadge { get => _nowBadge; private set => Set(ref _nowBadge, value); }

    /// <summary>連続活動時間の文。</summary>
    public string NowMinutesText { get => _nowMinutesText; private set => Set(ref _nowMinutesText, value); }

    /// <summary>いまの判定を出せるか。</summary>
    public bool HasNow
    {
        get => _hasNow;
        private set
        {
            if (Set(ref _hasNow, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingNow)));
            }
        }
    }

    /// <summary>いまの判定を出せないか。出せないときの一言を出すために持つ。</summary>
    public bool MissingNow => !HasNow;

    /// <summary>この後の予報を1こまでも出せるか。</summary>
    public bool HasHours
    {
        get => _hasHours;
        private set
        {
            if (Set(ref _hasHours, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingHours)));
            }
        }
    }

    /// <summary>この後の予報が1こまも無いか。</summary>
    public bool MissingHours => !HasHours;

    /// <summary>3日間の天気を1こまでも出せるか。</summary>
    public bool HasDays
    {
        get => _hasDays;
        private set
        {
            if (Set(ref _hasDays, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingDays)));
            }
        }
    }

    /// <summary>3日間の天気が1こまも無いか。</summary>
    public bool MissingDays => !HasDays;

    /// <summary>全国の天気を1こまでも出せるか。</summary>
    public bool HasCities
    {
        get => _hasCities;
        private set
        {
            if (Set(ref _hasCities, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingCities)));
            }
        }
    }

    /// <summary>全国の天気が1こまも無いか。</summary>
    public bool MissingCities => !HasCities;

    /// <summary>いまの判定を出せないときの一言。</summary>
    public string StripNote { get => _stripNote; private set => Set(ref _stripNote, value); }

    /// <summary>先読みの判定。</summary>
    public DisplayBadge LookaheadBadge { get => _lookaheadBadge; private set => Set(ref _lookaheadBadge, value); }

    /// <summary>先読みの文。</summary>
    public string LookaheadText { get => _lookaheadText; private set => Set(ref _lookaheadText, value); }

    /// <summary>先読みを出すか。</summary>
    public bool HasLookahead { get => _hasLookahead; private set => Set(ref _hasLookahead, value); }

    /// <summary>公式の発表の文。</summary>
    public string AlertText { get => _alertText; private set => Set(ref _alertText, value); }

    /// <summary>公式の発表を出すか。</summary>
    public bool HasAlert { get => _hasAlert; private set => Set(ref _hasAlert, value); }

    /// <summary>上の帯に積む注意。</summary>
    public ObservableCollection<string> Notices { get; } = [];

    /// <summary>「HH:MM時点」。</summary>
    public string AsOfText { get => _asOfText; private set => Set(ref _asOfText, value); }

    /// <summary>出典の表記。</summary>
    public string AttributionText { get => _attributionText; private set => Set(ref _attributionText, value); }

    /// <summary>いまの判定のスライドを出しているか。</summary>
    public bool ShowNow { get => _showNow; private set => Set(ref _showNow, value); }

    /// <summary>この後の予報のスライドを出しているか。</summary>
    public bool ShowHours { get => _showHours; private set => Set(ref _showHours, value); }

    /// <summary>3日間の天気のスライドを出しているか。</summary>
    public bool ShowDays { get => _showDays; private set => Set(ref _showDays, value); }

    /// <summary>全国の天気のスライドを出しているか。</summary>
    public bool ShowNational { get => _showNational; private set => Set(ref _showNational, value); }

    /// <summary>もしものときのスライドを出しているか。</summary>
    public bool ShowEmergency { get => _showEmergency; private set => Set(ref _showEmergency, value); }

    /// <summary>いまの判定のスライドの時刻と天気。</summary>
    public string NowTimeLine { get => _nowTimeLine; private set => Set(ref _nowTimeLine, value); }

    /// <summary>いまの気温。</summary>
    public string NowTemperature { get => _nowTemperature; private set => Set(ref _nowTemperature, value); }

    /// <summary>本体が返した注意文。</summary>
    public string NowAdvice { get => _nowAdvice; private set => Set(ref _nowAdvice, value); }

    /// <summary>暑さ指数の素の値と補正後の値。</summary>
    public string NowWbgtNote { get => _nowWbgtNote; private set => Set(ref _nowWbgtNote, value); }

    /// <summary>体感温度、湿度、風速、当日の最高と最低。</summary>
    public string NowExtraLine { get => _nowExtraLine; private set => Set(ref _nowExtraLine, value); }

    /// <summary>いまの判定を出せないときの文。</summary>
    public string EmptyNow { get => _emptyNow; private set => Set(ref _emptyNow, value); }

    /// <summary>この後の予報を出せないときの文。</summary>
    public string EmptyHours { get => _emptyHours; private set => Set(ref _emptyHours, value); }

    /// <summary>3日間の天気を出せないときの文。</summary>
    public string EmptyDays { get => _emptyDays; private set => Set(ref _emptyDays, value); }

    /// <summary>全国の天気を出せないときの文。</summary>
    /// <remarks>
    /// 手元に1件も無いときにだけ出す。
    /// 取り直しに失敗しても前回の都市を出し続けるため、文面は取得できていないことだけを言う。
    /// </remarks>
    public static string EmptyNational => "全国の天気を取得できていません";

    /// <summary>巡回を止めているか。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseLabel)));
            }
        }
    }

    /// <summary>操作の帯に出す、止めるか解くかの文字。</summary>
    public string PauseLabel => IsPaused ? "再開" : "一時停止";

    /// <summary>この後の予報のこま。</summary>
    public ObservableCollection<DisplayHourCell> Hours { get; } = [];

    /// <summary>3日間の天気のこま。</summary>
    public ObservableCollection<DisplayDayCell> Days { get; } = [];

    /// <summary>全国の天気のこま。</summary>
    public ObservableCollection<DisplayCityCell> Cities { get; } = [];

    /// <summary>もしものときの手順。中身は変わらないため作り直さない。</summary>
    public IReadOnlyList<DisplayEmergencyItem> EmergencyItems { get; } =
    [
        .. EmergencySteps.Items.Select((step, index) => new DisplayEmergencyItem(
            (index + 1).ToString(CultureInfo.InvariantCulture),
            step.Title,
            step.Detail)),
    ];

    /// <summary>詳しい手順への案内。</summary>
    public static string EmergencyLink => EmergencySteps.MoreInfo;

    /// <summary>出しているスライドを切り替える。</summary>
    /// <param name="slide">出すスライド。</param>
    public void Select(DisplaySlide slide)
    {
        ShowNow = slide == DisplaySlide.Now;
        ShowHours = slide == DisplaySlide.Hours;
        ShowDays = slide == DisplaySlide.Days;
        ShowNational = slide == DisplaySlide.National;
        ShowEmergency = slide == DisplaySlide.Emergency;
    }

    /// <summary>時計だけを進める。</summary>
    /// <param name="now">いまの時刻。</param>
    public void Tick(DateTimeOffset now) =>
        ClockText = JstTime.ToLocal(now).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 手元の材料で全体を描き直す。
    /// </summary>
    /// <param name="inputs">材料。</param>
    /// <param name="now">いまの時刻。</param>
    public void Apply(DisplayInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        PlaceName = inputs.PlaceName;
        Tick(now);

        var forecast = inputs.Forecast;
        var current = CurrentHour(forecast, now);

        ApplyStrip(forecast, current, now);
        ApplyBands(inputs, forecast, now);
        ApplyNowSlide(forecast, current, now);
        ApplyHoursSlide(forecast, now);
        ApplyDaysSlide(forecast, now);
        ApplyNationalSlide(inputs.National);
    }

    /// <summary>いまの時間の行を選ぶ。もしものときの条件にも同じものを渡す。</summary>
    /// <param name="forecast">予報。まだ無ければ null。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>いまの時間の行。無ければ null。</returns>
    public static HourForecast? CurrentHour(ForecastResponse? forecast, DateTimeOffset now) =>
        forecast is null ? null : ForecastView.SelectCurrentHour(forecast, now);

    private void ApplyStrip(ForecastResponse? forecast, HourForecast? current, DateTimeOffset now)
    {
        if (current is null)
        {
            HasNow = false;
            HasLookahead = false;
            NowBadge = DisplayBadge.Pending;
            NowMinutesText = string.Empty;
            LookaheadText = string.Empty;
            StripNote = forecast is null ? "予報を取得しています" : "いまの時間の予報がありません";
            return;
        }

        HasNow = true;
        NowBadge = DisplayBadge.Of(current.Outdoor.Level, current.Outdoor.Label, current.Outdoor.Grade);
        NowMinutesText = DisplayBand.ActivityText(current.Outdoor.ActivityMinutes);
        StripNote = string.Empty;

        // 先読みは、いまの行があるときだけ意味を持つ
        var ahead = forecast is null ? null : DisplayBand.Lookahead(forecast, current, now);
        if (ahead is null)
        {
            HasLookahead = false;
            LookaheadText = string.Empty;
            return;
        }

        HasLookahead = true;
        LookaheadBadge = DisplayBadge.Of(ahead.Outdoor.Level, ahead.Outdoor.Label, ahead.Outdoor.Grade);
        LookaheadText = string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayBand.HourText(ahead, now)}から {DisplayBand.ActivityText(ahead.Outdoor.ActivityMinutes)}");
    }

    private void ApplyBands(DisplayInputs inputs, ForecastResponse? forecast, DateTimeOffset now)
    {
        var alert = DisplayBand.AlertText(inputs.Alert, now);
        HasAlert = alert is not null;
        AlertText = alert ?? string.Empty;

        var notices = DisplayBand.Notices(
            inputs.Notices with
            {
                ForecastGeneratedAt = forecast?.GeneratedAt,
                NationalGeneratedAt = inputs.National?.GeneratedAt,
            },
            now);

        // 中身が同じなら差し替えない。毎分の描き直しで並びが動くのを避ける
        if (!Notices.SequenceEqual(notices, StringComparer.Ordinal))
        {
            Notices.Clear();
            foreach (var notice in notices)
            {
                Notices.Add(notice);
            }
        }

        AsOfText = forecast is null ? "--:--時点" : DisplayBand.AsOfText(forecast.GeneratedAt);
        AttributionText = forecast?.Attribution.WeatherData ?? string.Empty;
    }

    private void ApplyNowSlide(ForecastResponse? forecast, HourForecast? current, DateTimeOffset now)
    {
        if (current is null)
        {
            EmptyNow = forecast is null ? "予報を取得しています" : "いまの時間の予報がありません";
            NowTimeLine = string.Empty;
            NowTemperature = string.Empty;
            NowAdvice = string.Empty;
            NowWbgtNote = string.Empty;
            NowExtraLine = string.Empty;
            return;
        }

        var outdoor = current.Outdoor;
        var weather = current.Weather;
        var local = JstTime.ParseLocal(current.Time);
        var exact = local is { } time && time.Hour == JstTime.ToLocal(now).Hour;

        // いまの時間そのものでなければ、直近の未来を出していることを添える
        NowTimeLine = local is { } parsed
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{parsed.Hour}時{(exact ? string.Empty : "（直近の時間帯）")}・{current.WeatherLabel}")
            : current.WeatherLabel;

        NowTemperature = string.Create(CultureInfo.InvariantCulture, $"{weather.Temperature:0.#}℃");
        NowAdvice = outdoor.Advice;

        // 会場据付のWBGT計との食い違いを避けるため、素の値と補正後の値を必ず並べる
        NowWbgtNote = string.Create(
            CultureInfo.InvariantCulture,
            $"暑さ指数（WBGT）{outdoor.Wbgt:0.#}℃・着ぐるみ補正後{outdoor.SuitWbgt:0.#}℃（推定値）");

        var extras = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"体感{weather.ApparentTemperature:0.#}℃"),
            string.Create(CultureInfo.InvariantCulture, $"湿度{weather.Humidity:0}%"),
            string.Create(CultureInfo.InvariantCulture, $"風速{weather.WindSpeed:0.#}m/s"),
        };

        if (forecast is not null && DisplayForecast.TodayOf(forecast, now) is { } today)
        {
            extras.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"本日は最高{today.TemperatureMax:0}℃・最低{today.TemperatureMin:0}℃"));
        }

        NowExtraLine = string.Join("・", extras);
    }

    private void ApplyHoursSlide(ForecastResponse? forecast, DateTimeOffset now)
    {
        Hours.Clear();

        if (forecast is null)
        {
            EmptyHours = "予報を取得しています";
            HasHours = false;
            return;
        }

        EmptyHours = "この後の時間の予報がありません";
        foreach (var hour in DisplayForecast.UpcomingHours(forecast, now))
        {
            Hours.Add(new DisplayHourCell(
                DisplayBand.HourText(hour, now),
                hour.WeatherLabel,
                string.Create(CultureInfo.InvariantCulture, $"{hour.Weather.Temperature:0}℃"),
                DisplayBadge.Of(hour.Outdoor.Level, hour.Outdoor.Label, hour.Outdoor.Grade)));
        }

        HasHours = Hours.Count > 0;
    }

    private void ApplyDaysSlide(ForecastResponse? forecast, DateTimeOffset now)
    {
        Days.Clear();

        if (forecast is null)
        {
            EmptyDays = "予報を取得しています";
            HasDays = false;
            return;
        }

        EmptyDays = "日別の予報がありません";
        var today = DisplayForecast.Today(now);

        foreach (var day in DisplayForecast.NextDays(forecast, now))
        {
            Days.Add(new DisplayDayCell(
                DayLabel(day.Date, today),
                day.WeatherLabel,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"最高{day.TemperatureMax:0}℃・最低{day.TemperatureMin:0}℃"),
                day.Laundry is { } laundry ? $"洗濯: {laundry.Label}" : string.Empty,
                DisplayBadge.Of(day.OutdoorWorst)));
        }

        HasDays = Days.Count > 0;
    }

    private void ApplyNationalSlide(NationalResponse? national)
    {
        Cities.Clear();

        if (national is null)
        {
            HasCities = false;
            return;
        }

        foreach (var city in national.Cities)
        {
            Cities.Add(new DisplayCityCell(
                city.Name,
                city.WeatherLabel,
                string.Create(CultureInfo.InvariantCulture, $"最高{city.TemperatureMax:0}℃"),
                DisplayBadge.Of(city.OutdoorWorst)));
        }

        HasCities = Cities.Count > 0;
    }

    /// <summary>
    /// 日付を「今日 8/19（水）」の形にする。
    /// </summary>
    /// <param name="date">日付の文字列。</param>
    /// <param name="today">日本時間の今日。</param>
    /// <returns>出す文字。読めなければそのまま返す。</returns>
    /// <remarks>
    /// 「今日」「明日」は並びの位置ではなく、今日との日付の差で決める。
    /// 取得が止まったまま日付をまたいだとき、昨日の行を「今日」と出さないためである。
    /// </remarks>
    private static string DayLabel(string date, DateOnly today)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return date;
        }

        var relative = (parsed.DayNumber - today.DayNumber) switch
        {
            0 => "今日 ",
            1 => "明日 ",
            2 => "明後日 ",
            _ => string.Empty,
        };

        var weekday = "日月火水木金土"[(int)parsed.DayOfWeek];
        return string.Create(CultureInfo.InvariantCulture, $"{relative}{parsed.Month}/{parsed.Day}（{weekday}）");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
