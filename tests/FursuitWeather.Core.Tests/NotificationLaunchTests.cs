using FursuitWeather.Core.Notifications;

namespace FursuitWeather.Core.Tests;

/// <summary>通知から起動されたかの見分けと、トレイの案内の判断を見る。</summary>
public sealed class NotificationLaunchTests
{
    private const string Exe = @"C:\Users\someone\AppData\Local\Programs\FursuitWeather\FursuitWeather.Widget.exe";

    // ---- 通知からの起動

    [Fact]
    public void 活性化の種類が通知なら引数を見ずに通知からとする()
    {
        // サンプルコードの経路
        Assert.Equal(LaunchSource.NotificationActivation, NotificationLaunch.Classify(true, [Exe]));
    }

    [Fact]
    public void 種類が通常の起動でも引数があれば通知からとする()
    {
        // Microsoft Learn の本文の経路。COM サーバーの起動の行がこの引数を付ける
        Assert.Equal(
            LaunchSource.NotificationArgument,
            NotificationLaunch.Classify(false, [Exe, "----AppNotificationActivated:"]));
    }

    [Fact]
    public void 引数の後ろに続きがあっても通知からとする()
    {
        Assert.Equal(
            LaunchSource.NotificationArgument,
            NotificationLaunch.Classify(false, [Exe, "----AppNotificationActivated:action=open"]));
    }

    [Theory]
    [InlineData("--self-test-notification")]
    [InlineData("--update-manifest-url=https://github.com/223n/FursuitWeather_Windows/releases/download/v0.3.0/update.json")]
    [InlineData("AppNotificationActivated")]
    [InlineData("----appnotificationactivated:")]
    public void 通知と関係の無い引数では通常の起動とする(string argument)
    {
        Assert.Equal(LaunchSource.Normal, NotificationLaunch.Classify(false, [Exe, argument]));
    }

    [Fact]
    public void 引数が無ければ通常の起動とする()
    {
        Assert.Equal(LaunchSource.Normal, NotificationLaunch.Classify(false, [Exe]));
    }

    // ---- トレイの案内

    [Fact]
    public void 初めての起動で表に出ていなければ案内する()
    {
        Assert.Equal(TrayGuideAction.Show, TrayGuide.Decide(false, false, true, false));
    }

    [Fact]
    public void 表に出ているか読めなければ案内する()
    {
        // 出し損ねると窓口を見失う。出しすぎても1回で済む
        Assert.Equal(TrayGuideAction.Show, TrayGuide.Decide(false, false, true, null));
    }

    [Fact]
    public void 済んでいれば何もしない()
    {
        Assert.Equal(TrayGuideAction.None, TrayGuide.Decide(true, false, true, false));
    }

    [Fact]
    public void 掲示のあいだは出さず済んだとも記録しない()
    {
        // 次に掲示なしで起動したときへ回す
        Assert.Equal(TrayGuideAction.None, TrayGuide.Decide(false, true, true, false));
    }

    [Fact]
    public void すでに表へ出していれば出さずに済ませる()
    {
        // 前の版から使っていて、自分で出した利用者
        Assert.Equal(TrayGuideAction.MarkOnly, TrayGuide.Decide(false, false, true, true));
    }

    [Fact]
    public void 通知を切っている利用者には出さずに済ませる()
    {
        Assert.Equal(TrayGuideAction.MarkOnly, TrayGuide.Decide(false, false, false, false));
    }

    [Fact]
    public void 案内の本文はトーストに載る2行に収める()
    {
        Assert.Equal(2, TrayGuide.Lines.Count);
        Assert.Equal("ms-settings:taskbar", TrayGuide.SettingsUri.OriginalString);
    }
}
