namespace FursuitWeather.Core.Update;

/// <summary>更新の確認をいつ行うかの調整値。</summary>
public sealed record UpdateCheckOptions
{
    /// <summary>既定の設定。</summary>
    public static UpdateCheckOptions Default { get; } = new();

    /// <summary>確認の周期。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 周期をずらす幅。
    /// </summary>
    /// <remarks>
    /// 端末ごとに位相をずらす。
    /// 全員が同じ時刻に取りに行くと、配信の側に山ができる。
    /// </remarks>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromHours(2);

    /// <summary>起動してから最初の確認までの、最短の待ち。</summary>
    public TimeSpan StartupDelayMinimum { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>起動してから最初の確認までの、最長の待ち。</summary>
    public TimeSpan StartupDelayMaximum { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// 更新の確認をいつ行うかを決める。
/// </summary>
/// <remarks>
/// <para>
/// 純粋な関数として書く。時計も乱数も自分では読まない。
/// 位相は端末ごとに一度決めた値を呼び出し側が渡す。
/// </para>
/// <para>
/// <b>壁時計と単調時刻の両方を見る。</b>
/// <see cref="Environment.TickCount64"/> は、Windowsでは <c>.NET 10</c> 以前は
/// スリープ中の時間を含み、<c>.NET 11</c> で含まなくなるとMicrosoft Learnに明記されている。
/// 単調時刻だけで周期を組むと、<c>.NET 11</c> へ上げた瞬間に
/// 「よく眠るノートPCでは何日も確認が走らない」に変わる。
/// 一方で壁時計だけに頼ると、利用者が時計を変えたときに暴発する。
/// </para>
/// <para>
/// 同じ考え方を予報のポーリングでも使っている。
/// <see cref="Polling.PollingSchedule"/> を見ること。
/// </para>
/// </remarks>
public static class UpdateCheckSchedule
{
    /// <summary>
    /// 起動してから最初の確認までの待ち。
    /// </summary>
    /// <param name="phase">端末ごとに決めた0以上1未満の値。</param>
    /// <param name="options">調整値。省略すると既定値。</param>
    /// <returns>待つ時間。</returns>
    /// <remarks>
    /// 起動と同時に取りに行かない。
    /// 起動の直後は利用者が何かをしている最中であり、通信も混む。
    /// </remarks>
    public static TimeSpan StartupDelay(double phase, UpdateCheckOptions? options = null)
    {
        var opt = options ?? UpdateCheckOptions.Default;
        var clamped = Math.Clamp(phase, 0d, 1d);
        var span = opt.StartupDelayMaximum - opt.StartupDelayMinimum;

        return opt.StartupDelayMinimum + (span * clamped);
    }

    /// <summary>
    /// 位相を当てはめた、実際の確認の周期。
    /// </summary>
    /// <param name="phase">端末ごとに決めた0以上1未満の値。</param>
    /// <param name="options">調整値。省略すると既定値。</param>
    /// <returns>周期。</returns>
    /// <remarks>0.5を中心に、前後へ <see cref="UpdateCheckOptions.Jitter"/> だけ振れる。</remarks>
    public static TimeSpan EffectiveInterval(double phase, UpdateCheckOptions? options = null)
    {
        var opt = options ?? UpdateCheckOptions.Default;
        var clamped = Math.Clamp(phase, 0d, 1d);

        return opt.Interval + (opt.Jitter * ((clamped * 2d) - 1d));
    }

    /// <summary>
    /// いま確認へ行くべきか。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="wallClock">いまの壁時計（UTC）。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="uptime">アプリが起動してからの時間。</param>
    /// <param name="phase">端末ごとに決めた0以上1未満の値。</param>
    /// <param name="options">調整値。省略すると既定値。</param>
    /// <param name="answeredThisSession">
    /// このプロセスで、確かな答えを得たか。
    /// 通信に失敗した確認や、署名の通らなかった確認は答えに数えない。
    /// </param>
    /// <returns>行くべきなら true。</returns>
    /// <remarks>
    /// <para>
    /// <b>確認だけは、どのモードでも自動で行う。</b>
    /// 止まるのは取得と適用である。
    /// </para>
    /// <para>
    /// 更新が途中のまま起動したときは、確かな答えを得るまで周期を待たずに確認する。
    /// 見つけた更新はメモリにしか持たないため、確認し直さないと次の周期まで何も進まない。
    /// 起動の直後の待ちは、この場合も効かせる。
    /// 答えを得られなかったあとの間隔は、呼び出し側が <see cref="UnansweredRetryDelay"/> で空ける。
    /// </para>
    /// </remarks>
    public static bool IsDue(
        UpdateState state,
        DateTimeOffset wallClock,
        TimeSpan monotonic,
        TimeSpan uptime,
        double phase,
        UpdateCheckOptions? options = null,
        bool answeredThisSession = true)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 起動の直後は行かない。この待ちは、前回いつ確認したかに関わらず効く
        if (uptime < StartupDelay(phase, options))
        {
            return false;
        }

        // 一度も確認していないなら、待ちが明けた時点で行く
        if (state.LastCheckedAt is not { } lastWall || state.LastCheckedMonotonic is not { } lastMonotonic)
        {
            return true;
        }

        if (!answeredThisSession && IsPending(state.Stage))
        {
            return true;
        }

        var interval = EffectiveInterval(phase, options);

        // どちらか一方でも越えていれば行く。
        // 壁時計は利用者の時刻の変更で戻りうるため、単調時刻の側が受け皿になる
        return wallClock - lastWall >= interval || monotonic - lastMonotonic >= interval;
    }

    /// <summary>
    /// 確かな答えを得られなかった確認のあと、次に試すまで待つ時間。
    /// </summary>
    /// <remarks>
    /// 最後の値で頭打ちにする。
    /// 待たずに試すと、回線が切れているあいだ毎分叩き続ける。
    /// 使い切りにすると、起動した直後の1回の失敗で途中の更新が翌日まで止まる。
    /// </remarks>
    public static IReadOnlyList<TimeSpan> UnansweredRetryDelays { get; } =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// 確かな答えを得られなかった確認が続いたときに、次に試すまで待つ時間。
    /// </summary>
    /// <param name="failures">続けて答えを得られなかった回数。</param>
    /// <returns>待つ時間。1回も失敗していなければ0。</returns>
    public static TimeSpan UnansweredRetryDelay(int failures) =>
        failures <= 0
            ? TimeSpan.Zero
            : UnansweredRetryDelays[Math.Min(failures - 1, UnansweredRetryDelays.Count - 1)];

    /// <summary>
    /// 更新が途中の状態か。
    /// </summary>
    /// <param name="stage">保存してあった進み具合。</param>
    /// <returns>途中なら true。</returns>
    /// <remarks>
    /// 失敗と確定したものも含める。
    /// 入れ直しを案内しているため、確認し直して取得までは進めておく。
    /// </remarks>
    public static bool IsPending(UpdateStage stage) => stage is
        UpdateStage.Checking or
        UpdateStage.UpdateAvailable or
        UpdateStage.DownloadHeld or
        UpdateStage.Downloading or
        UpdateStage.DownloadPaused or
        UpdateStage.Downloaded or
        UpdateStage.InstallHeld or
        UpdateStage.Failed;
}
