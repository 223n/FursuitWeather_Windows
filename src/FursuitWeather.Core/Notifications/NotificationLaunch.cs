namespace FursuitWeather.Core.Notifications;

/// <summary>アプリがどこから起動されたか。</summary>
public enum LaunchSource
{
    /// <summary>通知とは関係の無い起動。自動起動やスタートメニューなど。</summary>
    Normal,

    /// <summary>通知を押して起動された。活性化の種類が通知だった。</summary>
    NotificationActivation,

    /// <summary>通知を押して起動された。活性化の種類は通常の起動で、引数で見分けた。</summary>
    NotificationArgument,
}

/// <summary>
/// 通知を押して起動されたか（コールドローンチ）を見分ける。
/// </summary>
/// <remarks>
/// <para>
/// Microsoft Learn の本文とサンプルコードが食い違っている。
/// 本文は活性化の種類を <c>Launch</c> だとし、サンプルは <c>AppNotification</c> で分岐する。
/// どちらで来ても拾えるよう、種類と起動の引数の両方を見る。
/// </para>
/// <para>
/// 未パッケージのアプリでは、Windows App SDK が通知の COM サーバーとして
/// <c>"exe" ----AppNotificationActivated:</c> を登録する。実機のレジストリで確かめた。
/// </para>
/// </remarks>
public static class NotificationLaunch
{
    /// <summary>通知から起動されたときに、Windows App SDK が付ける引数。</summary>
    public const string ActivationArgument = "----AppNotificationActivated:";

    /// <summary>
    /// 起動の経路を決める。
    /// </summary>
    /// <param name="activatedAsNotification">活性化の種類が通知だったか。</param>
    /// <param name="commandLine">起動の引数。</param>
    /// <returns>起動の経路。</returns>
    public static LaunchSource Classify(bool activatedAsNotification, IEnumerable<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (activatedAsNotification)
        {
            return LaunchSource.NotificationActivation;
        }

        return commandLine.Any(argument => argument.StartsWith(ActivationArgument, StringComparison.Ordinal))
            ? LaunchSource.NotificationArgument
            : LaunchSource.Normal;
    }

    /// <summary>診断に出す文字。</summary>
    /// <param name="source">起動の経路。</param>
    /// <returns>起動の経路を表す文字。</returns>
    public static string Describe(LaunchSource source) => source switch
    {
        LaunchSource.NotificationActivation => "通知から（活性化の種類が通知）",
        LaunchSource.NotificationArgument => "通知から（起動の引数で見分けた）",
        _ => "通常",
    };
}
