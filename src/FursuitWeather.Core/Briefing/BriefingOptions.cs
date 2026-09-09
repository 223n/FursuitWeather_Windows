namespace FursuitWeather.Core.Briefing;

/// <summary>朝のブリーフィングの調整値。</summary>
/// <remarks>
/// 根拠は <c>docs/notifications.md</c> にある。
/// </remarks>
public sealed record BriefingOptions
{
    /// <summary>既定の設定。</summary>
    public static BriefingOptions Default { get; } = new();

    /// <summary>出すかどうか。</summary>
    /// <remarks>
    /// 既定でONにする。
    /// 通知の総数は増えるが、飽和して変化しない期間の沈黙を埋める役割のほうが重い。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>出す時刻（日本時間）。</summary>
    public TimeOnly DeliverAt { get; init; } = new(8, 0);

    /// <summary>
    /// この時刻を過ぎたら、その日は出さない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 昼を回ってから「今日の予定」を知らせても遅い。
    /// その時間帯の悪化はトーストの層が受け持つ。
    /// </para>
    /// <para>
    /// 夜にPCを起動したときへ持ち越して出すと、翌朝のぶんと紛らわしくなる。
    /// </para>
    /// </remarks>
    public TimeOnly LatestDeliveryAt { get; init; } = new(12, 0);
}
