using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>判定の悪化の検知を見る。</summary>
public sealed class ChangeDetectorTests
{
    private const string Location = "35.68,139.68";

    private static DateTimeOffset At(string localTime) => JstTime.ToInstant(localTime)!.Value;

    /// <summary>時刻と連続活動時間とレベルから、1時間分の予報を作る。</summary>
    private static HourForecast Hour(string time, int minutes, string level = "warning", double suitWbgt = 28d) => new()
    {
        Time = time,
        Outdoor = new ActivityAssessment
        {
            ActivityMinutes = minutes,
            Level = level,
            SuitWbgt = suitWbgt,
            Grade = level is "danger" or "coldDanger" ? 4 : 2,
        },
    };

    private static ForecastResponse Forecast(DateTimeOffset generatedAt, params HourForecast[] hours) => new()
    {
        GeneratedAt = generatedAt,
        Hours = hours,
    };

    /// <summary>基準が張られた状態を作る。</summary>
    private static ChangeState Baseline(
        DateTimeOffset savedAt,
        int minutes,
        string level = "warning",
        double suitWbgt = 28d,
        double? discontinuedAt = null,
        IReadOnlyList<NotificationRecord>? history = null) => new()
        {
            SavedAt = savedAt,
            LocationKey = Location,
            BaselineGeneratedAt = savedAt,
            LastMinutes = minutes,
            LastLevel = level,
            LastSuitWbgt = suitWbgt,
            DiscontinuedSuitWbgt = discontinuedAt,
            History = history ?? [],
        };

    [Fact]
    public void 初回は基準を張るだけで通知しない()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(null, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
        Assert.Equal(0, result.State.LastMinutes);
        Assert.Equal(Location, result.State.LocationKey);
    }

    [Fact]
    public void 状態が古いと張り直して通知しない()
    {
        var now = At("2026-08-15T10:30");
        var stale = Baseline(now.AddHours(-7), 20);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(stale, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 地点が変わると張り直して通知しない()
    {
        var now = At("2026-08-15T10:30");
        var other = Baseline(now.AddMinutes(-11), 20) with { LocationKey = "43.06,141.35" };
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(other, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
        Assert.Equal(Location, result.State.LocationKey);
    }

    [Fact]
    public void 古い応答では基準を更新せず通知しない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20);
        // generatedAt が基準以下のレスポンス
        var forecast = Forecast(state.BaselineGeneratedAt.AddMinutes(-1), Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
        Assert.Equal(state.BaselineGeneratedAt, result.State.BaselineGeneratedAt);
    }

    [Fact]
    public void ゼロ分になったら着用中止級を出す()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.DiscontinueWear, n.Kind);
        Assert.True(n.IsUrgent);
        Assert.False(n.IsCold);
    }

    [Fact]
    public void 暑熱から低温へ移ったときも着用中止級を出す()
    {
        // danger と coldDanger はどちらも 0分・grade 4 のため、数値だけでは検出できない
        var now = At("2026-01-15T06:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger");
        var forecast = Forecast(now, Hour("2026-01-15T06:00", 0, "coldDanger", suitWbgt: 2d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.DiscontinueWear, n.Kind);
        Assert.True(n.IsCold);
    }

    [Fact]
    public void 同じレベルでゼロ分が続くときは繰り返さない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger");
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 短縮したら通知する()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.Shortened, n.Kind);
        Assert.Equal(20, n.PreviousMinutes);
        Assert.Equal(10, n.CurrentMinutes);
        Assert.False(n.IsUrgent);
    }

    [Fact]
    public void 延びたときは通知しない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 10);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 20));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 同じ内容は再び出さない()
    {
        var now = At("2026-08-15T10:30");
        var signature = $"{Location}|{NotificationKind.Shortened}|2026-08-15T10:00|20|10|warning";
        var state = Baseline(now.AddMinutes(-11), 20, history:
            [new NotificationRecord(NotificationKind.Shortened, now.AddHours(-5), signature)]);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 同じ種類は二時間あけないと出さない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20, history:
            [new NotificationRecord(NotificationKind.Shortened, now.AddMinutes(-90), "別の署名")]);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 二時間あいていれば出す()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20, history:
            [new NotificationRecord(NotificationKind.Shortened, now.AddMinutes(-121), "別の署名")]);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Single(result.Notifications);
    }

    [Fact]
    public void 一日の上限を超えたら打ち切りを告げる()
    {
        // 黙って止めると「もう悪化がない」と読み違えられる
        var now = At("2026-08-15T20:30");
        var history = Enumerable.Range(0, 6)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-15T08:00").AddMinutes(i), $"署名{i}"))
            .ToList();
        var state = Baseline(now.AddMinutes(-11), 20, history: history);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.DailyCapReached, n.Kind);
        // 短縮そのものは出さない
        Assert.DoesNotContain(result.Notifications, x => x.Kind == NotificationKind.Shortened);
    }

    [Fact]
    public void 打ち切りの告知は一日一回だけ()
    {
        var now = At("2026-08-15T20:30");
        var history = Enumerable.Range(0, 6)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-15T08:00").AddMinutes(i), $"署名{i}"))
            .ToList();
        history.Add(new NotificationRecord(NotificationKind.DailyCapReached, At("2026-08-15T18:00"), "既出の告知"));
        var state = Baseline(now.AddMinutes(-11), 20, history: history);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void 打ち切りの告知は上限に数えない()
    {
        // 告知自身を数えると、翌日以降の判定がずれる
        var now = At("2026-08-15T20:30");
        var history = Enumerable.Range(0, 5)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-15T08:00").AddMinutes(i), $"署名{i}"))
            .ToList();
        history.Add(new NotificationRecord(NotificationKind.DailyCapReached, At("2026-08-15T18:00"), "既出の告知"));
        var state = Baseline(now.AddMinutes(-11), 20, history: history);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        // 短縮は5件しか出ていないので、6件目として通る
        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.Shortened, n.Kind);
    }

    [Fact]
    public void 着用中止級は一日の上限に縛られない()
    {
        var now = At("2026-08-15T20:30");
        var history = Enumerable.Range(0, 6)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-15T08:00").AddMinutes(i), $"署名{i}"))
            .ToList();
        var state = Baseline(now.AddMinutes(-11), 20, history: history);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.DiscontinueWear, n.Kind);
    }

    [Fact]
    public void 着用中止級が一日二回目なら短縮へ落とす()
    {
        // 着用中止級は1日1回だが、黙ってはいけない。
        // 20分から0分への転落は、その日2回目でも新しい情報である
        var now = At("2026-08-15T20:30");
        var state = Baseline(now.AddMinutes(-11), 20, history:
            [new NotificationRecord(NotificationKind.DiscontinueWear, At("2026-08-15T09:00"), "朝の署名")]);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.Shortened, n.Kind);
        Assert.Equal(20, n.PreviousMinutes);
        Assert.Equal(0, n.CurrentMinutes);
    }

    [Fact]
    public void 着用中止級と短縮は同時に出さない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.DiscontinueWear, n.Kind);
    }

    [Fact]
    public void 公式のアラートが出たら通知する()
    {
        var now = At("2026-08-15T05:10");
        var state = Baseline(now.AddMinutes(-11), 0, "danger");
        var forecast = Forecast(now, Hour("2026-08-15T05:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, alertActive: true, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.OfficialAlert, n.Kind);
        Assert.True(n.IsUrgent);
        Assert.True(result.State.AlertActive);
    }

    [Fact]
    public void アラートが続いている間は繰り返さない()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger") with { AlertActive = true };
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, alertActive: true, Location, now);

        Assert.Empty(result.Notifications);
    }

    [Fact]
    public void アラートは一日の上限に縛られない()
    {
        var now = At("2026-08-15T20:30");
        var history = Enumerable.Range(0, 6)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-15T08:00").AddMinutes(i), $"署名{i}"))
            .ToList();
        var state = Baseline(now.AddMinutes(-11), 0, "danger", history: history);
        var forecast = Forecast(now, Hour("2026-08-15T20:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, alertActive: true, Location, now);

        Assert.Contains(result.Notifications, n => n.Kind == NotificationKind.OfficialAlert);
    }

    [Fact]
    public void 回復は連続して続かないと認めない()
    {
        var now = At("2026-08-15T17:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 33d, discontinuedAt: 33d);
        // 17時は回復しているが、18時にまた0分へ戻る
        var forecast = Forecast(now,
            Hour("2026-08-15T17:00", 10, "severe", suitWbgt: 31d),
            Hour("2026-08-15T18:00", 0, "danger", suitWbgt: 33d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.DoesNotContain(result.Notifications, n => n.Kind == NotificationKind.Recovery);
    }

    [Fact]
    public void 回復は下がり幅が足りないと認めない()
    {
        var now = At("2026-08-15T17:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 33d, discontinuedAt: 33d);
        // 0.5℃下がっていない
        var forecast = Forecast(now,
            Hour("2026-08-15T17:00", 10, "severe", suitWbgt: 32.8d),
            Hour("2026-08-15T18:00", 10, "severe", suitWbgt: 32.7d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.DoesNotContain(result.Notifications, n => n.Kind == NotificationKind.Recovery);
    }

    [Fact]
    public void 条件を満たせば回復を通知する()
    {
        var now = At("2026-08-15T17:30");
        var state = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 33d, discontinuedAt: 33d);
        var forecast = Forecast(now,
            Hour("2026-08-15T17:00", 10, "severe", suitWbgt: 31d),
            Hour("2026-08-15T18:00", 10, "severe", suitWbgt: 30d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.Recovery, n.Kind);
        Assert.False(n.IsUrgent);
        // 回復したら、着用中止のときの値は忘れる
        Assert.Null(result.State.DiscontinuedSuitWbgt);
    }

    [Fact]
    public void 着用中止を出したときの値を覚える()
    {
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 0, "danger", suitWbgt: 34.5d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.Equal(34.5d, result.State.DiscontinuedSuitWbgt);
    }

    [Fact]
    public void 先読みの窓のうち最も厳しい時間を選ぶ()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now,
            Hour("2026-08-15T10:00", 20),
            Hour("2026-08-15T11:00", 10),
            Hour("2026-08-15T12:00", 30));

        var target = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(3));

        Assert.NotNull(target);
        Assert.Equal("2026-08-15T11:00", target.Time);
    }

    [Fact]
    public void 先読みの窓の外は選ばない()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now,
            Hour("2026-08-15T10:00", 20),
            // 窓の外。ここが最も厳しくても選ばない
            Hour("2026-08-15T20:00", 0, "danger"));

        var target = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(3));

        Assert.NotNull(target);
        Assert.Equal("2026-08-15T10:00", target.Time);
    }

    [Fact]
    public void 同じ分数なら早い時間を選ぶ()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now,
            Hour("2026-08-15T12:00", 10),
            Hour("2026-08-15T11:00", 10));

        var target = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(3));

        Assert.NotNull(target);
        Assert.Equal("2026-08-15T11:00", target.Time);
    }

    [Fact]
    public void いまが属する時間も対象に入れる()
    {
        // 10時30分のとき、10時の行も見る
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var target = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(3));

        Assert.NotNull(target);
        Assert.Equal("2026-08-15T10:00", target.Time);
    }

    [Fact]
    public void 過ぎた時間は選ばない()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now, Hour("2026-08-15T08:00", 0, "danger"));

        Assert.Null(ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void 先読みの幅を変えられる()
    {
        var now = At("2026-08-15T10:30");
        var forecast = Forecast(now,
            Hour("2026-08-15T10:00", 20),
            Hour("2026-08-15T15:00", 0, "danger"));

        var narrow = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(1));
        var wide = ChangeDetector.SelectTarget(forecast, now, TimeSpan.FromHours(6));

        Assert.Equal("2026-08-15T10:00", narrow!.Time);
        Assert.Equal("2026-08-15T15:00", wide!.Time);
    }

    [Fact]
    public void 履歴は上限を超えて溜まらない()
    {
        var options = ChangeDetectorOptions.Default with { HistoryLimit = 3 };
        var history = Enumerable.Range(0, 3)
            .Select(i => new NotificationRecord(NotificationKind.Shortened, At("2026-08-14T08:00").AddMinutes(i), $"古い署名{i}"))
            .ToList();
        var now = At("2026-08-15T10:30");
        var state = Baseline(now.AddMinutes(-11), 20, history: history);
        var forecast = Forecast(now, Hour("2026-08-15T10:00", 10));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now, options);

        Assert.Single(result.Notifications);
        Assert.Equal(3, result.State.History.Count);
        // いちばん古いものが押し出される
        Assert.DoesNotContain(result.State.History, r => r.Signature == "古い署名0");
    }

    [Theory]
    [InlineData(ActivityLevel.Optimal, true)]
    [InlineData(ActivityLevel.ColdCaution, true)]
    [InlineData(ActivityLevel.ColdWarning, true)]
    [InlineData(ActivityLevel.ColdDanger, true)]
    [InlineData(ActivityLevel.Safe, false)]
    [InlineData(ActivityLevel.Danger, false)]
    [InlineData(ActivityLevel.Unknown, false)]
    public void 低温側かどうかを見分ける(ActivityLevel level, bool expected)
    {
        Assert.Equal(expected, level.IsCold());
    }

    // ---- 公式発表がガードで握り潰されないこと

    [Fact]
    public void アラートは解除後の再発表でも通知する()
    {
        // 実装が作る署名をそのまま履歴へ積むため、検知器を通して組み立てる。
        // 署名の形式に依存しない形にしないと、退行のテストにならない
        var day1 = At("2026-08-15T05:10");
        var state = Baseline(day1.AddMinutes(-11), 0, "danger");

        var first = ChangeDetector.Evaluate(
            state, Forecast(day1, Hour("2026-08-15T05:00", 0, "danger")), true, Location, day1);
        Assert.Contains(first.Notifications, n => n.Kind == NotificationKind.OfficialAlert);

        // 解除
        var cleared = At("2026-08-15T23:00");
        var second = ChangeDetector.Evaluate(
            first.State, Forecast(cleared, Hour("2026-08-15T23:00", 0, "danger")), false, Location, cleared);
        Assert.False(second.State.AlertActive);

        // 翌日の再発表。状態が古くならないよう、間に1回はさむ
        var morning = At("2026-08-16T04:00");
        var third = ChangeDetector.Evaluate(
            second.State, Forecast(morning, Hour("2026-08-16T04:00", 0, "danger")), false, Location, morning);

        var day2 = At("2026-08-16T05:10");
        var fourth = ChangeDetector.Evaluate(
            third.State, Forecast(day2, Hour("2026-08-16T05:00", 0, "danger")), true, Location, day2);

        Assert.Contains(fourth.Notifications, n => n.Kind == NotificationKind.OfficialAlert);
    }

    [Fact]
    public void 同じ日に解除と再発表があっても通知する()
    {
        var morning = At("2026-08-15T05:10");
        var state = Baseline(morning.AddMinutes(-11), 0, "danger");

        var first = ChangeDetector.Evaluate(
            state, Forecast(morning, Hour("2026-08-15T05:00", 0, "danger")), true, Location, morning);
        Assert.Contains(first.Notifications, n => n.Kind == NotificationKind.OfficialAlert);

        var cleared = At("2026-08-15T15:00");
        var second = ChangeDetector.Evaluate(
            first.State, Forecast(cleared, Hour("2026-08-15T15:00", 0, "danger")), false, Location, cleared);

        var again = At("2026-08-15T18:00");
        var third = ChangeDetector.Evaluate(
            second.State, Forecast(again, Hour("2026-08-15T18:00", 0, "danger")), true, Location, again);

        Assert.Contains(third.Notifications, n => n.Kind == NotificationKind.OfficialAlert);
    }

    [Fact]
    public void 状態が古くてもアラートは通知する()
    {
        // 夜間にアプリを止める運用では、翌朝の初回のポーリングが必ずこの経路を通る。
        // 午前5時の発表をここで消してはいけない
        var now = At("2026-08-15T08:00");
        var stale = Baseline(now.AddHours(-9), 20);
        var forecast = Forecast(now, Hour("2026-08-15T08:00", 10));

        var result = ChangeDetector.Evaluate(stale, forecast, alertActive: true, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.OfficialAlert, n.Kind);
        Assert.True(result.State.AlertActive);
    }

    [Fact]
    public void 初回の起動でもアラートは通知する()
    {
        var now = At("2026-08-15T08:00");
        var forecast = Forecast(now, Hour("2026-08-15T08:00", 10));

        var result = ChangeDetector.Evaluate(null, forecast, alertActive: true, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.OfficialAlert, n.Kind);
    }

    [Fact]
    public void 古い応答でもアラートは通知する()
    {
        var now = At("2026-08-15T05:10");
        var state = Baseline(now.AddMinutes(-11), 0, "danger");
        var forecast = Forecast(state.BaselineGeneratedAt, Hour("2026-08-15T05:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, alertActive: true, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.OfficialAlert, n.Kind);
        Assert.True(result.State.AlertActive);
        // 基準そのものは動かさない
        Assert.Equal(state.BaselineGeneratedAt, result.State.BaselineGeneratedAt);
        Assert.Equal(state.LastMinutes, result.State.LastMinutes);
    }

    [Fact]
    public void アラートの解除を状態へ取り込む()
    {
        // 取り込まないと、次の発表で立ち上がりを検出できなくなる
        var now = At("2026-08-15T23:00");
        var state = Baseline(now.AddMinutes(-11), 0, "danger") with { AlertActive = true };
        var forecast = Forecast(now, Hour("2026-08-15T23:00", 0, "danger"));

        var result = ChangeDetector.Evaluate(state, forecast, alertActive: false, Location, now);

        Assert.False(result.State.AlertActive);
    }

    // ---- 回復の基準

    [Fact]
    public void 張り直しても回復の基準を引き継ぐ()
    {
        var now = At("2026-08-15T16:20");
        var stale = Baseline(now.AddHours(-7), 0, "danger", suitWbgt: 35d, discontinuedAt: 35d);
        var forecast = Forecast(now, Hour("2026-08-15T16:00", 10, "severe", suitWbgt: 34.9d));

        var result = ChangeDetector.Evaluate(stale, forecast, false, Location, now);

        Assert.Equal(35d, result.State.DiscontinuedSuitWbgt);
    }

    [Fact]
    public void 地点が変われば回復の基準を捨てる()
    {
        var now = At("2026-08-15T16:20");
        var other = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 35d, discontinuedAt: 35d)
            with
        { LocationKey = "43.06,141.35" };
        var forecast = Forecast(now, Hour("2026-08-15T16:00", 10, "severe", suitWbgt: 34.9d));

        var result = ChangeDetector.Evaluate(other, forecast, false, Location, now);

        Assert.Null(result.State.DiscontinuedSuitWbgt);
    }

    [Fact]
    public void 基準が無くても直前の値でわずかな改善を退ける()
    {
        // 基準が無いことを「デッドバンド合格」と読んではいけない
        var now = At("2026-08-15T16:20");
        var state = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 35d, discontinuedAt: null);
        var forecast = Forecast(now,
            Hour("2026-08-15T16:00", 10, "severe", suitWbgt: 34.9d),
            Hour("2026-08-15T17:00", 10, "severe", suitWbgt: 34.8d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        Assert.DoesNotContain(result.Notifications, n => n.Kind == NotificationKind.Recovery);
    }

    [Fact]
    public void 基準が無くても十分下がれば回復を認める()
    {
        var now = At("2026-08-15T16:20");
        var state = Baseline(now.AddMinutes(-11), 0, "danger", suitWbgt: 35d, discontinuedAt: null);
        var forecast = Forecast(now,
            Hour("2026-08-15T16:00", 10, "severe", suitWbgt: 33d),
            Hour("2026-08-15T17:00", 10, "severe", suitWbgt: 32d));

        var result = ChangeDetector.Evaluate(state, forecast, false, Location, now);

        var n = Assert.Single(result.Notifications);
        Assert.Equal(NotificationKind.Recovery, n.Kind);
    }
}
