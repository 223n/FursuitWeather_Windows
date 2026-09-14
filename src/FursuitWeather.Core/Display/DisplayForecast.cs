using FursuitWeather.Core.Forecast;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Display;

/// <summary>
/// 掲示のスライドへ出す行と日を選ぶ。
/// </summary>
/// <remarks>
/// 判定は複製しない。時刻と日付の値で突き合わせて選ぶだけである。
/// <c>hours</c> は欠測で歯抜けになるため、添字を時刻とみなさない。
/// </remarks>
public static class DisplayForecast
{
    /// <summary>この後の予報の窓の幅。</summary>
    public static readonly TimeSpan HoursWindow = TimeSpan.FromHours(6);

    /// <summary>日ごとの予報に出す日数。</summary>
    public const int DayCount = 3;

    /// <summary>
    /// いまの時間の頭から6時間の窓にある行を、時刻の順に選ぶ。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>選んだ行。欠測があれば6行より少ない。</returns>
    /// <remarks>
    /// WebとMac版は、起点を時刻で選んだあと配列の6件を取る。
    /// それでは欠測があると6時間より先の行まで出るため、時刻の窓で選ぶ。
    /// </remarks>
    public static IReadOnlyList<HourForecast> UpcomingHours(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var local = JstTime.ToLocal(now);
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
        var end = start + HoursWindow;

        return
        [
            .. forecast.Hours
                .Select(hour => (Hour: hour, Time: JstTime.ParseLocal(hour.Time)))
                .Where(pair => pair.Time is { } time && time >= start && time < end)
                .OrderBy(pair => pair.Time)
                .Select(pair => pair.Hour),
        ];
    }

    /// <summary>
    /// 今日から日付で選んだ日を返す。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>今日から3日のうち、予報がある日。</returns>
    public static IReadOnlyList<DayForecast> NextDays(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var today = Today(now);
        var days = new List<DayForecast>(DayCount);
        for (var offset = 0; offset < DayCount; offset++)
        {
            if (ForecastView.SelectDay(forecast, today.AddDays(offset)) is { } day)
            {
                days.Add(day);
            }
        }

        return days;
    }

    /// <summary>今日の日ごとの予報。いまの判定のスライドの最高と最低に使う。</summary>
    /// <param name="forecast">予報。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>今日の予報。無ければ null。</returns>
    public static DayForecast? TodayOf(ForecastResponse forecast, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        return ForecastView.SelectDay(forecast, Today(now));
    }

    /// <summary>行の日付が今日より後か。時刻に「翌」を添えるのに使う。</summary>
    /// <param name="hour">行。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>今日より後なら true。</returns>
    public static bool IsAfterToday(HourForecast hour, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hour);
        return JstTime.ParseLocal(hour.Time) is { } time && DateOnly.FromDateTime(time) > Today(now);
    }

    /// <summary>日本時間の今日。</summary>
    /// <param name="now">いまの時刻。</param>
    /// <returns>日付。</returns>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(JstTime.ToLocal(now));
}
