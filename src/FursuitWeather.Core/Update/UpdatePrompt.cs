using FursuitWeather.Core.Time;

namespace FursuitWeather.Core.Update;

/// <summary>更新をどう促すか。</summary>
public enum PromptChannel
{
    /// <summary>いまは促さない。</summary>
    None,

    /// <summary>トーストで割り込む。</summary>
    Toast,

    /// <summary>割り込まず、トレイと小窓と設定画面にだけ残す。</summary>
    Passive,
}

/// <summary>
/// 更新を利用者へ促す時期を決める。
/// </summary>
/// <remarks>
/// <para>
/// 根拠は <c>docs/update.md</c> の「再提示の間隔」にある。
/// </para>
/// <para>
/// <b>限りなく延ばす指数のバックオフにしない。</b>
/// 更新を永久に届かなくするためである。
/// 7日で頭打ちにし、そこから先は同じ間隔で出し続ける。
/// </para>
/// <para>
/// ただし割り込みは、1つの狙いにつき5回で止める。
/// そのあとはトレイと小窓と設定画面にだけ残し、押したい人が押せる状態を保つ。
/// 回数は新しい版を見つけたときに <see cref="UpdateLedger.RecordAvailable"/> が戻す。
/// 戻さないと、アプリの生涯で5回しか知らせなくなる。
/// </para>
/// </remarks>
public static class UpdatePrompt
{
    /// <summary>「あとで」を選んだあと、次に促すまで待つ時間。</summary>
    /// <remarks>最後の値で頭打ちにする。</remarks>
    public static IReadOnlyList<TimeSpan> Backoff { get; } =
    [
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(72),
        TimeSpan.FromDays(7),
    ];

    /// <summary>セキュリティの修正のときに待つ時間。伸ばさない。</summary>
    public static readonly TimeSpan SecurityInterval = TimeSpan.FromHours(24);

    /// <summary>1つの狙いについて、トーストで割り込む上限。</summary>
    public const int ToastLimit = 5;

    /// <summary>これより早い時刻には出さない（日本時間）。</summary>
    public static readonly TimeOnly Earliest = new(9, 0);

    /// <summary>これより遅い時刻には出さない（日本時間）。</summary>
    public static readonly TimeOnly Latest = new(21, 0);

    /// <summary>
    /// 次に促してよい時刻。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="severity">更新の重さ。</param>
    /// <returns>待つ時刻。一度も促していなければ null。</returns>
    public static DateTimeOffset? NextPromptAt(UpdateState state, UpdateSeverity severity = UpdateSeverity.Normal)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.LastPromptAt is not { } last || state.PromptCount <= 0)
        {
            return null;
        }

        // セキュリティの修正は間隔を伸ばさない。モードの選択は上書きしない
        if (severity == UpdateSeverity.Security)
        {
            return last + SecurityInterval;
        }

        var index = Math.Min(state.PromptCount - 1, Backoff.Count - 1);
        return last + Backoff[index];
    }

    /// <summary>
    /// いま、どう促すべきか。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="severity">更新の重さ。</param>
    /// <returns>促し方。</returns>
    /// <remarks>
    /// <para>
    /// 割り込むのは9時から21時のあいだだけである。
    /// 同じ日に2回以上は割り込まない。
    /// </para>
    /// <para>
    /// 割り込まないと決めたときも <see cref="PromptChannel.Passive"/> を返す。
    /// 更新があること自体は、常にどこかに出しておく。
    /// </para>
    /// </remarks>
    public static PromptChannel Decide(
        UpdateState state,
        DateTimeOffset now,
        UpdateSeverity severity = UpdateSeverity.Normal)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 割り込みの予算を使い切っている
        if (state.PromptCount >= ToastLimit)
        {
            return PromptChannel.Passive;
        }

        var local = JstTime.ToLocal(now);
        var clock = TimeOnly.FromDateTime(local);

        if (clock < Earliest || clock > Latest)
        {
            return PromptChannel.Passive;
        }

        if (state.LastPromptAt is { } last)
        {
            // 同じ日に2回以上は割り込まない
            if (JstTime.ToLocal(last).Date == local.Date)
            {
                return PromptChannel.Passive;
            }

            if (NextPromptAt(state, severity) is { } next && now < next)
            {
                return PromptChannel.Passive;
            }
        }

        return PromptChannel.Toast;
    }

    /// <summary>
    /// 促したことを状態へ書き入れる。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="now">促した時刻。</param>
    /// <returns>書き入れたあとの状態。</returns>
    /// <remarks>
    /// 数えるのは割り込みだけである。
    /// 小窓に出ているだけのものを数えると、予算が黙って尽きる。
    /// </remarks>
    public static UpdateState RecordToast(UpdateState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state with { LastPromptAt = now, PromptCount = state.PromptCount + 1 };
    }
}
