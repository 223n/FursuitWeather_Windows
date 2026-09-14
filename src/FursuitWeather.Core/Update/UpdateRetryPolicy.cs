namespace FursuitWeather.Core.Update;

/// <summary>
/// 失敗したときに、次へ進んでよい時期を決める。
/// </summary>
/// <remarks>
/// <para>
/// 根拠は <c>docs/update.md</c> の「再試行の抑制」にある。
/// </para>
/// <para>
/// <b>抑制は自動の側にだけ効かせる。</b>
/// 手動のボタンは常に生かす。
/// </para>
/// <para>
/// 数える鍵は版とSHA-256の両方である。
/// 同じ版が作り直されたときに、古い抑制を引きずらない。
/// </para>
/// </remarks>
public static class UpdateRetryPolicy
{
    /// <summary>
    /// 取得に失敗したあと待つ時間。
    /// </summary>
    /// <remarks>最後の値で頭打ちにする。限りなく延ばすと更新が永久に届かない。</remarks>
    public static IReadOnlyList<TimeSpan> DownloadBackoff { get; } =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>1日に自動で取得を試す上限。</summary>
    public const int DownloadDailyCap = 5;

    /// <summary>
    /// 取得の失敗を数える窓。
    /// </summary>
    /// <remarks>
    /// 最後の失敗からこれだけ経てば、数え直す。
    /// 窓を持たずに累計で数えると、回線が悪かった数日のせいで自動の取得が二度と動かなくなる。
    /// </remarks>
    public static readonly TimeSpan DownloadWindow = TimeSpan.FromHours(24);

    /// <summary>1つの版について、自動でインストールを試す上限。</summary>
    public const int InstallCap = 3;

    /// <summary>インストールを試し直すまでの最小の間隔。</summary>
    public static readonly TimeSpan InstallInterval = TimeSpan.FromHours(6);

    /// <summary>この数だけ異なる版で続けて失敗したら、自動を止める。</summary>
    public const int DistinctFailedVersionsCutoff = 3;

    /// <summary>
    /// 次に取得を試してよい時刻。
    /// </summary>
    /// <param name="attempts">失敗の記録。</param>
    /// <returns>待つ時刻。待たなくてよければ null。</returns>
    public static DateTimeOffset? NextDownloadAt(UpdateAttempts attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        if (attempts.DownloadFailures <= 0 || attempts.LastDownloadFailureAt is not { } last)
        {
            return null;
        }

        var index = Math.Min(attempts.DownloadFailures - 1, DownloadBackoff.Count - 1);
        return last + DownloadBackoff[index];
    }

    /// <summary>1日の上限まで取得を試したか。</summary>
    /// <param name="attempts">失敗の記録。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>上限に達していれば true。</returns>
    /// <remarks>
    /// <para>
    /// 上限が効くのは、最後の失敗から <see cref="DownloadWindow"/> のあいだだけである。
    /// 窓が明けたら、また自動で試してよい。
    /// </para>
    /// <para>
    /// 失敗の時刻が無い記録は、上限に達していないとみなす。
    /// 窓を決められないものを止める側へ倒すと、二度と解けない。
    /// </para>
    /// </remarks>
    public static bool IsDownloadExhausted(UpdateAttempts attempts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        return attempts.DownloadFailures >= DownloadDailyCap &&
            attempts.LastDownloadFailureAt is { } last &&
            now - last < DownloadWindow;
    }

    /// <summary>
    /// 窓が明けていれば、取得の失敗を数え直した記録を返す。
    /// </summary>
    /// <param name="attempts">いまの記録。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>使ってよい記録。</returns>
    /// <remarks>
    /// 失敗を書き入れる前に通す。
    /// 通さないと、窓が明けたあとの1回の失敗でまた上限に届き、1日に1回しか試せなくなる。
    /// </remarks>
    public static UpdateAttempts ExpireDownloadFailures(UpdateAttempts attempts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        return attempts.LastDownloadFailureAt is { } last && now - last >= DownloadWindow
            ? attempts with { DownloadFailures = 0, LastDownloadFailureAt = null }
            : attempts;
    }

    /// <summary>
    /// 次にインストールを試してよい時刻。
    /// </summary>
    /// <param name="attempts">失敗の記録。</param>
    /// <returns>待つ時刻。待たなくてよければ null。</returns>
    /// <remarks>間隔は6時間以上あける。同じセッションで2回試さない。</remarks>
    public static DateTimeOffset? NextInstallAt(UpdateAttempts attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        if (attempts.InstallFailures <= 0 || attempts.LastInstallFailureAt is not { } last)
        {
            return null;
        }

        return last + InstallInterval;
    }

    /// <summary>この版でインストールを試す回数を使い切ったか。</summary>
    /// <param name="attempts">失敗の記録。</param>
    /// <returns>使い切っていれば true。</returns>
    public static bool IsInstallExhausted(UpdateAttempts attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        return attempts.InstallFailures >= InstallCap;
    }

    /// <summary>
    /// 自動での更新そのものを止めるべきか。
    /// </summary>
    /// <param name="recentFailedVersions">続けて失敗した版。</param>
    /// <returns>止めるべきなら true。</returns>
    /// <remarks>
    /// 数えるのは異なる版の数である。
    /// 同じ版で3回失敗しただけなら、次の版で直るかもしれない。
    /// </remarks>
    public static bool ShouldDisableAuto(IReadOnlyList<string> recentFailedVersions)
    {
        ArgumentNullException.ThrowIfNull(recentFailedVersions);

        return recentFailedVersions
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .Count() >= DistinctFailedVersionsCutoff;
    }

    /// <summary>
    /// 狙いが変わったときに、失敗の記録を張り直す。
    /// </summary>
    /// <param name="attempts">いまの記録。</param>
    /// <param name="version">新しく狙う版。</param>
    /// <param name="sha256">その配布物のSHA-256。</param>
    /// <returns>使ってよい記録。</returns>
    /// <remarks>
    /// 版かSHA-256のどちらかが違えば、数え直す。
    /// 作り直された配布物に古い抑制を当てると、直っているのに試さない。
    /// </remarks>
    public static UpdateAttempts Rebase(UpdateAttempts attempts, string version, string sha256)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        return attempts.Matches(version, sha256)
            ? attempts
            : new UpdateAttempts { Version = version, ExpectedSha256 = sha256 };
    }
}
