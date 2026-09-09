namespace FursuitWeather.Core.Changes;

/// <summary>判定の悪化を見るときの調整値。</summary>
/// <remarks>
/// 既定値の根拠は <c>docs/notifications.md</c> にある。
/// 実測に基づくものと推定によるものが混ざっているため、変えるときは同文書を見ること。
/// </remarks>
public sealed record ChangeDetectorOptions
{
    /// <summary>既定の設定。</summary>
    public static ChangeDetectorOptions Default { get; } = new();

    /// <summary>
    /// これから何時間先までを見るか。
    /// </summary>
    /// <remarks>
    /// 常駐アプリを使う人はPCの前におり、まだ着ていない。
    /// 「いま悪化した」よりも「これから出るときに悪化している」ほうが行動につながる。
    /// 設定で1時間から6時間まで変えられるようにする。
    /// </remarks>
    public TimeSpan Lookahead { get; init; } = TimeSpan.FromHours(3);

    /// <summary>これより古い状態は基準として使わず、張り直す。</summary>
    public TimeSpan StaleState { get; init; } = TimeSpan.FromHours(6);

    /// <summary>同じ種類の通知をこの間隔より詰めて出さない。</summary>
    public TimeSpan SameKindCooldown { get; init; } = TimeSpan.FromHours(2);

    /// <summary>1日に出す通知の上限。着用中止級と公式発表は数に入れない。</summary>
    public int DailyCap { get; init; } = 6;

    /// <summary>
    /// 回復とみなすために必要な、補正後WBGTの下がり幅（℃）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/notifications.md</c> は「帯の境界より0.5℃下」と書いているが、
    /// 帯の境界の値はAPIの側にあり、クライアントは持たない。
    /// 判定ロジックを複製しない方針のため、ここでは
    /// 「着用中止を知らせたときの補正後WBGTより0.5℃下がったか」で代える。
    /// </para>
    /// <para>
    /// 境界を持たずに「わずかな改善では戻さない」という意図を満たせる。
    /// </para>
    /// </remarks>
    public double RecoveryDeadbandCelsius { get; init; } = 0.5;

    /// <summary>回復とみなすために、活動できる状態が続いている必要のある時間数。</summary>
    public int RecoveryDwellHours { get; init; } = 2;

    /// <summary>抑制の判断のために覚えておく通知の履歴の上限。</summary>
    public int HistoryLimit { get; init; } = 64;
}
