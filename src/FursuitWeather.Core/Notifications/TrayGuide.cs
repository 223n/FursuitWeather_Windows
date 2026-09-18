namespace FursuitWeather.Core.Notifications;

/// <summary>トレイのアイコンの案内をどうするか。</summary>
public enum TrayGuideAction
{
    /// <summary>何もしない。済んでいるか、いまは出せない。</summary>
    None,

    /// <summary>案内を出し、出したと記録する。</summary>
    Show,

    /// <summary>出さずに、済んだと記録する。</summary>
    MarkOnly,
}

/// <summary>
/// トレイのアイコンを表へ出すよう、初回に1回だけ案内する。
/// </summary>
/// <remarks>
/// <para>
/// Windows 11 は、あとから足したトレイのアイコンを既定でタスクバーの「^」の中へ隠す。
/// 表へ出せるのは利用者だけで、アプリからは出せない。
/// 「トレイだけ」を選んだ利用者は、隠れたアイコンのほかに窓口が無い。
/// </para>
/// <para>
/// <b>ダイアログにはしない。</b>
/// 無人の端末、掲示の端末、自動の更新のあとの起動し直しで、OKを待って止まるためである。
/// トーストなら待たせず、通知センターにも残る。
/// </para>
/// </remarks>
public static class TrayGuide
{
    /// <summary>見出し。</summary>
    public const string Title = "FursuitWeather はタスクトレイで動いています";

    /// <summary>ボタンの文字。</summary>
    public const string ButtonText = "タスクバーの設定を開く";

    /// <summary>ボタンで開く先。項目の名前は Windows の版で変わるため、ページだけを開く。</summary>
    public static Uri SettingsUri { get; } = new("ms-settings:taskbar");

    /// <summary>本文。トーストに載るのは2行まで。</summary>
    public static IReadOnlyList<string> Lines { get; } =
    [
        "アイコンはタスクバーの「^」の中に隠れています",
        "常に表示するには、タスクバーの設定で切り替えてください",
    ];

    /// <summary>
    /// いま案内をどうするかを決める。
    /// </summary>
    /// <param name="alreadyShown">前に済ませたか。</param>
    /// <param name="displayActive">掲示で始めた、または掲示のあいだか。</param>
    /// <param name="notificationsEnabled">アプリの設定でトースト通知を使うか。</param>
    /// <param name="promoted">アイコンがすでに表へ出ているか。読めなければ null。</param>
    /// <returns>すること。</returns>
    /// <remarks>
    /// <para>
    /// 掲示のあいだは出さず、済んだとも記録しない。
    /// 来場者の見る画面に出さないためで、次に掲示なしで起動したときへ回す。
    /// </para>
    /// <para>
    /// 表へ出ているなら案内は要らない。前の版から使っていて、自分で出した利用者に当たる。
    /// 読めないときは出ていないとみなす。出しすぎても1回で済むが、出し損ねると窓口を見失う。
    /// </para>
    /// <para>
    /// アプリの設定でトースト通知を切っている利用者には出さない。
    /// 自分で通知を切った利用者に、通知で割り込まない。
    /// </para>
    /// </remarks>
    public static TrayGuideAction Decide(bool alreadyShown, bool displayActive, bool notificationsEnabled, bool? promoted)
    {
        if (alreadyShown || displayActive)
        {
            return TrayGuideAction.None;
        }

        if (promoted == true || !notificationsEnabled)
        {
            return TrayGuideAction.MarkOnly;
        }

        return TrayGuideAction.Show;
    }
}
