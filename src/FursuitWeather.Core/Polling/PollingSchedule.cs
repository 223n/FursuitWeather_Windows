namespace FursuitWeather.Core.Polling;

/// <summary>取りに行く対象。</summary>
public enum PollTarget
{
    /// <summary>地点の予報。</summary>
    Forecast,

    /// <summary>環境省の熱中症警戒アラート。</summary>
    Alert,
}

/// <summary>ポーリングの間隔。</summary>
/// <remarks>
/// 本体の <c>public/display.js</c> の値をそのまま踏襲する。
/// 正常なレスポンスは10分間キャッシュされるため、これより詰めても新しいデータは返らない。
/// </remarks>
public static class PollInterval
{
    /// <summary>予報を取りに行く間隔。</summary>
    public static readonly TimeSpan Forecast = TimeSpan.FromMinutes(11);

    /// <summary>アラートを取りに行く間隔。</summary>
    public static readonly TimeSpan Alert = TimeSpan.FromMinutes(31);

    /// <summary>失敗したあと、最初に待つ時間。</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(60);

    /// <summary>失敗が続いたときに待つ時間の上限。</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
}

/// <summary>
/// 取りに行く時期を決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>壁時計と単調時刻の両方を見る。</b>
/// <see cref="Environment.TickCount64"/> は、Windowsでは <c>.NET 10</c> 以前はスリープ中の時間を含み、
/// <c>.NET 11</c> で含まなくなるとMicrosoft Learnに明記されている。
/// 単調時刻だけで周期を組むと、<c>.NET 11</c> へ上げた瞬間に
/// 「よく眠るノートPCでは何時間も取得が走らない」に変わる。
/// </para>
/// <para>
/// 一方で壁時計だけに頼ると、利用者が時計を変えたときに暴発する。
/// どちらかが閾値を超えたら取りに行く形にすると、両方の弱点を避けられる。
/// </para>
/// </remarks>
public sealed record PollingSchedule
{
    /// <summary>最後に取りに成功した壁時計の時刻。まだ無ければ null。</summary>
    public DateTimeOffset? LastSuccessWallClock { get; init; }

    /// <summary>最後に取りに成功した単調時刻。まだ無ければ null。</summary>
    public TimeSpan? LastSuccessMonotonic { get; init; }

    /// <summary>連続して失敗した回数。</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>失敗したあと、次に試してよい単調時刻。</summary>
    public TimeSpan? RetryNotBefore { get; init; }

    /// <summary>
    /// 失敗したあと、次に試してよい壁時計の時刻。
    /// </summary>
    /// <remarks>
    /// 単調時刻はスリープ中に進まないことがあるため、その間の救済に使う。
    /// </remarks>
    public DateTimeOffset? RetryNotBeforeWallClock { get; init; }

    /// <summary>
    /// いま取りに行くべきかを見る。
    /// </summary>
    /// <param name="interval">通常の間隔。</param>
    /// <param name="wallClock">いまの壁時計の時刻。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <returns>取りに行くべきなら true。</returns>
    public bool IsDue(TimeSpan interval, DateTimeOffset wallClock, TimeSpan monotonic)
    {
        // 失敗のあとの待ち時間。単調時刻と壁時計のどちらかが明けていれば試してよい。
        // 単調時刻だけで見ると、長く眠ったあとに待ち時間が明けず、層1で復帰できなくなる
        if (RetryNotBefore is { } notBefore && monotonic < notBefore)
        {
            var wallClockStillWaiting =
                RetryNotBeforeWallClock is not { } wallNotBefore || wallClock < wallNotBefore;

            if (wallClockStillWaiting)
            {
                return false;
            }
        }

        // 一度も成功していなければ、すぐ取りに行く
        if (LastSuccessWallClock is null || LastSuccessMonotonic is null)
        {
            return true;
        }

        var byWallClock = wallClock - LastSuccessWallClock.Value >= interval;
        var byMonotonic = monotonic - LastSuccessMonotonic.Value >= interval;

        // 壁時計が巻き戻された場合、差が負になる。単調時刻の側で救う
        return byWallClock || byMonotonic;
    }

    /// <summary>取りに成功したときの状態を返す。</summary>
    /// <param name="wallClock">いまの壁時計の時刻。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <returns>更新した状態。</returns>
    public PollingSchedule Succeeded(DateTimeOffset wallClock, TimeSpan monotonic) => this with
    {
        LastSuccessWallClock = wallClock,
        LastSuccessMonotonic = monotonic,
        ConsecutiveFailures = 0,
        RetryNotBefore = null,
        RetryNotBeforeWallClock = null,
    };

    /// <summary>
    /// 取りに失敗したときの状態を返す。
    /// </summary>
    /// <param name="wallClock">いまの壁時計の時刻。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="jitter">ゆらぎの割合。0から1の範囲で渡す。</param>
    /// <returns>更新した状態。</returns>
    /// <remarks>
    /// 待ち時間を倍にしていき、上限で頭打ちにする。
    /// 多くの端末が同時に復帰したときに上流へ殺到しないよう、ゆらぎを足す。
    /// ゆらぎの値は呼び出し側が渡す。ここに乱数を持たせるとテストできなくなるためである。
    /// </remarks>
    public PollingSchedule Failed(DateTimeOffset wallClock, TimeSpan monotonic, double jitter = 0d)
    {
        var failures = ConsecutiveFailures + 1;

        // 1分、2分、4分、8分……と倍にし、上限で止める
        var doublings = Math.Min(failures - 1, 16);
        var baseDelay = TimeSpan.FromTicks(PollInterval.Retry.Ticks * (1L << doublings));
        if (baseDelay > PollInterval.MaxBackoff)
        {
            baseDelay = PollInterval.MaxBackoff;
        }

        var clamped = Math.Clamp(jitter, 0d, 1d);
        var delay = baseDelay + (baseDelay * clamped * 0.5d);

        return this with
        {
            ConsecutiveFailures = failures,
            RetryNotBefore = monotonic + delay,
            RetryNotBeforeWallClock = wallClock + delay,
        };
    }

    /// <summary>
    /// スリープからの復帰や回線の回復を受けて、すぐ取りに行けるようにする。
    /// </summary>
    /// <returns>更新した状態。</returns>
    /// <remarks>
    /// これらのイベントは「すぐ取り直すきっかけ」としてだけ使う。
    /// 届かなくても通常のtickで復帰できるよう、間隔の判定は別に持つ。
    /// </remarks>
    public PollingSchedule Resumed() => this with
    {
        RetryNotBefore = null,
        RetryNotBeforeWallClock = null,
    };
}
