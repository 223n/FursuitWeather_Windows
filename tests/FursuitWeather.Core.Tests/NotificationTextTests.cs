using FursuitWeather.Core.Briefing;
using FursuitWeather.Core.Changes;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Notifications;

namespace FursuitWeather.Core.Tests;

/// <summary>通知の文面の組み立てを見る。</summary>
public sealed class NotificationTextTests
{
    private const string Place = "東京駅の周辺";

    private static PendingNotification Change(
        NotificationKind kind,
        int previousMinutes = 20,
        int currentMinutes = 0,
        string previousLabel = "警戒",
        string currentLabel = "危険",
        bool isCold = false,
        string time = "2026-08-15T15:00") => new()
        {
            Kind = kind,
            Signature = "signature",
            Hour = new HourForecast { Time = time },
            PreviousMinutes = previousMinutes,
            CurrentMinutes = currentMinutes,
            PreviousLabel = previousLabel,
            CurrentLabel = currentLabel,
            IsCold = isCold,
            IsUrgent = kind is NotificationKind.DiscontinueWear or NotificationKind.OfficialAlert,
        };

    [Fact]
    public void 着用中止は地点と時刻と分の変化を出す()
    {
        var message = NotificationText.Build(Change(NotificationKind.DiscontinueWear), Place);

        Assert.Equal($"着用中止（{Place}）", message.Title);
        Assert.Contains("15時ごろ", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("警戒 → 危険", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("連続20分 → 0分", message.Lines[0], StringComparison.Ordinal);
        Assert.True(message.IsUrgent);
    }

    [Fact]
    public void 低温の着用中止に熱中症の導線を出さない()
    {
        // coldDanger も grade 4 で0分になる。
        // ここに冷房環境への退避を出すのは誤誘導であり、低温では逆に危ない
        var message = NotificationText.Build(
            Change(NotificationKind.DiscontinueWear, previousLabel: "低温警戒", currentLabel: "低温危険", isCold: true),
            Place);

        Assert.DoesNotContain("冷房", message.Lines[1], StringComparison.Ordinal);
        Assert.Contains("凍傷", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 暑熱の着用中止には冷房環境への退避を出す()
    {
        var message = NotificationText.Build(Change(NotificationKind.DiscontinueWear), Place);

        Assert.Contains("冷房環境へ退避", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 前後の分が同じときは矢印を出さない()
    {
        // danger から coldDanger へ移ると、どちらも0分のまま種類だけが変わる。
        // 「連続0分 → 0分」は何も伝えない
        var message = NotificationText.Build(
            Change(
                NotificationKind.DiscontinueWear,
                previousMinutes: 0,
                currentMinutes: 0,
                previousLabel: "危険",
                currentLabel: "低温危険",
                isCold: true),
            Place);

        Assert.DoesNotContain("0分 → 0分", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("連続0分", message.Lines[0], StringComparison.Ordinal);

        // レベルの移り変わりだけは残す。ここが唯一の情報だからである
        Assert.Contains("危険 → 低温危険", message.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 短縮は見出しに分の変化を出し重くしない()
    {
        var message = NotificationText.Build(
            Change(NotificationKind.Shortened, previousMinutes: 20, currentMinutes: 10, currentLabel: "厳重警戒"),
            Place);

        Assert.Equal($"連続20分 → 10分（{Place}）", message.Title);
        Assert.Contains("警戒 → 厳重警戒", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("10分以内にとどめ", message.Lines[1], StringComparison.Ordinal);
        Assert.False(message.IsUrgent);
    }

    [Fact]
    public void 低温の短縮では冷房へ誘導しない()
    {
        var message = NotificationText.Build(
            Change(
                NotificationKind.Shortened,
                previousMinutes: 45,
                currentMinutes: 30,
                previousLabel: "低温注意",
                currentLabel: "低温警戒",
                isCold: true),
            Place);

        Assert.DoesNotContain("冷房", message.Lines[1], StringComparison.Ordinal);
        Assert.Contains("暖かい屋内", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 前のラベルが無くても矢印だけが残らない()
    {
        // 古い保存内容から読むとラベルが空になる。
        // そこで「 → 厳重警戒」のような文面を作らない
        var message = NotificationText.Build(
            Change(NotificationKind.Shortened, previousLabel: string.Empty, currentLabel: "厳重警戒"),
            Place);

        Assert.DoesNotContain("→ 厳重警戒", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("厳重警戒", message.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 前後のラベルが同じなら並べない()
    {
        var message = NotificationText.Build(
            Change(NotificationKind.Shortened, previousLabel: "危険", currentLabel: "危険"),
            Place);

        Assert.DoesNotContain("危険 → 危険", message.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 時刻が読めなくても文面を組める()
    {
        var message = NotificationText.Build(
            Change(NotificationKind.DiscontinueWear, time: "こわれた値"),
            Place);

        Assert.Equal($"着用中止（{Place}）", message.Title);
        Assert.False(message.Lines[0].StartsWith(" ・ ", StringComparison.Ordinal));
        Assert.Contains("連続20分 → 0分", message.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 公式発表は県名を見出しへ入れる()
    {
        var alert = new HeatAlert { PrefectureName = "東京都", Special = false };
        var message = NotificationText.Build(Change(NotificationKind.OfficialAlert), Place, alert);

        Assert.Equal("熱中症警戒アラート（東京都）", message.Title);
        Assert.True(message.IsUrgent);
    }

    [Fact]
    public void 特別警戒アラートは見出しを変える()
    {
        var alert = new HeatAlert { PrefectureName = "東京都", Special = true };
        var message = NotificationText.Build(Change(NotificationKind.OfficialAlert), Place, alert);

        Assert.Equal("熱中症特別警戒アラート（東京都）", message.Title);
    }

    [Fact]
    public void 県名が無くても空のかっこを出さない()
    {
        var message = NotificationText.Build(Change(NotificationKind.OfficialAlert), Place, alert: null);

        Assert.DoesNotContain("（）", message.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void 復帰は重くしない()
    {
        var message = NotificationText.Build(
            Change(NotificationKind.Recovery, previousMinutes: 0, currentMinutes: 10, currentLabel: "厳重警戒"),
            Place);

        Assert.False(message.IsUrgent);
        Assert.Contains("連続10分", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("体調と装備", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 打ち切りの告知は悪化が止まったと読めない文にする()
    {
        var message = NotificationText.Build(
            Change(NotificationKind.DailyCapReached),
            Place);

        Assert.Contains("悪化が止まったという意味ではありません", message.Lines[0], StringComparison.Ordinal);
        Assert.False(message.IsUrgent);
    }

    [Fact]
    public void どの種類でも本文は2行までにする()
    {
        // シェルが受け取るのはタイトルと追加の2要素まで。
        // 超えた行は黙って落ちるため、安全に関わる文が消える
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var message = NotificationText.Build(Change(kind), Place);
            Assert.True(message.Lines.Count <= 2, $"{kind} の本文が{message.Lines.Count}行あります。");
        }
    }

    [Fact]
    public void ブリーフィングは最悪の判定と気温を出す()
    {
        var message = NotificationText.Build(Briefing(), Place);

        Assert.Equal($"今日の着ぐるみ（{Place}）", message.Title);
        Assert.Contains("危険", message.Lines[0], StringComparison.Ordinal);
        Assert.Contains("最高35℃", message.Lines[0], StringComparison.Ordinal);
        Assert.False(message.IsUrgent);
    }

    [Fact]
    public void 適した時間帯が無いことをはっきり伝える()
    {
        var message = NotificationText.Build(Briefing(hours: []), Place);

        Assert.Contains("向く時間帯はありません", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ブリーフィングは公式発表を先頭へ置く()
    {
        // 本文は入りきらないと後ろから切れる。危険は前へ置く
        var message = NotificationText.Build(Briefing(alertActive: true), Place);

        Assert.StartsWith("熱中症警戒アラート", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 時間帯が多いときは切り詰めて件数を添える()
    {
        var message = NotificationText.Build(
            Briefing(hours: ["06:00", "07:00", "08:00", "17:00", "18:00"]),
            Place);

        Assert.Contains("6時台 / 7時台 / 8時台 ほか2件", message.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 読めない時間帯は落とさずそのまま出す()
    {
        var message = NotificationText.Build(Briefing(hours: ["夕方"]), Place);

        Assert.Contains("夕方", message.Lines[1], StringComparison.Ordinal);
    }

    private static BriefingContent Briefing(
        bool alertActive = false,
        bool coolingRequired = true,
        IReadOnlyList<string>? hours = null) => new()
        {
            Date = new DateOnly(2026, 8, 15),
            Worst = new LevelSummary { Level = "danger", Label = "危険", Grade = 4 },
            TemperatureMax = 35d,
            TemperatureMin = 27d,
            RecommendedHours = hours ?? ["06:00"],
            CoolingRequired = coolingRequired,
            AlertActive = alertActive,
        };
}
