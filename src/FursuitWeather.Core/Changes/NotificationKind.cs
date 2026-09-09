namespace FursuitWeather.Core.Changes;

/// <summary>通知の種類。</summary>
/// <remarks>
/// 種類ごとの条件と抑制の規則は、リポジトリの <c>docs/notifications.md</c> にある。
/// </remarks>
public enum NotificationKind
{
    /// <summary>着用中止級。連続活動時間が0分になった。</summary>
    DiscontinueWear,

    /// <summary>短縮。連続活動時間が前回より減った。</summary>
    Shortened,

    /// <summary>公式発表。環境省の熱中症警戒アラートが出た。</summary>
    OfficialAlert,

    /// <summary>復帰。0分から回復した。唯一の改善の通知。</summary>
    Recovery,

    /// <summary>
    /// 1日の上限に達したため、今日はこれ以上出さないことの告知。
    /// </summary>
    /// <remarks>
    /// 黙って止めると「もう悪化がない」と読み違えられる。
    /// 1日1回だけ出す。
    /// </remarks>
    DailyCapReached,
}

/// <summary>レベルの分類。</summary>
public static class LevelClassification
{
    /// <summary>低温側のレベルかどうか。</summary>
    /// <param name="level">レベル。</param>
    /// <returns>低温側なら true。</returns>
    /// <remarks>
    /// 熱中症の応急対応への導線は、暑熱側にだけ出す。
    /// 低温の <c>coldDanger</c> に熱中症の手順を出すのは誤誘導になる。
    /// </remarks>
    public static bool IsCold(this Models.ActivityLevel level) => level switch
    {
        Models.ActivityLevel.Optimal => true,
        Models.ActivityLevel.ColdCaution => true,
        Models.ActivityLevel.ColdWarning => true,
        Models.ActivityLevel.ColdDanger => true,
        _ => false,
    };
}
