using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>取得と適用の2つのゲートを見る。</summary>
public sealed class UpdateGateTests
{
    private const string ShaA = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";
    private const string ShaB = "60e4c1df2a6d10783be26203062cc612dd0a4f879fe80ef1f2d8b3d41f9a9514";

    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(9));

    private static UpdateState State(
        UpdateMode mode = UpdateMode.Automatic,
        bool pauseOnMetered = true,
        bool pauseOnBattery = true,
        bool autoDisabled = false,
        bool interrupted = false,
        UpdateAttempts? attempts = null) => new()
        {
            Mode = mode,
            PauseOnMetered = pauseOnMetered,
            PauseOnBattery = pauseOnBattery,
            AutoUpdateDisabled = autoDisabled,
            HasInterruptedInstall = interrupted,
            Attempts = attempts ?? new UpdateAttempts(),
        };

    private static UpdateConditions Conditions(
        bool metered = false,
        bool battery = false,
        bool acceptsNotifications = true,
        int uptimeMinutes = 30) => new()
        {
            IsMetered = metered,
            IsOnBattery = battery,
            AcceptsNotifications = acceptsNotifications,
            Uptime = TimeSpan.FromMinutes(uptimeMinutes),
        };

    // ---- 取得のゲート

    [Fact]
    public void 自動なら取得へ進む()
    {
        var decision = UpdateGate.ForDownload(State(), Conditions(), Now);

        Assert.True(decision.CanProceed);
        Assert.Equal(UpdateHoldReason.None, decision.Reason);
    }

    [Fact]
    public void 取得だけ自動でも取得へ進む()
    {
        var decision = UpdateGate.ForDownload(State(UpdateMode.DownloadOnly), Conditions(), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void お知らせのみは取得で利用者を待つ()
    {
        var decision = UpdateGate.ForDownload(State(UpdateMode.NotifyOnly), Conditions(), Now);

        Assert.Equal(GateOutcome.WaitForUser, decision.Outcome);
    }

    [Fact]
    public void 従量制課金では取得を見送る()
    {
        var decision = UpdateGate.ForDownload(State(), Conditions(metered: true), Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
        Assert.Equal(UpdateHoldReason.Metered, decision.Reason);
    }

    [Fact]
    public void 従量制課金でも設定で切っていれば取得する()
    {
        var decision = UpdateGate.ForDownload(State(pauseOnMetered: false), Conditions(metered: true), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 電池で動いていれば取得を見送る()
    {
        var decision = UpdateGate.ForDownload(State(), Conditions(battery: true), Now);

        Assert.Equal(UpdateHoldReason.OnBattery, decision.Reason);
    }

    [Fact]
    public void 全画面でも取得は妨げない()
    {
        // 裏で受け取るだけで割り込まない。止めるのは適用のほうである
        var decision = UpdateGate.ForDownload(State(), Conditions(acceptsNotifications: false), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 起動直後でも取得は妨げない()
    {
        var decision = UpdateGate.ForDownload(State(), Conditions(uptimeMinutes: 1), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 取得の失敗のあとは待つ()
    {
        var attempts = new UpdateAttempts
        {
            DownloadFailures = 1,
            LastDownloadFailureAt = Now.AddSeconds(-30),
        };

        var decision = UpdateGate.ForDownload(State(attempts: attempts), Conditions(), Now);

        Assert.Equal(UpdateHoldReason.RetryBackoff, decision.Reason);
        Assert.Equal(Now.AddSeconds(-30).AddMinutes(1), decision.RetryAt);
    }

    [Fact]
    public void 待ち時間が過ぎれば取得へ進む()
    {
        var attempts = new UpdateAttempts
        {
            DownloadFailures = 1,
            LastDownloadFailureAt = Now.AddMinutes(-2),
        };

        var decision = UpdateGate.ForDownload(State(attempts: attempts), Conditions(), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 取得の上限に達したら窓が明けるまで自動では試さない()
    {
        // 6時間の待ちは過ぎているが、24時間の窓の中にいる
        var last = Now.AddHours(-7);
        var attempts = new UpdateAttempts
        {
            DownloadFailures = UpdateRetryPolicy.DownloadDailyCap,
            LastDownloadFailureAt = last,
        };

        var decision = UpdateGate.ForDownload(State(attempts: attempts), Conditions(), Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
        Assert.Equal(UpdateHoldReason.RetryBackoff, decision.Reason);

        // 時間で解ける見送りは、解ける時刻を返す。
        // null を返すと、呼び出し側が「解けない」と読んで試し直さない
        Assert.Equal(last.AddHours(24), decision.RetryAt);
    }

    [Fact]
    public void 取得の上限は窓が明ければ解ける()
    {
        var attempts = new UpdateAttempts
        {
            DownloadFailures = UpdateRetryPolicy.DownloadDailyCap,
            LastDownloadFailureAt = Now.AddHours(-24),
        };

        var decision = UpdateGate.ForDownload(State(attempts: attempts), Conditions(), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 前の版で上限に達しても次の版では試す()
    {
        // 失敗の記録は狙いの配布物に紐づく。
        // 張り直さずに見ると、前の版の失敗で以後のどの版も自動で取らなくなる
        var state = State(
            mode: UpdateMode.DownloadOnly,
            attempts: new UpdateAttempts
            {
                Version = "0.3.1",
                ExpectedSha256 = ShaA,
                DownloadFailures = UpdateRetryPolicy.DownloadDailyCap,
                LastDownloadFailureAt = Now.AddHours(-7),
            });
        Assert.Equal(GateOutcome.Hold, UpdateGate.ForDownload(state, Conditions(), Now).Outcome);

        state = UpdateLedger.RecordAvailable(state, "0.3.2", ShaB);

        Assert.True(UpdateGate.ForDownload(state, Conditions(), Now).CanProceed);
    }

    [Fact]
    public void 作り直された配布物では試す()
    {
        // 壊れた配布物を同じ版で出し直したときに、古い抑制を引きずらない
        var state = State(
            mode: UpdateMode.DownloadOnly,
            attempts: new UpdateAttempts
            {
                Version = "0.3.1",
                ExpectedSha256 = ShaA,
                DownloadFailures = UpdateRetryPolicy.DownloadDailyCap,
                LastDownloadFailureAt = Now.AddHours(-7),
            });

        state = UpdateLedger.RecordAvailable(state, "0.3.1", ShaB);

        Assert.True(UpdateGate.ForDownload(state, Conditions(), Now).CanProceed);
    }

    [Fact]
    public void 毎日1回の失敗が続いても自動の取得は止まらない()
    {
        // 確認は1日に1回である。累計で5回数えると、6日目から永久に止まっていた
        var state = State(mode: UpdateMode.DownloadOnly);
        state = UpdateLedger.RecordAvailable(state, "0.3.1", ShaA);

        for (var day = 0; day < 10; day++)
        {
            var at = Now.AddDays(day);
            Assert.True(UpdateGate.ForDownload(state, Conditions(), at).CanProceed, $"{day}日目");

            state = UpdateLedger.RecordDownloadFailure(state, "0.3.1", ShaA, at);
        }
    }

    [Fact]
    public void 続けて失敗したら待ちを伸ばし窓が明ければまた試す()
    {
        var state = State(mode: UpdateMode.DownloadOnly);
        state = UpdateLedger.RecordAvailable(state, "0.3.1", ShaA);

        // 1分、5分、15分、1時間の待ちを挟んで5回失敗する
        var at = Now;
        foreach (var wait in new[] { 0, 1, 5, 15, 60 })
        {
            at = at.AddMinutes(wait);
            Assert.True(UpdateGate.ForDownload(state, Conditions(), at).CanProceed);
            state = UpdateLedger.RecordDownloadFailure(state, "0.3.1", ShaA, at);
        }

        var held = UpdateGate.ForDownload(state, Conditions(), at.AddHours(6));
        Assert.Equal(GateOutcome.Hold, held.Outcome);
        Assert.Equal(at.AddHours(24), held.RetryAt);

        Assert.True(UpdateGate.ForDownload(state, Conditions(), at.AddHours(24)).CanProceed);

        // 窓が明けたあとの失敗は1回目として数える。1日に1回しか試せなくならない
        state = UpdateLedger.RecordDownloadFailure(state, "0.3.1", ShaA, at.AddHours(24));
        Assert.Equal(1, state.Attempts.DownloadFailures);
    }

    // ---- 適用のゲート

    [Fact]
    public void 自動ならインストールへ進む()
    {
        var decision = UpdateGate.ForInstall(State(), Conditions(), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 掲示のあいだは自動でインストールしない()
    {
        // 全画面の掲示をWindowsがどう見なすかに任せず、アプリが知っている状態で止める
        var decision = UpdateGate.ForInstall(State(), Conditions() with { DisplayActive = true }, Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
        Assert.Equal(UpdateHoldReason.DisplayActive, decision.Reason);
        Assert.Contains("掲示", UpdateGate.Describe(decision.Reason), StringComparison.Ordinal);
    }

    [Fact]
    public void 掲示のあいだでも取得は止めない()
    {
        var decision = UpdateGate.ForDownload(State(), Conditions() with { DisplayActive = true }, Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 前回の中断は掲示より先に見る()
    {
        var decision = UpdateGate.ForInstall(State(interrupted: true), Conditions() with { DisplayActive = true }, Now);

        Assert.Equal(UpdateHoldReason.InterruptedInstall, decision.Reason);
    }

    [Fact]
    public void 取得だけ自動なら掲示のあいだも押されるのを待つ()
    {
        var decision = UpdateGate.ForInstall(
            State(mode: UpdateMode.DownloadOnly),
            Conditions() with { DisplayActive = true },
            Now);

        Assert.Equal(GateOutcome.WaitForUser, decision.Outcome);
    }

    [Fact]
    public void 取得だけ自動ならインストールで利用者を待つ()
    {
        var decision = UpdateGate.ForInstall(State(UpdateMode.DownloadOnly), Conditions(), Now);

        Assert.Equal(GateOutcome.WaitForUser, decision.Outcome);
    }

    [Fact]
    public void 割り込めない状態ならインストールを見送る()
    {
        // 全画面、プレゼンテーション、ロック、応答不可の時間帯など
        var decision = UpdateGate.ForInstall(State(), Conditions(acceptsNotifications: false), Now);

        Assert.Equal(UpdateHoldReason.DoNotDisturb, decision.Reason);
    }

    [Fact]
    public void 起動から10分未満ならインストールを見送る()
    {
        var decision = UpdateGate.ForInstall(State(), Conditions(uptimeMinutes: 9), Now);

        Assert.Equal(UpdateHoldReason.JustStarted, decision.Reason);
    }

    [Fact]
    public void 起動から10分を過ぎればインストールへ進む()
    {
        var decision = UpdateGate.ForInstall(State(), Conditions(uptimeMinutes: 10), Now);

        Assert.True(decision.CanProceed);
    }

    [Fact]
    public void 前回が途中で終わっていれば自動では再実行しない()
    {
        // 無人で壊れた状態へ上書きを繰り返すのが最も危ない
        var decision = UpdateGate.ForInstall(State(interrupted: true), Conditions(), Now);

        Assert.Equal(UpdateHoldReason.InterruptedInstall, decision.Reason);
    }

    [Fact]
    public void 途中で終わった記録はモードより先に見る()
    {
        // 押すのが利用者だとしても、確認は挟ませる
        var decision = UpdateGate.ForInstall(
            State(UpdateMode.NotifyOnly, interrupted: true), Conditions(), Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
        Assert.Equal(UpdateHoldReason.InterruptedInstall, decision.Reason);
    }

    [Fact]
    public void インストールの失敗のあとは6時間あける()
    {
        var attempts = new UpdateAttempts
        {
            InstallFailures = 1,
            LastInstallFailureAt = Now.AddHours(-5),
        };

        var decision = UpdateGate.ForInstall(State(attempts: attempts), Conditions(), Now);

        Assert.Equal(UpdateHoldReason.RetryBackoff, decision.Reason);
        Assert.Equal(Now.AddHours(1), decision.RetryAt);
    }

    [Fact]
    public void インストールを3回失敗したら自動では試さない()
    {
        var attempts = new UpdateAttempts
        {
            InstallFailures = UpdateRetryPolicy.InstallCap,
            LastInstallFailureAt = Now.AddDays(-1),
        };

        var decision = UpdateGate.ForInstall(State(attempts: attempts), Conditions(), Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
    }

    // ---- 遮断

    [Fact]
    public void 自動を止めていれば取得も適用も見送る()
    {
        var state = State(autoDisabled: true);

        Assert.Equal(UpdateHoldReason.AutoUpdateDisabled, UpdateGate.ForDownload(state, Conditions(), Now).Reason);
        Assert.Equal(UpdateHoldReason.AutoUpdateDisabled, UpdateGate.ForInstall(state, Conditions(), Now).Reason);
    }

    // ---- 文言

    [Fact]
    public void 見送る理由には必ず文がある()
    {
        // 「なぜ更新されないのか分からない」を作らない
        foreach (var reason in Enum.GetValues<UpdateHoldReason>())
        {
            var text = UpdateGate.Describe(reason);

            if (reason == UpdateHoldReason.None)
            {
                Assert.Equal(string.Empty, text);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(text), $"{reason} の説明がありません。");
        }
    }
}
