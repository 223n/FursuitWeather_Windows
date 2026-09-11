namespace FursuitWeather.Core.Update;

/// <summary>前回のインストールを確定した結果。</summary>
public enum InstallOutcome
{
    /// <summary>確定するものが無かった。</summary>
    None,

    /// <summary>狙った版で起動できた。</summary>
    Succeeded,

    /// <summary>狙った版になっていない。途中で終わったとみなす。</summary>
    Failed,
}

/// <summary>
/// 更新の出来事を状態へ書き入れる。
/// </summary>
/// <remarks>
/// <para>
/// 純粋な関数として書く。時計もファイルも触らない。
/// 書き入れる規則を1か所に集め、UIの層が勝手に状態を組み替えないようにする。
/// </para>
/// <para>
/// <b>インストールの成否は、終了コードではなく次回の起動時の版で決める。</b>
/// アプリが先に終わるため、インストーラーの終了コードを読む主体がいない。
/// 電源断もスリープも強制終了も、この1つの仕組みで同じ経路に集まる。
/// 根拠は <c>docs/update.md</c> の「成否の判定は終了コードではありません」にある。
/// </para>
/// </remarks>
public static class UpdateLedger
{
    /// <summary>
    /// インストーラーを起動する直前に書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="targetVersion">入れる版。</param>
    /// <param name="expectedSha256">入れる配布物のSHA-256。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// <b>インストーラーの起動より前に保存すること。</b>
    /// 逆の順序だと、起動した直後に電源が落ちたときに中断を検知できない。
    /// </remarks>
    public static UpdateState BeginInstall(UpdateState state, string targetVersion, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        return state with
        {
            Stage = UpdateStage.Installing,
            TargetVersion = targetVersion,
            ExpectedSha256 = expectedSha256,
            Attempts = UpdateRetryPolicy.Rebase(state.Attempts, targetVersion, expectedSha256),
        };
    }

    /// <summary>
    /// 起動したときに、前回のインストールの成否を確定する。
    /// </summary>
    /// <param name="state">保存してあった状態。</param>
    /// <param name="running">いま動いている版。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>確定したあとの状態と、その結論。</returns>
    /// <remarks>
    /// <para>
    /// いま動いている版が狙った版以上なら成功とする。
    /// 利用者が手でさらに新しい版を入れた場合も、成功として扱ってよい。
    /// </para>
    /// <para>
    /// 下回っていれば、途中で終わったとみなす。
    /// <b>自動の扱いでも再実行はせず、確認を挟む。</b>
    /// 無人で壊れた状態へ上書きを繰り返すのが、最も危ない。
    /// </para>
    /// </remarks>
    public static (UpdateState State, InstallOutcome Outcome) Reconcile(
        UpdateState state,
        SemanticVersion running,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Stage != UpdateStage.Installing)
        {
            return (state, InstallOutcome.None);
        }

        // 狙った版が読めないなら、成否を決められない。
        // 失敗として数えると、壊れた記録のせいで自動の更新が止まる。
        // 何もしなかったことにして、状態だけ畳む
        if (!SemanticVersion.TryParse(state.TargetVersion, out var target))
        {
            return (ClearTarget(state) with { Stage = UpdateStage.Idle }, InstallOutcome.None);
        }

        if (running >= target)
        {
            // 成功は「続けて失敗した版」の連鎖を断つ。
            // 途中で1つ入れば、次の版の失敗を3つ目として数えない。
            // 自動の遮断も解く。案内どおりに手で入れ直して成功しても解けないと、
            // 以後のどの版も自動では取らず、入れもしない
            return (
                ClearTarget(state) with
                {
                    Stage = UpdateStage.Succeeded,
                    HasInterruptedInstall = false,
                    AutoUpdateDisabled = false,
                    Attempts = new UpdateAttempts(),
                    RecentFailedVersions = [],
                },
                InstallOutcome.Succeeded);
        }

        // 狙いと記録が食い違っていれば、ここで張り直してから数える。
        // BeginInstall が先に張り直すため通常は一致しているが、
        // 手で編集された状態や古い版の書いた状態では食い違いうる
        var rebased = UpdateRetryPolicy.Rebase(state.Attempts, state.TargetVersion, state.ExpectedSha256);
        var attempts = rebased with
        {
            InstallFailures = rebased.InstallFailures + 1,
            LastInstallFailureAt = now,
        };

        var failed = state.RecentFailedVersions.Append(state.TargetVersion).ToList();

        return (
            state with
            {
                Stage = UpdateStage.Failed,
                HasInterruptedInstall = true,
                Attempts = attempts,
                RecentFailedVersions = failed,
                AutoUpdateDisabled = state.AutoUpdateDisabled || UpdateRetryPolicy.ShouldDisableAuto(failed),
            },
            InstallOutcome.Failed);
    }

    /// <summary>
    /// いま動いている版が失敗した版をすべて追い越していれば、失敗の記録を解く。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="running">いま動いている版。</param>
    /// <returns>書き入れたあとの状態。解くものが無ければ同じもの。</returns>
    /// <remarks>
    /// <para>
    /// Releasesのページから手で入れた場合は <see cref="UpdateStage.Installing"/> を通らない。
    /// そのため <see cref="Reconcile"/> の成功の枝に来ず、自動の遮断が残り続ける。
    /// 失敗した版より新しい版で動いているなら、その失敗はもう意味を持たない。
    /// </para>
    /// <para>
    /// インストール中の状態には触れない。そちらの成否は <see cref="Reconcile"/> が決める。
    /// </para>
    /// </remarks>
    public static UpdateState ForgetOvertakenFailures(UpdateState state, SemanticVersion running)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Stage == UpdateStage.Installing || state.RecentFailedVersions.Count == 0)
        {
            return state;
        }

        // 失敗の記録に入るのは、読めた狙いだけである。読めないものは比べようがないので飛ばす
        foreach (var text in state.RecentFailedVersions)
        {
            if (SemanticVersion.TryParse(text, out var failed) && running < failed)
            {
                return state;
            }
        }

        return state with
        {
            RecentFailedVersions = [],
            AutoUpdateDisabled = false,
            HasInterruptedInstall = false,
        };
    }

    /// <summary>
    /// 利用者が中断の記録を確かめたことを書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// 確認を挟んだあとは、また自動の判断へ戻す。
    /// 記録をいつまでも残すと、自動の適用が永久に止まる。
    /// </remarks>
    public static UpdateState AcknowledgeInterruption(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { HasInterruptedInstall = false };
    }

    /// <summary>
    /// 確認を終えたことを書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="wallClock">いまの壁時計（UTC）。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="acceptedManifestAt">受け入れたマニフェストの生成時刻。受け入れていなければ null。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// <para>
    /// 確認の時刻は、成否に関わらず進める。
    /// 進めないと、失敗が続くあいだ30秒ごとに取りに行き続ける。
    /// </para>
    /// <para>
    /// マニフェストの生成時刻は、<b>署名を通して受け入れたときだけ</b>進める。
    /// 通らなかったものの時刻で進めると、偽のマニフェストで巻き戻しの検知を狂わせられる。
    /// </para>
    /// </remarks>
    public static UpdateState RecordCheck(
        UpdateState state,
        DateTimeOffset wallClock,
        TimeSpan monotonic,
        DateTimeOffset? acceptedManifestAt)
    {
        ArgumentNullException.ThrowIfNull(state);

        var next = state with
        {
            LastCheckedAt = wallClock,
            LastCheckedMonotonic = monotonic,
        };

        if (acceptedManifestAt is { } at &&
            (state.LastManifestGeneratedAt is not { } last || at > last))
        {
            next = next with { LastManifestGeneratedAt = at };
        }

        return next;
    }

    /// <summary>
    /// 検証を通った更新を見つけたことを書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="version">見つけた版。</param>
    /// <param name="sha256">その配布物のSHA-256。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// <para>
    /// <b>取得のゲートを見る前に通すこと。</b>
    /// 失敗の記録と割り込みの予算は、どちらも狙いの配布物に紐づく。
    /// 狙いが変わったのに張り直さないと、前の版で使い切った分が次の版まで止める。
    /// </para>
    /// <para>
    /// 割り込みの回数は戻すが、最後に割り込んだ時刻は残す。
    /// 同じ日に2回は割り込まないという約束は、版が変わっても守る。
    /// </para>
    /// </remarks>
    public static UpdateState RecordAvailable(UpdateState state, string version, string sha256)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        if (state.Attempts.Matches(version, sha256))
        {
            return state;
        }

        return state with
        {
            Attempts = new UpdateAttempts { Version = version, ExpectedSha256 = sha256 },
            PromptCount = 0,
        };
    }

    /// <summary>
    /// 取得を終え、検証も通ったことを書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// 取得の失敗の数を戻す。
    /// 取れたあとも数を残すと、時々の失敗が積もって上限に届く。
    /// </remarks>
    public static UpdateState RecordDownloaded(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state with
        {
            Stage = UpdateStage.Downloaded,
            Attempts = state.Attempts with { DownloadFailures = 0, LastDownloadFailureAt = null },
        };
    }

    /// <summary>
    /// 取得に失敗したことを書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="version">取ろうとした版。</param>
    /// <param name="sha256">取ろうとした配布物のSHA-256。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// 前の失敗から窓が明けていれば、数え直してから足す。
    /// </remarks>
    public static UpdateState RecordDownloadFailure(UpdateState state, string version, string sha256, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var attempts = UpdateRetryPolicy.ExpireDownloadFailures(
            UpdateRetryPolicy.Rebase(state.Attempts, version, sha256),
            now);
        return state with
        {
            Stage = UpdateStage.DownloadPaused,
            Attempts = attempts with
            {
                DownloadFailures = attempts.DownloadFailures + 1,
                LastDownloadFailureAt = now,
            },
        };
    }

    private static UpdateState ClearTarget(UpdateState state) => state with
    {
        TargetVersion = string.Empty,
        ExpectedSha256 = string.Empty,
    };
}
