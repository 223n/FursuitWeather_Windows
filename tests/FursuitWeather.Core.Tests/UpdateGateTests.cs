using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>取得と適用の2つのゲートを見る。</summary>
public sealed class UpdateGateTests
{
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
    public void 取得の上限に達したら自動では試さない()
    {
        var attempts = new UpdateAttempts
        {
            DownloadFailures = UpdateRetryPolicy.DownloadDailyCap,
            LastDownloadFailureAt = Now.AddDays(-1),
        };

        var decision = UpdateGate.ForDownload(State(attempts: attempts), Conditions(), Now);

        Assert.Equal(GateOutcome.Hold, decision.Outcome);
        Assert.Equal(UpdateHoldReason.RetryBackoff, decision.Reason);
    }

    // ---- 適用のゲート

    [Fact]
    public void 自動ならインストールへ進む()
    {
        var decision = UpdateGate.ForInstall(State(), Conditions(), Now);

        Assert.True(decision.CanProceed);
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
