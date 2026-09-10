using System.Text.Json.Serialization;

namespace FursuitWeather.Core.Update;

/// <summary>更新の重さ。</summary>
public enum UpdateSeverity
{
    /// <summary>APIが知らない値を返したとき。</summary>
    Unknown = 0,

    /// <summary>通常の更新。</summary>
    Normal,

    /// <summary>セキュリティの修正。文言と再提示の間隔だけ変える。</summary>
    Security,
}

/// <summary>配布物1つ。</summary>
public sealed record UpdatePackage
{
    /// <summary>対象のアーキテクチャ。<c>x64</c> など。</summary>
    public string Arch { get; init; } = string.Empty;

    /// <summary>
    /// 取得先。
    /// </summary>
    /// <remarks>
    /// 版を含む恒久のURLにする。
    /// GitHub Releasesの署名付きURLは約1時間で失効するため、焼き込めない。
    /// </remarks>
    public string Url { get; init; } = string.Empty;

    /// <summary>大きさ（バイト）。</summary>
    public long Size { get; init; }

    /// <summary>SHA-256。小文字の16進で64文字。</summary>
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>動作の必要条件。</summary>
public sealed record UpdateRequirements
{
    /// <summary>必要なWindowsのビルド番号。22621がWindows 11 22H2。</summary>
    public int MinimumOsBuild { get; init; }
}

/// <summary>1つのチャンネルの内容。</summary>
public sealed record UpdateChannel
{
    /// <summary>版。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>公開日。</summary>
    public string ReleasedAt { get; init; } = string.Empty;

    /// <summary>リリースノートの場所。</summary>
    public string NotesUrl { get; init; } = string.Empty;

    /// <summary>重さ。APIが返す文字列のまま保持する。</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>
    /// この版より古ければ必須。
    /// </summary>
    /// <remarks>
    /// 真偽値の <c>mandatory</c> ではなくこちらにする。
    /// 「この版より古い場合だけ必須」を1つの項目で表せ、矛盾が生じない。
    /// </remarks>
    public string? MandatoryBelow { get; init; }

    /// <summary>この版より古ければ使用を止める。緊急の停止に使う。</summary>
    public string? UnsupportedBelow { get; init; }

    /// <summary>必要条件。</summary>
    public UpdateRequirements Requirements { get; init; } = new();

    /// <summary>配布物。</summary>
    public IReadOnlyList<UpdatePackage> Packages { get; init; } = [];

    /// <summary>重さを列挙へ写す。知らない値は <see cref="UpdateSeverity.Unknown"/> になる。</summary>
    [JsonIgnore]
    public UpdateSeverity SeverityId => Severity switch
    {
        "normal" => UpdateSeverity.Normal,
        "security" => UpdateSeverity.Security,
        _ => UpdateSeverity.Unknown,
    };
}

/// <summary>
/// 更新の在りかを示すマニフェスト。
/// </summary>
/// <remarks>
/// <para>
/// 置き場所とスキーマの根拠は <c>docs/update.md</c> にある。
/// </para>
/// <para>
/// <b>この型へ写す前に署名を検証すること。</b>
/// 中身で分岐してから検証する順序にしてはいけない。
/// </para>
/// </remarks>
public sealed record UpdateManifest
{
    /// <summary>
    /// スキーマの版。
    /// </summary>
    /// <remarks>
    /// 知らない値なら解釈をやめ、リリースのページへの案内だけを出す。
    /// 将来スキーマを作り直せる唯一の担保である。
    /// </remarks>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// マニフェストを作った時刻。
    /// </summary>
    /// <remarks>
    /// 前回の成功時より古ければ無視する。巻き戻しの検知に使う。
    /// </remarks>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>署名に使った鍵の識別子。鍵の入れ替えの余地。</summary>
    public string KeyId { get; init; } = string.Empty;

    /// <summary>チャンネルごとの内容。<c>stable</c> を使う。</summary>
    public IReadOnlyDictionary<string, UpdateChannel> Channels { get; init; } =
        new Dictionary<string, UpdateChannel>(StringComparer.Ordinal);
}
