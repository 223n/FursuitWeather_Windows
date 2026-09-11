using FursuitWeather.Core.Time;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>更新の確認の周期と、促す時期を見る。</summary>
public sealed class UpdateCheckScheduleTests
{
    private const string ShaA = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";
    private const string ShaB = "60e4c1df2a6d10783be26203062cc612dd0a4f879fe80ef1f2d8b3d41f9a9514";

    private static readonly DateTimeOffset Now = JstTime.ToInstant("2026-09-10T12:00")!.Value;

    private static UpdateState Checked(DateTimeOffset wall, TimeSpan monotonic) => new()
    {
        LastCheckedAt = wall,
        LastCheckedMonotonic = monotonic,
    };

    // ---- 起動の直後

    [Theory]
    [InlineData(0d, 3)]
    [InlineData(0.5d, 6.5)]
    [InlineData(1d, 10)]
    public void 起動の待ちは3分から10分に収まる(double phase, double expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UpdateCheckSchedule.StartupDelay(phase));
    }

    [Fact]
    public void 位相が範囲の外でも収まる()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), UpdateCheckSchedule.StartupDelay(-5d));
        Assert.Equal(TimeSpan.FromMinutes(10), UpdateCheckSchedule.StartupDelay(99d));
    }

    [Fact]
    public void 起動の直後は確認しない()
    {
        var due = UpdateCheckSchedule.IsDue(
            new UpdateState(), Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 0.5d);

        Assert.False(due);
    }

    [Fact]
    public void 待ちが明ければ一度も確認していなくても行く()
    {
        var due = UpdateCheckSchedule.IsDue(
            new UpdateState(), Now, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(7), 0.5d);

        Assert.True(due);
    }

    [Fact]
    public void 前に確認していても起動の待ちは効く()
    {
        // 再起動のたびに即座へ取りに行かせない
        var state = Checked(Now.AddDays(-3), TimeSpan.Zero);

        var due = UpdateCheckSchedule.IsDue(state, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 0.5d);

        Assert.False(due);
    }

    // ---- 周期

    [Theory]
    [InlineData(0d, 22)]
    [InlineData(0.5d, 24)]
    [InlineData(1d, 26)]
    public void 周期は24時間の前後2時間に収まる(double phase, double expectedHours)
    {
        Assert.Equal(TimeSpan.FromHours(expectedHours), UpdateCheckSchedule.EffectiveInterval(phase));
    }

    [Fact]
    public void 周期に達していなければ行かない()
    {
        var state = Checked(Now.AddHours(-23), TimeSpan.FromHours(1));

        var due = UpdateCheckSchedule.IsDue(
            state, Now, TimeSpan.FromHours(24), TimeSpan.FromHours(1), 0.5d);

        Assert.False(due);
    }

    [Fact]
    public void 壁時計が周期を越えれば行く()
    {
        var state = Checked(Now.AddHours(-25), TimeSpan.FromHours(24));

        var due = UpdateCheckSchedule.IsDue(
            state, Now, TimeSpan.FromHours(24.5), TimeSpan.FromHours(25), 0.5d);

        Assert.True(due);
    }

    [Fact]
    public void 単調時刻が周期を越えれば行く()
    {
        // 壁時計だけを見ていると、利用者が時計を戻したときに永久に行かなくなる
        var state = Checked(Now.AddHours(1), TimeSpan.FromHours(1));

        var due = UpdateCheckSchedule.IsDue(
            state, Now, TimeSpan.FromHours(26), TimeSpan.FromHours(30), 0.5d);

        Assert.True(due);
    }

    [Fact]
    public void 時計を戻されても暴発しない()
    {
        // 単調時刻の側がまだ達していなければ行かない
        var state = Checked(Now.AddHours(10), TimeSpan.FromHours(1));

        var due = UpdateCheckSchedule.IsDue(
            state, Now, TimeSpan.FromHours(2), TimeSpan.FromHours(30), 0.5d);

        Assert.False(due);
    }

    // ---- 促し方

    [Fact]
    public void 一度も促していなければ割り込む()
    {
        Assert.Equal(PromptChannel.Toast, UpdatePrompt.Decide(new UpdateState(), Now));
    }

    [Fact]
    public void 夜中には割り込まない()
    {
        var night = JstTime.ToInstant("2026-09-10T03:00")!.Value;

        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(new UpdateState(), night));
    }

    [Fact]
    public void 夜遅くにも割り込まない()
    {
        var late = JstTime.ToInstant("2026-09-10T22:00")!.Value;

        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(new UpdateState(), late));
    }

    [Theory]
    [InlineData("2026-09-10T09:00")]
    [InlineData("2026-09-10T21:00")]
    public void 境目の時刻は割り込んでよい(string time)
    {
        var at = JstTime.ToInstant(time)!.Value;

        Assert.Equal(PromptChannel.Toast, UpdatePrompt.Decide(new UpdateState(), at));
    }

    [Fact]
    public void 同じ日に二度は割り込まない()
    {
        var state = new UpdateState { LastPromptAt = JstTime.ToInstant("2026-09-10T09:30")!.Value, PromptCount = 1 };

        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 回数を数えていない状態でも同じ日に二度は割り込まない()
    {
        // 間隔の判定は PromptCount を見るため、0 のときは素通りする。
        // 保存した内容を手で編集されたときや、回数を持たない古い状態を読んだときに起こる。
        // 同じ日の判定は、そこを塞ぐために要る
        var state = new UpdateState
        {
            LastPromptAt = JstTime.ToInstant("2026-09-10T09:30")!.Value,
            PromptCount = 0,
        };

        Assert.Null(UpdatePrompt.NextPromptAt(state));
        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(2, 72)]
    [InlineData(3, 168)]
    [InlineData(9, 168)]
    public void 促す間隔は7日で頭打ちにする(int promptCount, double expectedHours)
    {
        // 限りなく延ばすと、更新が永久に届かない
        var state = new UpdateState { LastPromptAt = Now, PromptCount = promptCount };

        Assert.Equal(Now.AddHours(expectedHours), UpdatePrompt.NextPromptAt(state));
    }

    [Fact]
    public void セキュリティの修正は間隔を伸ばさない()
    {
        var state = new UpdateState { LastPromptAt = Now, PromptCount = 3 };

        Assert.Equal(Now.AddHours(24), UpdatePrompt.NextPromptAt(state, UpdateSeverity.Security));
    }

    [Fact]
    public void 間隔が明けていなければ割り込まない()
    {
        var state = new UpdateState
        {
            LastPromptAt = JstTime.ToInstant("2026-09-09T12:00")!.Value,
            PromptCount = 2,
        };

        // 2回目のあとは72時間あける。翌日ではまだ早い
        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 間隔が明ければまた割り込む()
    {
        var state = new UpdateState
        {
            LastPromptAt = JstTime.ToInstant("2026-09-06T12:00")!.Value,
            PromptCount = 2,
        };

        Assert.Equal(PromptChannel.Toast, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 五回で割り込みを止める()
    {
        var state = new UpdateState
        {
            LastPromptAt = JstTime.ToInstant("2026-08-01T12:00")!.Value,
            PromptCount = UpdatePrompt.ToastLimit,
        };

        // 止めても消しはしない。トレイと小窓と設定画面には残す
        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 割り込んだことだけを数える()
    {
        var state = UpdatePrompt.RecordToast(new UpdateState(), Now);

        Assert.Equal(1, state.PromptCount);
        Assert.Equal(Now, state.LastPromptAt);
    }

    [Fact]
    public void 新しい版を見つけたら割り込みの予算を戻す()
    {
        // 戻さないと、アプリの生涯で5回しか知らせない
        var state = UpdateLedger.RecordAvailable(new UpdateState(), "0.3.1", ShaA) with
        {
            LastPromptAt = JstTime.ToInstant("2026-08-01T12:00")!.Value,
            PromptCount = UpdatePrompt.ToastLimit,
        };
        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));

        state = UpdateLedger.RecordAvailable(state, "0.3.2", ShaB);

        Assert.Equal(PromptChannel.Toast, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 同じ版のあいだは割り込みの予算を戻さない()
    {
        var state = UpdateLedger.RecordAvailable(new UpdateState(), "0.3.1", ShaA) with
        {
            LastPromptAt = JstTime.ToInstant("2026-08-01T12:00")!.Value,
            PromptCount = UpdatePrompt.ToastLimit,
        };

        state = UpdateLedger.RecordAvailable(state, "0.3.1", ShaA);

        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    [Fact]
    public void 版が変わっても同じ日に二度は割り込まない()
    {
        var state = UpdateLedger.RecordAvailable(new UpdateState(), "0.3.1", ShaA) with
        {
            LastPromptAt = JstTime.ToInstant("2026-09-10T09:30")!.Value,
            PromptCount = 1,
        };

        state = UpdateLedger.RecordAvailable(state, "0.3.2", ShaB);

        Assert.Equal(PromptChannel.Passive, UpdatePrompt.Decide(state, Now));
    }

    // ---- 更新が途中のまま起動したとき

    private static UpdateState CheckedBeforeReboot(UpdateStage stage) =>
        Checked(Now.AddHours(-1), TimeSpan.FromHours(5)) with { Stage = stage };

    [Theory]
    [InlineData(UpdateStage.UpdateAvailable)]
    [InlineData(UpdateStage.DownloadHeld)]
    [InlineData(UpdateStage.DownloadPaused)]
    [InlineData(UpdateStage.Downloaded)]
    [InlineData(UpdateStage.InstallHeld)]
    [InlineData(UpdateStage.Failed)]
    public void 更新が途中のまま起動したら周期を待たずに確認する(UpdateStage stage)
    {
        // 見つけた更新はメモリにしか持たない。確かめ直さないと、次の周期まで何も進まない
        var due = UpdateCheckSchedule.IsDue(
            CheckedBeforeReboot(stage),
            Now,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(20),
            0.5d,
            answeredThisSession: false);

        Assert.True(due);
    }

    [Fact]
    public void 更新が途中でも起動の待ちは効かせる()
    {
        var due = UpdateCheckSchedule.IsDue(
            CheckedBeforeReboot(UpdateStage.Downloaded),
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            0.5d,
            answeredThisSession: false);

        Assert.False(due);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(9, 60)]
    public void 答えを得られなかった確認は間を空けて試し直す(int failures, int expectedMinutes)
    {
        // 待たずに試すと、回線が切れているあいだ毎分叩き続ける。
        // 使い切りにすると、起動した直後の1回の失敗で途中の更新が翌日まで止まる
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UpdateCheckSchedule.UnansweredRetryDelay(failures));
    }

    [Fact]
    public void 答えの無かった確認のあとは待ちが明けるまで待つ()
    {
        var lastTick = TimeSpan.FromMinutes(10);

        Assert.True(UpdateCheckSchedule.IsWaitingAfterUnanswered(
            1, Now, lastTick, Now.AddMinutes(4), lastTick + TimeSpan.FromMinutes(4)));
        Assert.False(UpdateCheckSchedule.IsWaitingAfterUnanswered(
            1, Now, lastTick, Now.AddMinutes(5), lastTick + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void 時計が戻っても単調時刻で待ちを終える()
    {
        // 壁時計だけで決めると、戻った幅だけ試し直しが止まる
        var lastTick = TimeSpan.FromMinutes(10);

        Assert.False(UpdateCheckSchedule.IsWaitingAfterUnanswered(
            1, Now, lastTick, Now.AddHours(-3), lastTick + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void 再起動で単調時刻が戻っても壁時計で待ちを終える()
    {
        Assert.False(UpdateCheckSchedule.IsWaitingAfterUnanswered(
            1, Now, TimeSpan.FromHours(5), Now.AddMinutes(5), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void 答えの無かった確認が無ければ待たない()
    {
        Assert.False(UpdateCheckSchedule.IsWaitingAfterUnanswered(0, null, null, Now, TimeSpan.Zero));
    }

    [Fact]
    public void 途中の確認は答えを得たら終える()
    {
        var due = UpdateCheckSchedule.IsDue(
            CheckedBeforeReboot(UpdateStage.DownloadHeld),
            Now,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(20),
            0.5d,
            answeredThisSession: true);

        Assert.False(due);
    }

    [Theory]
    [InlineData(UpdateStage.Idle)]
    [InlineData(UpdateStage.Succeeded)]
    public void 途中でなければ周期を待つ(UpdateStage stage)
    {
        var due = UpdateCheckSchedule.IsDue(
            CheckedBeforeReboot(stage),
            Now,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(20),
            0.5d,
            answeredThisSession: false);

        Assert.False(due);
    }
}
