namespace FursuitWeather.Core.Update;

/// <summary>
/// いま狙っている版について、失敗を数えた記録。
/// </summary>
/// <remarks>
/// <b>版だけでなくSHA-256も鍵に含める。</b>
/// 同じ版が作り直されたときに、古い抑制を引きずらないためである。
/// </remarks>
public sealed record UpdateAttempts
{
    /// <summary>狙っている版。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>その版の配布物のSHA-256。</summary>
    public string ExpectedSha256 { get; init; } = string.Empty;

    /// <summary>取得に失敗した回数。</summary>
    public int DownloadFailures { get; init; }

    /// <summary>最後に取得へ失敗した時刻。</summary>
    public DateTimeOffset? LastDownloadFailureAt { get; init; }

    /// <summary>インストールに失敗した回数。</summary>
    public int InstallFailures { get; init; }

    /// <summary>最後にインストールへ失敗した時刻。</summary>
    public DateTimeOffset? LastInstallFailureAt { get; init; }

    /// <summary>この記録が、指定の配布物のものか。</summary>
    /// <param name="version">版。</param>
    /// <param name="sha256">配布物のSHA-256。</param>
    /// <returns>同じものなら true。</returns>
    public bool Matches(string version, string sha256) =>
        string.Equals(Version, version, StringComparison.Ordinal) &&
        string.Equals(ExpectedSha256, sha256, StringComparison.Ordinal);
}

/// <summary>
/// 更新まわりで端末に保存しておく状態。
/// </summary>
/// <remarks>
/// 純粋な値として持ち、読み書きはUIの層が行う。
/// </remarks>
public sealed record UpdateState
{
    /// <summary>更新の扱い方。</summary>
    public UpdateMode Mode { get; init; } = UpdateMode.DownloadOnly;

    /// <summary>
    /// 利用者が自分で選んだか。
    /// </summary>
    /// <remarks>
    /// 既定値を将来変えるときに要る。
    /// <b>利用者が明示的に選んだ設定は上書きしない。</b>
    /// 一度も触っていない利用者にだけ新しい既定を当てる。
    /// </remarks>
    public bool ModeChosenByUser { get; init; }

    /// <summary>従量制課金の接続では自動で取得しない。</summary>
    public bool PauseOnMetered { get; init; } = true;

    /// <summary>電池で動いているあいだは自動で取得しない。</summary>
    public bool PauseOnBattery { get; init; } = true;

    /// <summary>
    /// 自動での更新そのものを止めているか。
    /// </summary>
    /// <remarks>
    /// 異なる3つの版で続けて失敗したときに立てる。
    /// 手動のボタンは生かしたまま、自動の側だけを遮断する。
    /// </remarks>
    public bool AutoUpdateDisabled { get; init; }

    /// <summary>いまの進み具合。</summary>
    public UpdateStage Stage { get; init; } = UpdateStage.Idle;

    /// <summary>最後に確認した壁時計の時刻。</summary>
    /// <remarks>常時表示する。静かに失敗する設計では、これが「壊れていない」ことの唯一の証拠になる。</remarks>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>最後に確認した単調時刻。</summary>
    /// <remarks>
    /// 壁時計だけだと利用者の時刻の変更で暴発し、単調時刻だけだと再起動でリセットされる。
    /// 両方を見て、どちらかが閾値を超えたら動かす。
    /// </remarks>
    public TimeSpan? LastCheckedMonotonic { get; init; }

    /// <summary>最後に受け入れたマニフェストの生成時刻。巻き戻しの検知に使う。</summary>
    public DateTimeOffset? LastManifestGeneratedAt { get; init; }

    /// <summary>狙っている版。</summary>
    public string TargetVersion { get; init; } = string.Empty;

    /// <summary>狙っている配布物のSHA-256。</summary>
    public string ExpectedSha256 { get; init; } = string.Empty;

    /// <summary>いまの狙いについての失敗の記録。</summary>
    public UpdateAttempts Attempts { get; init; } = new();

    /// <summary>続けて失敗した版。異なる3つで自動を止める。</summary>
    public IReadOnlyList<string> RecentFailedVersions { get; init; } = [];

    /// <summary>最後に利用者へ促した時刻。</summary>
    public DateTimeOffset? LastPromptAt { get; init; }

    /// <summary>促した通算の回数。</summary>
    public int PromptCount { get; init; }

    /// <summary>
    /// 前回のインストールが途中で終わったか。
    /// </summary>
    /// <remarks>
    /// 見つけたときは、自動の扱いでも再実行せずに確認を挟む。
    /// 無人で壊れた状態へ上書きを繰り返すのが、最も危ない。
    /// </remarks>
    public bool HasInterruptedInstall { get; init; }
}
