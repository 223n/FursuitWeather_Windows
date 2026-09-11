using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>更新の出来事を状態へ書き入れる規則を見る。</summary>
public sealed class UpdateLedgerTests
{
    private const string Sha = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";
    private const string OtherSha = "60e4c1df2a6d10783be26203062cc612dd0a4f879fe80ef1f2d8b3d41f9a9514";

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(9));

    private static SemanticVersion V(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }

    private static UpdateState Installing(string target = "0.4.0", string sha = Sha) =>
        UpdateLedger.BeginInstall(new UpdateState(), target, sha);

    // ---- インストールの前

    [Fact]
    public void 起動の前に狙いを書き入れる()
    {
        var state = Installing();

        Assert.Equal(UpdateStage.Installing, state.Stage);
        Assert.Equal("0.4.0", state.TargetVersion);
        Assert.Equal(Sha, state.ExpectedSha256);
    }

    // ---- 起動したときの確定

    [Fact]
    public void インストール中でなければ何もしない()
    {
        var (state, outcome) = UpdateLedger.Reconcile(new UpdateState(), V("0.3.0"), Now);

        Assert.Equal(InstallOutcome.None, outcome);
        Assert.Equal(UpdateStage.Idle, state.Stage);
    }

    [Fact]
    public void 狙った版で起動できれば成功とする()
    {
        var (state, outcome) = UpdateLedger.Reconcile(Installing(), V("0.4.0"), Now);

        Assert.Equal(InstallOutcome.Succeeded, outcome);
        Assert.Equal(UpdateStage.Succeeded, state.Stage);
        Assert.Equal(string.Empty, state.TargetVersion);
    }

    [Fact]
    public void 狙いより新しい版なら成功とする()
    {
        // 利用者が手でさらに新しい版を入れた場合
        var (_, outcome) = UpdateLedger.Reconcile(Installing(), V("0.5.0"), Now);

        Assert.Equal(InstallOutcome.Succeeded, outcome);
    }

    [Fact]
    public void 狙った版になっていなければ失敗とする()
    {
        // 電源断、スリープ、強制終了、インストーラーの失敗がここへ集まる
        var (state, outcome) = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Equal(UpdateStage.Failed, state.Stage);
    }

    [Fact]
    public void 失敗したら次は確認を挟ませる()
    {
        // 無人で壊れた状態へ上書きを繰り返すのが最も危ない
        var (state, _) = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now);

        Assert.True(state.HasInterruptedInstall);
        Assert.Equal(UpdateHoldReason.InterruptedInstall, UpdateGate.ForInstall(
            state with { Mode = UpdateMode.Automatic },
            new UpdateConditions { Uptime = TimeSpan.FromHours(1) },
            Now).Reason);
    }

    [Fact]
    public void 失敗の回数と時刻を数える()
    {
        var (state, _) = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now);

        Assert.Equal(1, state.Attempts.InstallFailures);
        Assert.Equal(Now, state.Attempts.LastInstallFailureAt);
        Assert.Equal(["0.4.0"], state.RecentFailedVersions);
    }

    [Fact]
    public void 同じ配布物で続けて失敗すれば数を足す()
    {
        var first = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now).State;
        var retried = UpdateLedger.BeginInstall(UpdateLedger.AcknowledgeInterruption(first), "0.4.0", Sha);

        var (state, _) = UpdateLedger.Reconcile(retried, V("0.3.0"), Now.AddHours(7));

        Assert.Equal(2, state.Attempts.InstallFailures);
    }

    [Fact]
    public void 配布物が作り直されたら数え直す()
    {
        // 直っているのに古い抑制で試さない、を作らない
        var first = UpdateLedger.Reconcile(Installing(sha: Sha), V("0.3.0"), Now).State;
        var rebuilt = UpdateLedger.BeginInstall(first, "0.4.0", OtherSha);

        var (state, _) = UpdateLedger.Reconcile(rebuilt, V("0.3.0"), Now.AddHours(7));

        Assert.Equal(1, state.Attempts.InstallFailures);
    }

    [Fact]
    public void 記録が狙いと食い違っていれば数え直す()
    {
        // BeginInstall を通さずに書かれた状態。手で編集されたときや、古い版が書いたときに起こる。
        // 別の配布物の失敗回数を引き継ぐと、直っているのに自動では試さなくなる
        var legacy = new UpdateState
        {
            Stage = UpdateStage.Installing,
            TargetVersion = "0.4.0",
            ExpectedSha256 = Sha,
            Attempts = new UpdateAttempts { Version = "0.4.0", ExpectedSha256 = OtherSha, InstallFailures = 2 },
        };

        var (state, _) = UpdateLedger.Reconcile(legacy, V("0.3.0"), Now);

        Assert.Equal(1, state.Attempts.InstallFailures);
        Assert.Equal(Sha, state.Attempts.ExpectedSha256);
    }

    [Fact]
    public void 異なる3つの版で失敗したら自動を止める()
    {
        var state = new UpdateState();
        foreach (var version in (string[])["0.4.0", "0.5.0", "0.6.0"])
        {
            state = UpdateLedger.Reconcile(
                UpdateLedger.BeginInstall(state, version, Sha), V("0.3.0"), Now).State;
        }

        Assert.True(state.AutoUpdateDisabled);
    }

    [Fact]
    public void 成功すれば失敗の連鎖を断つ()
    {
        // 途中で1つ入れば、次の版の失敗を3つ目として数えない
        var state = new UpdateState();
        state = UpdateLedger.Reconcile(UpdateLedger.BeginInstall(state, "0.4.0", Sha), V("0.3.0"), Now).State;
        state = UpdateLedger.Reconcile(UpdateLedger.BeginInstall(state, "0.5.0", Sha), V("0.3.0"), Now).State;
        state = UpdateLedger.Reconcile(UpdateLedger.BeginInstall(state, "0.5.0", Sha), V("0.5.0"), Now).State;

        Assert.Empty(state.RecentFailedVersions);
        Assert.False(state.HasInterruptedInstall);
        Assert.Equal(0, state.Attempts.InstallFailures);

        state = UpdateLedger.Reconcile(UpdateLedger.BeginInstall(state, "0.6.0", Sha), V("0.5.0"), Now).State;
        Assert.False(state.AutoUpdateDisabled);
    }

    [Fact]
    public void 狙った版が読めなければ失敗として数えない()
    {
        // 壊れた記録のせいで自動の更新を止めない
        var broken = new UpdateState { Stage = UpdateStage.Installing, TargetVersion = "こわれた値" };

        var (state, outcome) = UpdateLedger.Reconcile(broken, V("0.3.0"), Now);

        Assert.Equal(InstallOutcome.None, outcome);
        Assert.Equal(UpdateStage.Idle, state.Stage);
        Assert.Empty(state.RecentFailedVersions);
    }

    [Fact]
    public void 確かめたあとは自動の判断へ戻す()
    {
        var failed = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now).State;

        var acknowledged = UpdateLedger.AcknowledgeInterruption(failed);

        Assert.False(acknowledged.HasInterruptedInstall);
    }

    // ---- 確認

    [Fact]
    public void 確認の時刻は成否に関わらず進める()
    {
        // 進めないと、失敗が続くあいだ30秒ごとに取りに行き続ける
        var state = UpdateLedger.RecordCheck(new UpdateState(), Now, TimeSpan.FromHours(3), acceptedManifestAt: null);

        Assert.Equal(Now, state.LastCheckedAt);
        Assert.Equal(TimeSpan.FromHours(3), state.LastCheckedMonotonic);
        Assert.Null(state.LastManifestGeneratedAt);
    }

    [Fact]
    public void 受け入れたマニフェストの時刻だけを覚える()
    {
        var at = Now.AddHours(-1);

        var state = UpdateLedger.RecordCheck(new UpdateState(), Now, TimeSpan.Zero, at);

        Assert.Equal(at, state.LastManifestGeneratedAt);
    }

    [Fact]
    public void 古いマニフェストの時刻で巻き戻さない()
    {
        var newer = Now.AddHours(-1);
        var older = Now.AddHours(-5);
        var state = new UpdateState { LastManifestGeneratedAt = newer };

        state = UpdateLedger.RecordCheck(state, Now, TimeSpan.Zero, older);

        Assert.Equal(newer, state.LastManifestGeneratedAt);
    }

    // ---- 取得の失敗

    [Fact]
    public void 取得の失敗を数える()
    {
        var state = UpdateLedger.RecordDownloadFailure(new UpdateState(), "0.4.0", Sha, Now);
        state = UpdateLedger.RecordDownloadFailure(state, "0.4.0", Sha, Now.AddMinutes(2));

        Assert.Equal(2, state.Attempts.DownloadFailures);
        Assert.Equal(Now.AddMinutes(2), state.Attempts.LastDownloadFailureAt);
        Assert.Equal(UpdateStage.DownloadPaused, state.Stage);
    }

    [Fact]
    public void 狙いが変われば取得の失敗も数え直す()
    {
        var state = UpdateLedger.RecordDownloadFailure(new UpdateState(), "0.4.0", Sha, Now);

        state = UpdateLedger.RecordDownloadFailure(state, "0.5.0", Sha, Now);

        Assert.Equal(1, state.Attempts.DownloadFailures);
        Assert.Equal("0.5.0", state.Attempts.Version);
    }

    [Fact]
    public void 窓が明けたあとの取得の失敗は1から数える()
    {
        var state = new UpdateState();
        for (var i = 0; i < 5; i++)
        {
            state = UpdateLedger.RecordDownloadFailure(state, "0.4.0", Sha, Now.AddMinutes(i));
        }

        state = UpdateLedger.RecordDownloadFailure(state, "0.4.0", Sha, Now.AddMinutes(4).AddHours(24));

        Assert.Equal(1, state.Attempts.DownloadFailures);
    }

    // ---- 取得を終えたとき

    [Fact]
    public void 取得を終えたら取得の失敗を戻す()
    {
        // 残すと、時々の失敗が積もって上限に届く
        var state = UpdateLedger.RecordDownloadFailure(new UpdateState(), "0.4.0", Sha, Now);
        state = UpdateLedger.RecordDownloadFailure(state, "0.4.0", Sha, Now.AddMinutes(2));

        state = UpdateLedger.RecordDownloaded(state);

        Assert.Equal(UpdateStage.Downloaded, state.Stage);
        Assert.Equal(0, state.Attempts.DownloadFailures);
        Assert.Null(state.Attempts.LastDownloadFailureAt);
        Assert.Equal("0.4.0", state.Attempts.Version);
    }

    [Fact]
    public void 取得を終えてもインストールの失敗は残す()
    {
        var (failed, _) = UpdateLedger.Reconcile(Installing(), V("0.3.0"), Now);

        var state = UpdateLedger.RecordDownloaded(failed);

        Assert.Equal(1, state.Attempts.InstallFailures);
        Assert.True(state.HasInterruptedInstall);
    }

    // ---- 更新を見つけたとき

    [Fact]
    public void 新しい狙いを見つけたら失敗の記録を張り直す()
    {
        var (failed, _) = UpdateLedger.Reconcile(Installing("0.4.0"), V("0.3.0"), Now);
        failed = UpdateLedger.RecordDownloadFailure(failed, "0.4.0", Sha, Now);

        var state = UpdateLedger.RecordAvailable(failed, "0.5.0", OtherSha);

        Assert.Equal("0.5.0", state.Attempts.Version);
        Assert.Equal(OtherSha, state.Attempts.ExpectedSha256);
        Assert.Equal(0, state.Attempts.DownloadFailures);
        Assert.Equal(0, state.Attempts.InstallFailures);

        // 異なる版の失敗の連鎖は、狙いを張り直しても断たない。断つのは成功だけである
        Assert.Equal(["0.4.0"], state.RecentFailedVersions);
    }

    [Fact]
    public void 同じ狙いなら記録をそのまま残す()
    {
        var state = UpdateLedger.RecordDownloadFailure(new UpdateState(), "0.4.0", Sha, Now) with
        {
            PromptCount = 3,
            LastPromptAt = Now,
        };

        Assert.Same(state, UpdateLedger.RecordAvailable(state, "0.4.0", Sha));
    }

    [Fact]
    public void 狙いが変わったら割り込みの回数を戻し時刻は残す()
    {
        // 時刻まで消すと、版が出た日に2回割り込みうる
        var state = UpdateLedger.RecordAvailable(new UpdateState(), "0.4.0", Sha) with
        {
            PromptCount = 5,
            LastPromptAt = Now,
        };

        state = UpdateLedger.RecordAvailable(state, "0.5.0", Sha);

        Assert.Equal(0, state.PromptCount);
        Assert.Equal(Now, state.LastPromptAt);
    }
}
