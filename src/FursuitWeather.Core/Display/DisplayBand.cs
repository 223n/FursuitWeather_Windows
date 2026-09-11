using System.Globalization;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Display;

/// <summary>データの鮮度。</summary>
public enum Freshness
{
    /// <summary>新しい。</summary>
    Fresh,

    /// <summary>古い。</summary>
    Stale,

    /// <summary>生成の時刻が未来にある。端末の時計が遅れている疑いがある。</summary>
    ClockBehind,
}

/// <summary>上の帯に積む注意を組むための材料。</summary>
public sealed record DisplayNoticeInputs
{
    /// <summary>直近の予報の取得に失敗したか。</summary>
    public bool ForecastFailed { get; init; }

    /// <summary>手元の予報の生成時刻。まだ無ければ null。</summary>
    public DateTimeOffset? ForecastGeneratedAt { get; init; }

    /// <summary>手元の全国の天気の生成時刻。まだ無ければ null。</summary>
    public DateTimeOffset? NationalGeneratedAt { get; init; }

    /// <summary>掲示先のモニターが外れ、ほかのモニターへ移したか。</summary>
    public bool MonitorMoved { get; init; }

    /// <summary>選んだモニターが見つからず、主モニターへ出しているか。</summary>
    public bool MonitorMissing { get; init; }

    /// <summary>地点が既定のままか。</summary>
    public bool DefaultLocation { get; init; }

    /// <summary>既定の地点の表示名。</summary>
    public string DefaultPlaceName { get; init; } = string.Empty;

    /// <summary>運営者向けの注意を出してよいか。掲示に入ってから60秒のあいだだけ真にする。</summary>
    public bool OperatorWindow { get; init; }

    /// <summary>掲示を終えるホットキーを登録できなかったか。</summary>
    public bool HotkeyFailed { get; init; }

    /// <summary>起動したときの更新の成否。無ければ null。</summary>
    public string? UpdateResult { get; init; }
}

/// <summary>
/// 上と下の帯に出すものを組む。
/// </summary>
/// <remarks>
/// 判定は複製しない。
/// いまの行と先読みの行の選び方は、小窓とトーストの判定と同じ関数を使う。
/// </remarks>
public static class DisplayBand
{
    /// <summary>先読みの幅。</summary>
    /// <remarks>
    /// 通知の設定に関わらず3時間で固定する。
    /// トースト（T1）の幅を利用者が変えられるようになっても、掲示は変えない。
    /// </remarks>
    public static readonly TimeSpan LookaheadWindow = TimeSpan.FromHours(3);

    /// <summary>生成の時刻が未来へずれていても、時計の狂いとみなさない幅。</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>運営者向けの注意を出しておく時間。</summary>
    public static readonly TimeSpan OperatorNoticeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// この先3時間に、いまより厳しい時間があれば選ぶ。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="current">いまの時間の行。無ければ null。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>選んだ行。無ければ null。</returns>
    /// <remarks>
    /// <para>
    /// 掲示のあいだトーストを止める代わりに、トースト（T1）の先読みを帯へ出す。
    /// 選び方はT1と同じ <see cref="ChangeDetector.SelectTarget"/> である。
    /// </para>
    /// <para>
    /// 比べるのは連続活動時間で、<c>grade</c> では比べない。
    /// 低温側には <c>grade 3</c> が無く、<c>optimal</c> から <c>coldCaution</c> へは <c>grade</c> が上がるためである。
    /// </para>
    /// </remarks>
    public static HourForecast? Lookahead(ForecastResponse forecast, HourForecast? current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var target = ChangeDetector.SelectTarget(forecast, now, LookaheadWindow);
        if (target is null)
        {
            return null;
        }

        if (current is null)
        {
            return target;
        }

        return target.Outdoor.ActivityMinutes < current.Outdoor.ActivityMinutes ? target : null;
    }

    /// <summary>
    /// 公式の発表の帯の文を組む。
    /// </summary>
    /// <param name="alert">発表。無ければ null。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>帯の文。出さないなら null。</returns>
    /// <remarks>
    /// <para>
    /// 「環境省発表」と県名を必ず出す。県境の付近では、隣の県と判定されることがあるためである。
    /// </para>
    /// <para>
    /// 対象日を過ぎた発表は出さない。
    /// 対象日を読めないときは出す。発表を黙って隠すより、出すほうが安全側である。
    /// </para>
    /// </remarks>
    public static string? AlertText(HeatAlert? alert, DateTimeOffset now)
    {
        if (alert is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(alert.TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var target) &&
            target < DisplayForecast.Today(now))
        {
            return null;
        }

        var prefecture = string.IsNullOrWhiteSpace(alert.PrefectureName) ? "発表地域" : alert.PrefectureName;
        return $"環境省発表: {prefecture}に{alert.KindName}";
    }

    /// <summary>連続活動時間の文。</summary>
    /// <param name="minutes">連続して活動できる分。</param>
    /// <returns>0分以下なら「着用中止」、それ以外は「連続N分まで」。</returns>
    public static string ActivityText(int minutes) =>
        minutes <= 0
            ? "着用中止"
            : string.Create(CultureInfo.InvariantCulture, $"連続{minutes}分まで");

    /// <summary>行の時刻の文。今日より後なら「翌」を添える。</summary>
    /// <param name="hour">行。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>「14時」や「翌1時」。読めなければ空。</returns>
    public static string HourText(HourForecast hour, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hour);

        if (JstTime.ParseLocal(hour.Time) is not { } time)
        {
            return string.Empty;
        }

        var prefix = DisplayForecast.IsAfterToday(hour, now) ? "翌" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{time.Hour}時");
    }

    /// <summary>「HH:MM時点」の文。</summary>
    /// <param name="generatedAt">生成の時刻。</param>
    /// <returns>日本時間で「9:05時点」の形。</returns>
    public static string AsOfText(DateTimeOffset generatedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{JstTime.ToLocal(generatedAt):H:mm}時点");

    /// <summary>鮮度を見る。</summary>
    /// <param name="generatedAt">生成の時刻。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>鮮度。</returns>
    /// <remarks>
    /// 古さの閾値は小窓と同じ <see cref="ForecastView.StaleAfter"/> を使う。
    /// 全国の天気にも同じ閾値を当てる。
    /// </remarks>
    public static Freshness FreshnessOf(DateTimeOffset generatedAt, DateTimeOffset now)
    {
        if (generatedAt - now > ClockTolerance)
        {
            return Freshness.ClockBehind;
        }

        return now - generatedAt > ForecastView.StaleAfter ? Freshness.Stale : Freshness.Fresh;
    }

    /// <summary>
    /// 上の帯に積む注意を組む。
    /// </summary>
    /// <param name="inputs">材料。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>注意の文。安全に関わるものほど前に置く。</returns>
    /// <remarks>
    /// 1件だけで済ませず、当てはまるものをすべて積む。
    /// 運営者向けの注意は、<see cref="DisplayNoticeInputs.OperatorWindow"/> が真のあいだだけ出す。
    /// 来場者の見る画面に残し続けないためである。
    /// </remarks>
    public static IReadOnlyList<string> Notices(DisplayNoticeInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var notices = new List<string>();

        if (inputs.ForecastGeneratedAt is { } forecastAt)
        {
            switch (FreshnessOf(forecastAt, now))
            {
                case Freshness.ClockBehind:
                    notices.Add("端末の時計が遅れている可能性があります。時計を合わせてください");
                    break;
                case Freshness.Stale:
                    notices.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"予報が{(int)(now - forecastAt).TotalMinutes}分前のままです"));
                    break;
                default:
                    break;
            }
        }

        if (inputs.ForecastFailed)
        {
            notices.Add("予報を取り直せていません。回線を確かめてください");
        }

        if (inputs.DefaultLocation)
        {
            notices.Add($"地点が設定されていません。{inputs.DefaultPlaceName}を表示しています");
        }

        if (inputs.NationalGeneratedAt is { } nationalAt && FreshnessOf(nationalAt, now) == Freshness.Stale)
        {
            notices.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"全国の天気が{(int)(now - nationalAt).TotalMinutes}分前のままです"));
        }

        if (inputs.MonitorMoved)
        {
            notices.Add("掲示先のモニターが外れたため、ほかのモニターに出しています");
        }
        else if (inputs.MonitorMissing)
        {
            notices.Add("選んだモニターが見つからないため、主モニターに出しています");
        }

        if (inputs.OperatorWindow)
        {
            if (inputs.HotkeyFailed)
            {
                notices.Add("掲示を終えるホットキーを登録できませんでした。Escキーかトレイから終えてください");
            }

            if (!string.IsNullOrWhiteSpace(inputs.UpdateResult))
            {
                notices.Add(inputs.UpdateResult);
            }
        }

        return notices;
    }
}
