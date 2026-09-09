using System.Globalization;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Briefing;

/// <summary>
/// 朝のブリーフィングを出す時期と中身を決める。
/// </summary>
/// <remarks>
/// <para>
/// 表示の3つの層のうち、<b>飽和して変化しない期間の沈黙を埋める</b>役割を持つ。
/// </para>
/// <para>
/// 実測では、那覇は88時間（およそ3.7日）まったく判定が変化しない。
/// 変化の検知だけを作ると、最も危険な期間に何日も無音になる。
/// 変化が無くても1日1回は現状を伝えるのが、この層の目的である。
/// </para>
/// <para>
/// 純粋な関数として書く。時計もファイルも触らない。
/// </para>
/// </remarks>
public static class BriefingScheduler
{
    /// <summary>
    /// いまブリーフィングを出すべきかを見て、出すなら中身を作る。
    /// </summary>
    /// <param name="forecast">予報。</param>
    /// <param name="alertActive">公式の熱中症警戒アラートが出ているか。</param>
    /// <param name="lastDeliveredDate">最後に出した日付（日本時間）。まだ無ければ null。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="options">調整値。省略すると既定値を使う。</param>
    /// <returns>出すべき中身。出す時期でなければ null。</returns>
    public static BriefingContent? TryBuild(
        ForecastResponse forecast,
        bool alertActive,
        DateOnly? lastDeliveredDate,
        DateTimeOffset now,
        BriefingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var opt = options ?? BriefingOptions.Default;
        if (!opt.Enabled)
        {
            return null;
        }

        var local = JstTime.ToLocal(now);
        var today = DateOnly.FromDateTime(local);
        var clock = TimeOnly.FromDateTime(local);

        // まだ出す時刻になっていない
        if (clock < opt.DeliverAt)
        {
            return null;
        }

        // 遅すぎる。その時間帯の悪化はトーストの層が受け持つ
        if (clock > opt.LatestDeliveryAt)
        {
            return null;
        }

        // 今日はもう出した
        if (lastDeliveredDate == today)
        {
            return null;
        }

        var day = FindDay(forecast, today);
        if (day is null)
        {
            return null;
        }

        return new BriefingContent
        {
            Date = today,
            Worst = day.OutdoorWorst,
            TemperatureMax = day.TemperatureMax,
            TemperatureMin = day.TemperatureMin,
            RecommendedHours = FilterUpcoming(day.RecommendedHours, clock),
            CoolingRequired = day.CoolingRequired,
            SuddenHeat = forecast.SuddenHeat,
            AlertActive = alertActive,
            IsCold = ToLevel(day.OutdoorWorst.Level).IsCold(),
        };
    }

    /// <summary>指定した日付の日別のまとめを探す。</summary>
    private static DayForecast? FindDay(ForecastResponse forecast, DateOnly date)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
    /// これから来る時間帯だけを残す。
    /// </summary>
    /// <remarks>
    /// 読めない値は落とさずに残す。
    /// 勧める時間を勝手に減らすより、そのまま見せるほうが安全側であるためである。
    /// </remarks>
    private static List<string> FilterUpcoming(IReadOnlyList<string> hours, TimeOnly clock)
    {
        var upcoming = new List<string>(hours.Count);
        foreach (var hour in hours)
        {
            if (!TimeOnly.TryParseExact(hour, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                upcoming.Add(hour);
                continue;
            }

            // いまが属する時間帯は残す。9時30分なら9時台はまだ使える
            if (parsed.Hour >= clock.Hour)
            {
                upcoming.Add(hour);
            }
        }

        return upcoming;
    }

    /// <summary>レベルIDを列挙へ写す。</summary>
    private static ActivityLevel ToLevel(string level) =>
        new ActivityAssessment { Level = level }.LevelId;
}
