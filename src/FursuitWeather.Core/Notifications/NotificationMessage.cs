namespace FursuitWeather.Core.Notifications;

/// <summary>通知の出どころ。</summary>
public enum NotificationSource
{
    /// <summary>判定の変化。<see cref="Changes.ChangeDetector"/> が決めたもの。</summary>
    Change,

    /// <summary>朝のブリーフィング。<see cref="Briefing.BriefingScheduler"/> が決めたもの。</summary>
    Briefing,
}

/// <summary>
/// 画面へ出す直前まで組み上げた通知。
/// </summary>
/// <remarks>
/// <para>
/// トーストの発行はUIの層が行う。
/// この型はUIに依存しないため、文面そのものをLinuxのランナーでもテストできる。
/// </para>
/// <para>
/// <b>本文は2行までにする。</b>
/// シェルが受け取るのはタイトルと追加の2要素までで、それを超えた行は黙って落ちる。
/// </para>
/// </remarks>
public sealed record NotificationMessage
{
    /// <summary>出どころ。</summary>
    public required NotificationSource Source { get; init; }

    /// <summary>見出し。</summary>
    public required string Title { get; init; }

    /// <summary>本文。2行まで。</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>重い通知として出すか。</summary>
    /// <remarks>
    /// 着用中止級と公式発表だけを重くする。
    /// 重い通知は利用者がアプリごとに一度許可する仕組みのため、
    /// 濫用すると拒否されて全部止まる。
    /// </remarks>
    public bool IsUrgent { get; init; }

    /// <summary>変化の種類。ブリーフィングのときは null。</summary>
    public Changes.NotificationKind? Kind { get; init; }

    /// <summary>記録と診断に使う1行の表記。</summary>
    /// <returns>種類と見出しを並べた文字列。</returns>
    public string Describe() =>
        $"{(Kind is { } kind ? kind.ToString() : "Briefing")}: {Title} / {string.Join(" / ", Lines)}";
}
