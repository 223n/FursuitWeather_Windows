using System.Globalization;
using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Notifications;

/// <summary>
/// 通知の文面を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// 文面の型は <c>docs/notifications.md</c> の「文面とボタン」に従う。
/// UIに依存させずCoreへ置く。文面は安全に関わるため、テストで固めたいからである。
/// </para>
/// <para>
/// <b>レベルIDから日本語へ引く表は持たない。</b>
/// APIが返したラベルをそのまま使う。表を持つと判定の複製になり、
/// 本体がラベルを変えたときに黙って食い違う。
/// ラベルが欠けているときは、その部分を省いた文面へ落とす。
/// </para>
/// </remarks>
public static class NotificationText
{
    /// <summary>本文の要素の区切り。</summary>
    private const string Separator = "　";

    /// <summary>
    /// 変化の通知の文面を組み立てる。
    /// </summary>
    /// <param name="notification">出すべき通知。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <param name="alert">公式の発表。無ければ null。</param>
    /// <returns>組み上がった文面。</returns>
    public static NotificationMessage Build(PendingNotification notification, string placeName, HeatAlert? alert = null)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return notification.Kind switch
        {
            NotificationKind.DiscontinueWear => Discontinue(notification, placeName),
            NotificationKind.Shortened => Shortened(notification, placeName),
            NotificationKind.OfficialAlert => Official(notification, alert),
            NotificationKind.Recovery => Recovery(notification, placeName),
            NotificationKind.DailyCapReached => DailyCap(placeName),
            _ => Unknown(notification, placeName),
        };
    }

    /// <summary>
    /// 朝のブリーフィングの文面を組み立てる。
    /// </summary>
    /// <param name="content">ブリーフィングの中身。</param>
    /// <param name="placeName">地点の表示名。</param>
    /// <returns>組み上がった文面。</returns>
    public static NotificationMessage Build(BriefingContent content, string placeName)
    {
        ArgumentNullException.ThrowIfNull(content);

        var head = new List<string>();
        if (!string.IsNullOrEmpty(content.Worst.Label))
        {
            head.Add($"もっとも厳しい判定 {content.Worst.Label}");
        }

        head.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"最高{content.TemperatureMax:0.#}℃ / 最低{content.TemperatureMin:0.#}℃"));

        // 危険を先に置く。切り詰められたときに残るのは前のほうだからである
        var tail = new List<string>();
        if (content.AlertActive)
        {
            tail.Add("熱中症警戒アラートが出ています");
        }

        if (content.SuddenHeat is { } sudden)
        {
            tail.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"急な暑さ（直近7日の平均より{sudden.TargetMax - sudden.RecentAverageMax:0.#}℃高い）"));
        }

        tail.Add(content.HasNoRecommendedHours
            ? "屋外の活動に向く時間帯はありません"
            : $"向く時間帯 {FormatHours(content.RecommendedHours)}");

        if (content.CoolingRequired)
        {
            tail.Add("日中は冷房が必須です");
        }

        return new NotificationMessage
        {
            Source = NotificationSource.Briefing,
            Title = $"今日の着ぐるみ（{placeName}）",
            Lines = [string.Join(Separator, head), string.Join(Separator, tail)],
            IsUrgent = false,
        };
    }

    private static NotificationMessage Discontinue(PendingNotification n, string placeName) => new()
    {
        Source = NotificationSource.Change,
        Kind = n.Kind,
        Title = $"着用中止（{placeName}）",
        Lines =
        [
            Join(HourText(n), LevelText(n), MinutesText(n)),

            // 低温の grade 4 に熱中症の手順を出すのは誤誘導になる
            n.IsCold
                ? "凍傷とスーツ素材の低温劣化の恐れがあります"
                : "屋外での着用を中止し、冷房環境へ退避してください",
        ],
        IsUrgent = true,
    };

    private static NotificationMessage Shortened(PendingNotification n, string placeName) => new()
    {
        Source = NotificationSource.Change,
        Kind = n.Kind,
        Title = string.Create(
            CultureInfo.InvariantCulture,
            $"連続{n.PreviousMinutes}分 → {n.CurrentMinutes}分（{placeName}）"),
        Lines =
        [
            Join(HourText(n), LevelText(n)),

            // 三項演算子の中で補間を書くと、補間の結果が先に文字列になり、
            // 書式化の文化圏を渡す口が閉じる。分岐は外へ出す
            n.IsCold
                ? string.Create(CultureInfo.InvariantCulture, $"{n.CurrentMinutes}分以内にとどめ、暖かい屋内へ退避してください")
                : string.Create(CultureInfo.InvariantCulture, $"{n.CurrentMinutes}分以内にとどめ、屋内の冷房環境へ退避してください"),
        ],
        IsUrgent = false,
    };

    /// <summary>
    /// 公式発表の文面。
    /// </summary>
    /// <remarks>
    /// 見出しには県名を入れる。
    /// 県境の付近では隣の県と判定されることがあり、どの県への発表かが行動を変えるためである。
    /// </remarks>
    private static NotificationMessage Official(PendingNotification n, HeatAlert? alert)
    {
        var kind = (alert ?? new HeatAlert()).KindName;
        var name = string.IsNullOrEmpty(alert?.PrefectureName) ? "発表地域" : alert.PrefectureName;

        var second = string.IsNullOrEmpty(n.CurrentLabel)
            ? "屋外の予定を主催と確認してください"
            : $"着ぐるみの判定は「{n.CurrentLabel}」です。屋外の予定を主催と確認してください";

        return new NotificationMessage
        {
            Source = NotificationSource.Change,
            Kind = n.Kind,
            Title = $"{kind}（{name}）",
            Lines = ["環境省・気象庁が発表しました", second],
            IsUrgent = true,
        };
    }

    private static NotificationMessage Recovery(PendingNotification n, string placeName) => new()
    {
        Source = NotificationSource.Change,
        Kind = n.Kind,
        Title = $"着用を再開できる可能性があります（{placeName}）",
        Lines =
        [
            Join(
                HourText(n),
                n.CurrentLabel,
                string.Create(CultureInfo.InvariantCulture, $"連続{n.CurrentMinutes}分")),
            "体調と装備を確認してください",
        ],
        IsUrgent = false,
    };

    /// <summary>
    /// 打ち切りの告知の文面。
    /// </summary>
    /// <remarks>
    /// 「悪化が止まった」と読み違えられないようにする。
    /// 黙って止めるより危ないのは、止めたことを安心の合図として受け取られることである。
    /// </remarks>
    private static NotificationMessage DailyCap(string placeName) => new()
    {
        Source = NotificationSource.Change,
        Kind = NotificationKind.DailyCapReached,
        Title = $"今日はこれ以上お知らせしません（{placeName}）",
        Lines =
        [
            "1日の上限に達しました。悪化が止まったという意味ではありません",
            "最新の判定は小窓の表示で確かめてください",
        ],
        IsUrgent = false,
    };

    /// <summary>
    /// 知らない種類が来たときの文面。
    /// </summary>
    /// <remarks>
    /// 黙って捨てない。種類を足したのに文面を足し忘れたことが、利用者の側から見える。
    /// </remarks>
    private static NotificationMessage Unknown(PendingNotification n, string placeName) => new()
    {
        Source = NotificationSource.Change,
        Kind = n.Kind,
        Title = $"判定が変わりました（{placeName}）",
        Lines = [Join(HourText(n), LevelText(n), MinutesText(n)), "小窓の表示で確かめてください"],
        IsUrgent = n.IsUrgent,
    };

    /// <summary>「15時ごろ」の形にする。時刻が読めなければ空。</summary>
    private static string HourText(PendingNotification n)
    {
        var parsed = JstTime.ParseLocal(n.Hour?.Time);
        return parsed is null ? string.Empty : parsed.Value.ToString("H時ごろ", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 「警戒 → 厳重警戒」の形にする。
    /// </summary>
    /// <remarks>
    /// 前後のどちらかが欠けている、または同じときは、いまのラベルだけを返す。
    /// 矢印だけが残る文面を作らない。
    /// </remarks>
    private static string LevelText(PendingNotification n)
    {
        if (string.IsNullOrEmpty(n.CurrentLabel))
        {
            return n.PreviousLabel;
        }

        if (string.IsNullOrEmpty(n.PreviousLabel) ||
            string.Equals(n.PreviousLabel, n.CurrentLabel, StringComparison.Ordinal))
        {
            return n.CurrentLabel;
        }

        return $"{n.PreviousLabel} → {n.CurrentLabel}";
    }

    /// <summary>
    /// 「連続10分 → 0分」の形にする。
    /// </summary>
    /// <remarks>
    /// 前回も0分だったときは「連続0分 → 0分」になり、何も伝えない。
    /// danger から coldDanger へ移ったときに実際に起こるため、その場合は現在の値だけを出す。
    /// </remarks>
    private static string MinutesText(PendingNotification n) =>
        n.PreviousMinutes == n.CurrentMinutes
            ? string.Create(CultureInfo.InvariantCulture, $"連続{n.CurrentMinutes}分")
            : string.Create(CultureInfo.InvariantCulture, $"連続{n.PreviousMinutes}分 → {n.CurrentMinutes}分");

    /// <summary>空でない要素だけを区切りでつなぐ。</summary>
    private static string Join(params string[] parts) =>
        string.Join(" ・ ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// 「9時台 / 17時台」の形にする。
    /// </summary>
    /// <remarks>
    /// 読めない値はそのまま残す。勝手に減らすより、そのまま見せるほうが安全側である。
    /// 4件以上あるときは3件で切り、残りの数を添える。
    /// </remarks>
    private static string FormatHours(IReadOnlyList<string> hours)
    {
        var formatted = hours.Select(h =>
            TimeOnly.TryParseExact(h, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? string.Create(CultureInfo.InvariantCulture, $"{parsed.Hour}時台")
                : h).ToList();

        if (formatted.Count <= 3)
        {
            return string.Join(" / ", formatted);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{string.Join(" / ", formatted.Take(3))} ほか{formatted.Count - 3}件");
    }
}
