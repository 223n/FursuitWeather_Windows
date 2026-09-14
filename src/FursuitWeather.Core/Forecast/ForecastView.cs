using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Forecast;

/// <summary>
/// 受け取った予報から、小窓へ出す1件を選ぶ。
/// </summary>
/// <remarks>
/// 判定そのもの（暑さ指数の計算、レベルの判定、連続活動時間の算出）はAPIが行う。
/// ここでやるのは「いまの時間帯にあたる行を選ぶ」ことだけで、判定を複製しない。
/// </remarks>
public static class ForecastView
{
    /// <summary>
    /// 表示中のデータが古いとみなすまでの猶予。
    /// </summary>
    /// <remarks>
    /// 予報のポーリングは11分ごとで、正常なレスポンスは10分間キャッシュされる。
    /// 数回の失敗を許したうえで、1時間を超えたら画面に古さを明示する。
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// いまの時刻にあたる1時間分の予報を選ぶ。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの絶対時刻。</param>
    /// <returns>該当する時間。見つからなければ null。</returns>
    /// <remarks>
    /// <para>
    /// 欠測の時間は <see cref="ForecastResponse.Hours"/> から要素ごと落ちるため、
    /// 添字を時刻とみなしてはならない。かならず時刻の値で突き合わせる。
    /// </para>
    /// <para>
    /// <b>本体と同じ規則で選ぶ。</b>
    /// 日本時間の当日の行から、いまの時間の行を選ぶ。
    /// 無ければ当日の直近の未来の行を選び、それも無ければ選ばない。
    /// 本体の <c>pickCurrentHour</c>（<c>public/app.js</c> と <c>public/display.js</c>）と同じである。
    /// </para>
    /// <para>
    /// 小窓と掲示の両方がこの関数を使う。
    /// 同じ端末で、小窓と掲示が別の時間を「いま」と出さないようにするためである。
    /// 過去へは落とさない。すでに過ぎた時間を「いま」として掲げないためである。
    /// </para>
    /// </remarks>
    public static HourForecast? SelectCurrentHour(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var local = JstTime.ToLocal(now);
        var today = DateOnly.FromDateTime(local);

        HourForecast? exact = null;
        HourForecast? nextFuture = null;
        var nextFutureHour = int.MaxValue;

        foreach (var hour in forecast.Hours)
        {
            if (JstTime.ParseLocal(hour.Time) is not { } time || DateOnly.FromDateTime(time) != today)
            {
                continue;
            }

            if (time.Hour == local.Hour)
            {
                exact ??= hour;
            }
            else if (time.Hour > local.Hour && time.Hour < nextFutureHour)
            {
                nextFuture = hour;
                nextFutureHour = time.Hour;
            }
        }

        return exact ?? nextFuture;
    }

    /// <summary>
    /// 指定した日付にあたる日別のまとめを選ぶ。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="date">日本時間の日付。</param>
    /// <returns>該当する日。見つからなければ null。</returns>
    public static DayForecast? SelectDay(ForecastResponse forecast, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var text = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var day in forecast.Days)
        {
            if (string.Equals(day.Date, text, StringComparison.Ordinal))
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>
    /// 表示中のデータが古くなっていないかを見る。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの絶対時刻。</param>
    /// <returns>古ければ true。</returns>
    public static bool IsStale(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        return now - forecast.GeneratedAt > StaleAfter;
    }

    /// <summary>
    /// 取得してからの経過を返す。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの絶対時刻。</param>
    /// <returns>経過した時間。負にはならない。</returns>
    public static TimeSpan AgeOf(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        var age = now - forecast.GeneratedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
