namespace FursuitWeather.Core.Update;

/// <summary>ゲートの結論。</summary>
public enum GateOutcome
{
    /// <summary>自動で進めてよい。</summary>
    Proceed,

    /// <summary>条件により見送る。理由を見せ、脱出口を置くこと。</summary>
    Hold,

    /// <summary>設定により、利用者が押すのを待つ。</summary>
    WaitForUser,
}

/// <summary>ゲートを通すかの判断。</summary>
/// <param name="Outcome">結論。</param>
/// <param name="Reason">見送る理由。見送らないときは <see cref="UpdateHoldReason.None"/>。</param>
/// <param name="RetryAt">見送りを解くのに待つ時刻。時間で解けないときは null。</param>
public sealed record GateDecision(GateOutcome Outcome, UpdateHoldReason Reason, DateTimeOffset? RetryAt = null)
{
    /// <summary>自動で進めてよいか。</summary>
    public bool CanProceed => Outcome == GateOutcome.Proceed;

    /// <summary>通してよいという結論。</summary>
    public static GateDecision Proceed { get; } = new(GateOutcome.Proceed, UpdateHoldReason.None);

    /// <summary>利用者を待つという結論。</summary>
    public static GateDecision WaitForUser { get; } = new(GateOutcome.WaitForUser, UpdateHoldReason.None);

    /// <summary>見送るという結論を作る。</summary>
    /// <param name="reason">理由。</param>
    /// <param name="retryAt">解けるまで待つ時刻。</param>
    /// <returns>結論。</returns>
    public static GateDecision Hold(UpdateHoldReason reason, DateTimeOffset? retryAt = null) =>
        new(GateOutcome.Hold, reason, retryAt);
}

/// <summary>
/// いまの環境の様子。
/// </summary>
/// <remarks>
/// 判定に使うAPIはUIの層が呼び、ここへ値として渡す。
/// Coreは <c>Windows.Networking.Connectivity</c> にも <c>PowerManager</c> にも触れない。
/// </remarks>
public sealed record UpdateConditions
{
    /// <summary>
    /// 従量制課金の接続か。
    /// </summary>
    /// <remarks>
    /// <c>GetInternetConnectionProfile()</c> は null を返しうる。
    /// 分からないときは安全側に倒し、真として渡すこと。
    /// </remarks>
    public bool IsMetered { get; init; }

    /// <summary>電池で動いているか。</summary>
    public bool IsOnBattery { get; init; }

    /// <summary>
    /// 通知を出してよい状態か。
    /// </summary>
    /// <remarks>
    /// <c>SHQueryUserNotificationState()</c> が <c>QUNS_ACCEPTS_NOTIFICATIONS</c> を返すかどうか。
    /// 全画面、プレゼンテーション、ロック、応答不可の時間帯などで偽になる。
    /// </remarks>
    public bool AcceptsNotifications { get; init; } = true;

    /// <summary>アプリが起動してからの時間。</summary>
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// 取得と適用の2つのゲートを判定する。
/// </summary>
/// <remarks>
/// <para>
/// 純粋な関数として書く。時計もファイルも触らない。
/// 根拠は <c>docs/update.md</c> の「3つのモード」と「モードAでも適用を止める条件」にある。
/// </para>
/// <para>
/// <b>ここが判定するのは自動で進めてよいかだけである。</b>
/// 利用者がボタンを押したときは、このゲートを通さない。
/// 抑制は自動の側にだけ効かせ、手動の経路は常に生かす。
/// </para>
/// </remarks>
public static class UpdateGate
{
    /// <summary>起動してからこの時間は、自動でインストールしない。</summary>
    public static readonly TimeSpan MinimumUptimeBeforeInstall = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 取得へ進んでよいかを見る。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="conditions">環境の様子。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>判断。</returns>
    /// <remarks>
    /// <para>
    /// 見送るのは従量制課金と電池のときである。
    /// 全画面や起動直後は取得を妨げない。裏で受け取るだけで割り込まないためである。
    /// </para>
    /// <para>
    /// <b>呼ぶ前に <see cref="UpdateLedger.RecordAvailable"/> で狙いを張り直すこと。</b>
    /// 失敗の記録は狙いの版とSHA-256に紐づく。
    /// 張り直さずに見ると、前の版の失敗で次の版まで止める。
    /// </para>
    /// </remarks>
    public static GateDecision ForDownload(UpdateState state, UpdateConditions conditions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(conditions);

        // お知らせのみは、取得も押されるまで動かない
        if (state.Mode == UpdateMode.NotifyOnly)
        {
            return GateDecision.WaitForUser;
        }

        if (state.AutoUpdateDisabled)
        {
            return GateDecision.Hold(UpdateHoldReason.AutoUpdateDisabled);
        }

        if (state.PauseOnMetered && conditions.IsMetered)
        {
            return GateDecision.Hold(UpdateHoldReason.Metered);
        }

        if (state.PauseOnBattery && conditions.IsOnBattery)
        {
            return GateDecision.Hold(UpdateHoldReason.OnBattery);
        }

        var retryAt = UpdateRetryPolicy.NextDownloadAt(state.Attempts);
        if (retryAt is { } at && now < at)
        {
            return GateDecision.Hold(UpdateHoldReason.RetryBackoff, at);
        }

        if (UpdateRetryPolicy.IsDownloadExhausted(state.Attempts, now))
        {
            // 窓が明ける時刻を返す。時間で解ける見送りを「解けない」と見せない
            return GateDecision.Hold(
                UpdateHoldReason.RetryBackoff,
                state.Attempts.LastDownloadFailureAt + UpdateRetryPolicy.DownloadWindow);
        }

        return GateDecision.Proceed;
    }

    /// <summary>
    /// 適用へ進んでよいかを見る。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="conditions">環境の様子。</param>
    /// <param name="now">いまの時刻。</param>
    /// <returns>判断。</returns>
    /// <remarks>
    /// <para>
    /// 自動で入れると決めていても、割り込んではいけない状況では見送る。
    /// 「自動なのに勝手に再起動された」より「自動のはずが1回だけボタンを求められた」ほうが、
    /// 被害が小さいという判断である。
    /// </para>
    /// <para>
    /// 見送ったときは取得だけ自動の扱いへ降格し、そのことを知らせる。
    /// </para>
    /// </remarks>
    public static GateDecision ForInstall(UpdateState state, UpdateConditions conditions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(conditions);

        // 前回が途中で終わっているときは、自動の扱いでも再実行しない。
        // 無人で壊れた状態へ上書きを繰り返すのが最も危ない。
        // モードより先に見る。押すのは利用者だとしても、確認は挟ませる
        if (state.HasInterruptedInstall)
        {
            return GateDecision.Hold(UpdateHoldReason.InterruptedInstall);
        }

        if (state.Mode != UpdateMode.Automatic)
        {
            return GateDecision.WaitForUser;
        }

        if (state.AutoUpdateDisabled)
        {
            return GateDecision.Hold(UpdateHoldReason.AutoUpdateDisabled);
        }

        if (!conditions.AcceptsNotifications)
        {
            return GateDecision.Hold(UpdateHoldReason.DoNotDisturb);
        }

        if (conditions.Uptime < MinimumUptimeBeforeInstall)
        {
            return GateDecision.Hold(UpdateHoldReason.JustStarted);
        }

        var retryAt = UpdateRetryPolicy.NextInstallAt(state.Attempts);
        if (retryAt is { } at && now < at)
        {
            return GateDecision.Hold(UpdateHoldReason.RetryBackoff, at);
        }

        if (UpdateRetryPolicy.IsInstallExhausted(state.Attempts))
        {
            return GateDecision.Hold(UpdateHoldReason.RetryBackoff);
        }

        return GateDecision.Proceed;
    }

    /// <summary>
    /// 見送った理由を、利用者に見せる文にする。
    /// </summary>
    /// <param name="reason">理由。</param>
    /// <returns>説明の文。</returns>
    /// <remarks>
    /// 「なぜ更新されないのか分からない」を作らないために要る。
    /// 文の隣には必ず「今すぐ実行」を置くこと。
    /// </remarks>
    public static string Describe(UpdateHoldReason reason) => reason switch
    {
        UpdateHoldReason.None => string.Empty,
        UpdateHoldReason.Metered => "従量制課金の接続のため、自動での取得を見送っています。",
        UpdateHoldReason.OnBattery => "電池で動いているため、自動での取得を見送っています。",
        UpdateHoldReason.DoNotDisturb => "全画面の表示や応答不可の時間帯のため、インストールを見送っています。",
        UpdateHoldReason.JustStarted => "起動した直後のため、インストールを見送っています。",
        UpdateHoldReason.RetryBackoff => "続けて失敗したため、次に試すまで待っています。",
        UpdateHoldReason.AutoUpdateDisabled => "続けて失敗したため、自動での更新を止めています。手で入れ直してください。",
        UpdateHoldReason.InterruptedInstall => "前回のインストールが途中で終わっています。実行する前に確かめてください。",
        _ => "自動での更新を見送っています。",
    };
}
