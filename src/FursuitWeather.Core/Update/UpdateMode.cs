namespace FursuitWeather.Core.Update;

/// <summary>
/// 更新の扱い方。
/// </summary>
/// <remarks>
/// <para>
/// 3つを別々の仕組みにはしない。
/// 1本の状態機械にゲートを2つ置き、どちらを自動にするかで表す。
/// </para>
/// <list type="bullet">
/// <item>取得ゲート。更新ありから取得へ進むところ</item>
/// <item>適用ゲート。取得済みからインストールへ進むところ</item>
/// </list>
/// <para>
/// 「取得は手動だがインストールは自動」は意味を成さない。
/// そのため設定にはトグル2つではなく、選択肢を3つ置く。
/// </para>
/// <para>
/// <b>更新があるかの確認は、どのモードでも自動で行う。</b>
/// <see cref="NotifyOnly"/> で止まるのは取得と適用だけである。
/// </para>
/// </remarks>
public enum UpdateMode
{
    /// <summary>取得も適用も自動。</summary>
    Automatic,

    /// <summary>
    /// 取得だけ自動。適用は利用者が決める。
    /// </summary>
    /// <remarks>
    /// <b>これを既定にする。</b>
    /// 未署名のあいだに <see cref="Automatic"/> を既定にすると、
    /// 自動のはずが席にいない利用者のSmartScreenの操作を待つことになる。
    /// <see cref="NotifyOnly"/> を既定にすると、
    /// self-containedにした <c>.NET</c> 自体の修正すら届かない。
    /// </remarks>
    DownloadOnly,

    /// <summary>お知らせのみ。取得も適用もボタンを押したときだけ。</summary>
    NotifyOnly,
}

/// <summary>
/// 更新の進み具合。
/// </summary>
/// <remarks>
/// 意味と次へ進む条件は <c>docs/update.md</c> の「状態遷移」にある。
/// </remarks>
public enum UpdateStage
{
    /// <summary>最新、または未確認。</summary>
    Idle,

    /// <summary>マニフェストを照会中。</summary>
    Checking,

    /// <summary>新しい版があり、まだ取得していない。</summary>
    UpdateAvailable,

    /// <summary>条件により取得を保留している。</summary>
    DownloadHeld,

    /// <summary>取得中。</summary>
    Downloading,

    /// <summary>回線が切れるなどして取得が中断している。</summary>
    DownloadPaused,

    /// <summary>取得を終え、署名とSHA-256の検証も済んでいる。</summary>
    Downloaded,

    /// <summary>条件により適用を保留している。</summary>
    InstallHeld,

    /// <summary>自プロセスを終えてインストーラーを走らせている。</summary>
    Installing,

    /// <summary>次回の起動で版を照合し、成功と確定した。</summary>
    Succeeded,

    /// <summary>次回の起動で版を照合し、失敗と確定した。</summary>
    Failed,
}

/// <summary>
/// 自動で進めるのを見送る理由。
/// </summary>
/// <remarks>
/// <b>保留したときは理由を必ず見せ、「今すぐ実行」の脱出口を置く。</b>
/// 「なぜ更新されないのか分からない」が、更新の仕組みに対する最大の不満の源である。
/// </remarks>
public enum UpdateHoldReason
{
    /// <summary>見送る理由が無い。</summary>
    None,

    /// <summary>従量制課金の接続である。</summary>
    Metered,

    /// <summary>電池で動いている。</summary>
    OnBattery,

    /// <summary>全画面やプレゼンテーションなどで、割り込んではいけない状態である。</summary>
    DoNotDisturb,

    /// <summary>起動してまだ間がない。</summary>
    JustStarted,

    /// <summary>この版で失敗が続いており、次に試してよい時刻まで待っている。</summary>
    RetryBackoff,

    /// <summary>自動での更新そのものを止めている。</summary>
    AutoUpdateDisabled,

    /// <summary>前回のインストールが途中で終わっており、確認を待っている。</summary>
    InterruptedInstall,

    /// <summary>掲示モードのあいだ。掲示を終えたときに入れる。</summary>
    DisplayActive,
}
