using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Changes;

/// <summary>出すべき通知。</summary>
/// <remarks>
/// ここは「何を、どの重さで出すか」までを決める。
/// 文面の組み立ては <see cref="Notifications.NotificationText"/> が、
/// トーストの発行はUIの層が行う。
/// </remarks>
public sealed record PendingNotification
{
    /// <summary>種類。</summary>
    public required NotificationKind Kind { get; init; }

    /// <summary>対象の時間。公式発表のときは null。</summary>
    public HourForecast? Hour { get; init; }

    /// <summary>前回の連続活動時間（分）。</summary>
    public int PreviousMinutes { get; init; }

    /// <summary>いまの連続活動時間（分）。</summary>
    public int CurrentMinutes { get; init; }

    /// <summary>前回のレベルID。</summary>
    public string PreviousLevel { get; init; } = string.Empty;

    /// <summary>いまのレベルID。</summary>
    public string CurrentLevel { get; init; } = string.Empty;

    /// <summary>
    /// 前回のレベルの日本語ラベル。分からないときは空。
    /// </summary>
    /// <remarks>
    /// APIが返したラベルをそのまま運ぶ。レベルIDから日本語へ引く表は持たない。
    /// </remarks>
    public string PreviousLabel { get; init; } = string.Empty;

    /// <summary>いまのレベルの日本語ラベル。分からないときは空。</summary>
    public string CurrentLabel { get; init; } = string.Empty;

    /// <summary>内容の署名。同じ署名の通知は再び出さない。</summary>
    public required string Signature { get; init; }

    /// <summary>
    /// 重い通知として出すか。
    /// </summary>
    /// <remarks>
    /// 着用中止級と公式発表だけを重くする。
    /// 重い通知は利用者がアプリごとに一度許可する仕組みのため、
    /// 濫用すると拒否されて全部止まる。
    /// </remarks>
    public bool IsUrgent { get; init; }

    /// <summary>
    /// 択一のグループ。
    /// </summary>
    /// <remarks>
    /// 同じ事象を指す通知に同じ値を入れる。
    /// 抑制を通った先頭の1件だけを出し、残りは捨てる。
    /// 着用中止級と短縮は同じ悪化を指すため、同じグループに入れる。
    /// </remarks>
    public string? AlternativeGroup { get; init; }

    /// <summary>
    /// 低温側の事象か。
    /// </summary>
    /// <remarks>
    /// 熱中症の応急対応への導線は、暑熱側にだけ出す。
    /// </remarks>
    public bool IsCold { get; init; }
}

/// <summary>判定の結果。</summary>
/// <param name="State">次に保存する状態。</param>
/// <param name="Notifications">出すべき通知。無ければ空。</param>
public sealed record DetectionResult(ChangeState State, IReadOnlyList<PendingNotification> Notifications);
