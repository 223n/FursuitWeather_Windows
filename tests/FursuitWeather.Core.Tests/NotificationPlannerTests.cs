using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Notifications;
using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Tests;

/// <summary>変化の検知と朝のブリーフィングをまとめた層を見る。</summary>
public sealed class NotificationPlannerTests
{
    private const string Location = "35.68,139.77";
    private const string Place = "東京駅の周辺";

    private static DateTimeOffset At(string localTime) => JstTime.ToInstant(localTime)!.Value;

    private static HourForecast Hour(string time, int minutes, string level = "warning", string label = "警戒") => new()
    {
        Time = time,
        Outdoor = new ActivityAssessment
        {
            ActivityMinutes = minutes,
            Level = level,
            Label = label,
            SuitWbgt = 30d,
            Grade = level is "danger" or "coldDanger" ? 4 : 2,
        },
    };

    private static ForecastResponse Forecast(
        DateTimeOffset generatedAt,
        IReadOnlyList<HourForecast>? hours = null,
        IReadOnlyList<DayForecast>? days = null) => new()
        {
            GeneratedAt = generatedAt,
            Hours = hours ?? [],
            Days = days ?? [],
        };

    private static DayForecast Day(string date) => new()
    {
        Date = date,
        TemperatureMax = 35d,
        TemperatureMin = 27d,
        OutdoorWorst = new LevelSummary { Level = "danger", Label = "危険", Grade = 4 },
        RecommendedHours = ["06:00"],
        CoolingRequired = true,
    };

    private static ChangeState Baseline(DateTimeOffset savedAt, int minutes, string label = "警戒") => new()
    {
        SavedAt = savedAt,
        LocationKey = Location,
        BaselineGeneratedAt = savedAt,
        LastMinutes = minutes,
        LastLevel = "warning",
        LastLabel = label,
        LastSuitWbgt = 30d,
    };

    [Fact]
    public void 初回は基準を張るだけで変化の通知を出さない()
    {
        var now = At("2026-08-15T15:30");
        var plan = NotificationPlanner.Create(
            new NotificationState(),
            Forecast(now, [Hour("2026-08-15T15:00", 0, "danger", "危険")]),
            alert: null,
            Location,
            Place,
            now);

        Assert.Empty(plan.Messages);
        Assert.NotNull(plan.State.Change);
        Assert.Equal(Location, plan.State.Change.LocationKey);
    }

    [Fact]
    public void 悪化があれば文面まで組み上がる()
    {
        var now = At("2026-08-15T15:30");
        var previous = new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) };

        var plan = NotificationPlanner.Create(
            previous,
            Forecast(now, [Hour("2026-08-15T15:00", 0, "danger", "危険")]),
            alert: null,
            Location,
            Place,
            now);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(NotificationKind.DiscontinueWear, message.Kind);
        Assert.Equal(NotificationSource.Change, message.Source);
        Assert.Contains("連続20分 → 0分", message.Lines[0], StringComparison.Ordinal);
        Assert.True(message.IsUrgent);
    }

    [Fact]
    public void 公式発表の県名が文面へ届く()
    {
        // 検知の側は県名を持たない。ここで結び付けられていないと
        // 「発表地域」のままの通知が出る
        var now = At("2026-08-15T15:30");
        var alert = new HeatAlert { PrefectureName = "東京都", Special = false };

        var plan = NotificationPlanner.Create(
            new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) },
            Forecast(now, [Hour("2026-08-15T15:00", 20)]),
            alert,
            Location,
            Place,
            now);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(NotificationKind.OfficialAlert, message.Kind);
        Assert.Contains("東京都", message.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void 朝のブリーフィングを出したら日付を進める()
    {
        var now = At("2026-08-15T08:00");
        var plan = NotificationPlanner.Create(
            new NotificationState(),
            Forecast(now, [Hour("2026-08-15T08:00", 20)], [Day("2026-08-15")]),
            alert: null,
            Location,
            Place,
            now);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(NotificationSource.Briefing, message.Source);
        Assert.Equal(new DateOnly(2026, 8, 15), plan.State.LastBriefingDate);
    }

    [Fact]
    public void 同じ日に朝のブリーフィングを二度出さない()
    {
        // 予報は11分ごとに取り直す。日付を持ち回れていないと、午前中ずっと鳴り続ける
        var now = At("2026-08-15T08:00");
        var forecast = Forecast(now, [Hour("2026-08-15T08:00", 20)], [Day("2026-08-15")]);

        var first = NotificationPlanner.Create(
            new NotificationState(), forecast, null, Location, Place, now);

        var second = NotificationPlanner.Create(
            first.State, forecast, null, Location, Place, now.AddMinutes(11));

        Assert.Single(first.Messages);
        Assert.Empty(second.Messages);
        Assert.Equal(new DateOnly(2026, 8, 15), second.State.LastBriefingDate);
    }

    [Fact]
    public void 朝の便りより悪化を後ろへ置く()
    {
        // シェルは後から出したものを上に積む。行動を変える側を手前にする
        var now = At("2026-08-15T08:00");
        var previous = new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) };

        var plan = NotificationPlanner.Create(
            previous,
            Forecast(now, [Hour("2026-08-15T08:00", 0, "danger", "危険")], [Day("2026-08-15")]),
            alert: null,
            Location,
            Place,
            now);

        Assert.Equal(2, plan.Messages.Count);
        Assert.Equal(NotificationSource.Briefing, plan.Messages[0].Source);
        Assert.Equal(NotificationKind.DiscontinueWear, plan.Messages[1].Kind);
    }

    [Fact]
    public void 公式発表はブリーフィングの中身へも伝わる()
    {
        var now = At("2026-08-15T08:00");
        var plan = NotificationPlanner.Create(
            new NotificationState(),
            Forecast(now, [Hour("2026-08-15T08:00", 20)], [Day("2026-08-15")]),
            new HeatAlert { PrefectureName = "東京都" },
            Location,
            Place,
            now);

        var briefing = plan.Messages.First(m => m.Source == NotificationSource.Briefing);
        Assert.Contains("熱中症警戒アラート", briefing.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ブリーフィングを切っていても変化の通知は出す()
    {
        var now = At("2026-08-15T08:00");
        var previous = new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) };

        var plan = NotificationPlanner.Create(
            previous,
            Forecast(now, [Hour("2026-08-15T08:00", 0, "danger", "危険")], [Day("2026-08-15")]),
            alert: null,
            Location,
            Place,
            now,
            briefingOptions: new BriefingOptions { Enabled = false });

        var message = Assert.Single(plan.Messages);
        Assert.Equal(NotificationKind.DiscontinueWear, message.Kind);
    }

    [Fact]
    public void 地点を変えたら基準を張り直して黙る()
    {
        var now = At("2026-08-15T15:30");
        var previous = new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) };

        var plan = NotificationPlanner.Create(
            previous,
            Forecast(now, [Hour("2026-08-15T15:00", 0, "danger", "危険")]),
            alert: null,
            "43.06,141.35",
            "札幌駅の周辺",
            now);

        Assert.Empty(plan.Messages);
        Assert.Equal("43.06,141.35", plan.State.Change!.LocationKey);
    }

    [Fact]
    public void 実物のデモデータでも例外にならない()
    {
        // 手で書いた模造品ではなく、本体が返した実物で通す
        var forecast = TestData.DemoForecast;
        var now = forecast.GeneratedAt;

        var plan = NotificationPlanner.Create(new NotificationState(), forecast, null, Location, Place, now);

        Assert.NotNull(plan.State.Change);
        Assert.All(plan.Messages, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Title));
            Assert.True(m.Lines.Count <= 2);
        });
    }

    [Fact]
    public void 基準の説明に地点と履歴の件数を出す()
    {
        // 通知が出ない時間のほうが長い。動いているかを確かめる手段が要る
        var now = At("2026-08-15T15:30");
        var text = NotificationPlanner.Describe(
            new NotificationState { Change = Baseline(now.AddMinutes(-30), 20) },
            now);

        Assert.Contains(Location, text, StringComparison.Ordinal);
        Assert.Contains("連続20分", text, StringComparison.Ordinal);
        Assert.Contains("30分前", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 基準が無いことを黙らせない()
    {
        var text = NotificationPlanner.Describe(new NotificationState(), At("2026-08-15T15:30"));

        Assert.Contains("基準なし", text, StringComparison.Ordinal);
    }
}
