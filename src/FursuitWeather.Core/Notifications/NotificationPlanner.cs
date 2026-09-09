using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Notifications;

/// <summary>
/// 通知まわりの、保存しておく状態のすべて。
/// </summary>
/// <remarks>
/// 変化の基準と、朝のブリーフィングを最後に出した日をひとまとめにする。
/// 別々のファイルにすると、片方だけ書けた状態が作れてしまう。
/// </remarks>
public sealed record NotificationState
{
    /// <summary>変化を見るときの基準。まだ無ければ null。</summary>
    public ChangeState? Change { get; init; }

    /// <summary>朝のブリーフィングを最後に出した日（日本時間）。まだ無ければ null。</summary>
    public DateOnly? LastBriefingDate { get; init; }
}

/// <summary>
/// 取得のたびに、出すべき通知と次に保存する状態を決める。
/// </summary>
/// <param name="State">次に保存する状態。</param>
/// <param name="Messages">出すべき通知。無ければ空。</param>
public sealed record NotificationPlan(NotificationState State, IReadOnlyList<NotificationMessage> Messages);

/// <summary>
/// 変化の検知と朝のブリーフィングを1本にまとめる。
/// </summary>
/// <remarks>
/// <para>
/// 純粋な関数として書く。時計もファイルもトーストも触らない。
/// UIの層が持つのは「保存する」「出す」だけになる。
/// </para>
/// <para>
/// <b>朝のブリーフィングを先に置く。</b>
/// シェルは後から出したものを上に積むため、
/// 同じ回に悪化とブリーフィングが揃ったときは、行動を変える側が手前に来る。
/// </para>
/// </remarks>
public static class NotificationPlanner
{
    /// <summary>
    /// いまの取得結果から、出すべき通知を決める。
    /// </summary>
    /// <param name="previous">前回までの状態。初回は既定値を渡す。</param>
    /// <param name="forecast">いまの予報。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <param name="locationKey">地点の識別子。丸めたあとの座標を渡す。</param>
    /// <param name="placeName">地点の表示名。文面に使う。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="changeOptions">変化の検知の調整値。省略すると既定値。</param>
    /// <param name="briefingOptions">朝のブリーフィングの調整値。省略すると既定値。</param>
    /// <returns>次に保存する状態と、出すべき通知。</returns>
    public static NotificationPlan Create(
        NotificationState previous,
        ForecastResponse forecast,
        HeatAlert? alert,
        string locationKey,
        string placeName,
        DateTimeOffset now,
        ChangeDetectorOptions? changeOptions = null,
        BriefingOptions? briefingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(locationKey);

        var messages = new List<NotificationMessage>();
        var lastBriefingDate = previous.LastBriefingDate;

        var briefing = BriefingScheduler.TryBuild(
            forecast,
            alert is not null,
            lastBriefingDate,
            now,
            briefingOptions);

        if (briefing is not null)
        {
            messages.Add(NotificationText.Build(briefing, placeName));
            lastBriefingDate = briefing.Date;
        }

        // 発表はそのまま渡す。真偽値へ潰すと、対象日と特別警戒の区別が検知側へ届かない
        var detection = ChangeDetector.Evaluate(
            previous.Change,
            forecast,
            alert,
            locationKey,
            now,
            changeOptions);

        foreach (var pending in detection.Notifications)
        {
            messages.Add(NotificationText.Build(pending, placeName, alert));
        }

        return new NotificationPlan(
            new NotificationState { Change = detection.State, LastBriefingDate = lastBriefingDate },
            messages);
    }

    /// <summary>
    /// いまの基準を1行で表す。診断の画面に出す。
    /// </summary>
    /// <param name="state">状態。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>人が読める1行。</returns>
    /// <remarks>
    /// 通知は「出ないこと」が正しい時間のほうが長い。
    /// 動いているのか壊れているのかを、出ていないあいだにも確かめられるようにする。
    /// </remarks>
    public static string Describe(NotificationState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Change is not { } change)
        {
            return "基準なし（まだ1回も取得できていません）";
        }

        var age = now - change.SavedAt;
        var label = string.IsNullOrEmpty(change.LastLabel) ? change.LastLevel : change.LastLabel;
        var briefing = state.LastBriefingDate is { } date
            ? date.ToString("M月d日", System.Globalization.CultureInfo.InvariantCulture)
            : "まだ出していません";

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"基準 {label} 連続{change.LastMinutes}分（{FormatAge(age)}前 / {change.LocationKey}） / " +
            $"アラート{(change.AlertActive ? "あり" : "なし")} / 履歴{change.History.Count}件 / " +
            $"朝の便り {briefing} / 端末の日付 {JstTime.ToLocal(now):M月d日 H時m分}");
    }

    private static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "1分",
        { TotalHours: < 1 } => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes}分"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)age.TotalHours}時間"),
    };
}
