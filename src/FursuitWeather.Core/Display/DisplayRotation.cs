namespace FursuitWeather.Core.Display;

/// <summary>巡回の状態。</summary>
/// <remarks>
/// 時刻は単調時刻で持つ。
/// 壁時計で持つと、利用者が時計を戻したときに巡回が止まる。
/// </remarks>
public sealed record RotationState
{
    /// <summary>いま出しているスライド。</summary>
    public DisplaySlide Current { get; init; } = DisplaySlide.Now;

    /// <summary>このスライドを終える単調時刻。止めているあいだは使わない。</summary>
    public TimeSpan Deadline { get; init; }

    /// <summary>一時停止を自動で解く単調時刻。止めていなければ null。</summary>
    public TimeSpan? PausedUntil { get; init; }

    /// <summary>止めているか。</summary>
    public bool IsPaused => PausedUntil is not null;
}

/// <summary>
/// スライドの巡回を決める。
/// </summary>
/// <remarks>
/// <para>
/// 本体のWebと同じく、スライドごとに期限を持つ方式にする。
/// Mac版の「開いてからの経過を一周の長さで割った余り」は、
/// もしものときが出入りして一周の長さが変わるたびに、出す位置が飛ぶ。
/// </para>
/// <para>
/// 次へ進むときは、並びの上でいまの位置の次にある、出してよいスライドを選ぶ。
/// そのため、もしものときが出入りしても、いま出しているスライドは保たれる。
/// </para>
/// </remarks>
public static class DisplayRotation
{
    /// <summary>一時停止を自動で解くまでの時間。</summary>
    /// <remarks>
    /// Webは「再開」を押すまで止まったままにしている。
    /// 無人の端末では止まったままになりうるため、自動で解く。
    /// </remarks>
    public static readonly TimeSpan PauseLimit = TimeSpan.FromMinutes(5);

    private static readonly DisplaySlide[] Order =
    [
        DisplaySlide.Now,
        DisplaySlide.Hours,
        DisplaySlide.Days,
        DisplaySlide.National,
        DisplaySlide.Emergency,
    ];

    /// <summary>スライドを出しておく時間。本体と同じ秒数。</summary>
    /// <param name="slide">スライド。</param>
    /// <returns>出しておく時間。</returns>
    public static TimeSpan Duration(DisplaySlide slide) => slide switch
    {
        DisplaySlide.Now => TimeSpan.FromSeconds(15),
        DisplaySlide.Hours => TimeSpan.FromSeconds(20),
        DisplaySlide.Days => TimeSpan.FromSeconds(15),
        DisplaySlide.National => TimeSpan.FromSeconds(20),
        DisplaySlide.Emergency => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(15),
    };

    /// <summary>いま出してよいスライド。</summary>
    /// <param name="emergency">もしものときを加えるか。</param>
    /// <returns>巡回の順に並べたスライド。</returns>
    public static IReadOnlyList<DisplaySlide> ActiveSlides(bool emergency) =>
        emergency ? Order : Order[..^1];

    /// <summary>掲示を始める。</summary>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <returns>始めの状態。</returns>
    public static RotationState Start(TimeSpan monotonic) => new()
    {
        Current = DisplaySlide.Now,
        Deadline = monotonic + Duration(DisplaySlide.Now),
    };

    /// <summary>
    /// 1秒ごとに呼び、進めるべきなら進める。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="emergency">もしものときを加えるか。</param>
    /// <returns>次の状態。</returns>
    /// <remarks>
    /// スリープから戻って期限を大きく過ぎていても、1つだけ進める。
    /// 過ぎた分を飛ばして追いかけると、戻った直後に見せたいスライドを飛ばしうる。
    /// </remarks>
    public static RotationState Tick(RotationState state, TimeSpan monotonic, bool emergency)
    {
        ArgumentNullException.ThrowIfNull(state);

        var active = ActiveSlides(emergency);

        // 出していたスライドが外れた（もしものときが条件から外れた）なら、止めていても次へ進める
        if (!active.Contains(state.Current))
        {
            return MoveNext(state, monotonic, active);
        }

        if (state.PausedUntil is { } until)
        {
            if (monotonic < until)
            {
                return state;
            }

            // 自動で解く。いまのスライドを出し直すところから数える
            return state with
            {
                PausedUntil = null,
                Deadline = monotonic + Duration(state.Current),
            };
        }

        return monotonic < state.Deadline ? state : MoveNext(state, monotonic, active);
    }

    /// <summary>
    /// 手で次へ送る。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="emergency">もしものときを加えるか。</param>
    /// <returns>次の状態。</returns>
    /// <remarks>
    /// 送ったスライドの秒数が過ぎれば、自動送りへ戻る。
    /// 止めているあいだに送ったときは、止めたままにする。
    /// </remarks>
    public static RotationState Next(RotationState state, TimeSpan monotonic, bool emergency)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Move(state, monotonic, ActiveSlides(emergency), forward: true);
    }

    /// <summary>
    /// 手で前へ戻す。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <param name="emergency">もしものときを加えるか。</param>
    /// <returns>次の状態。</returns>
    /// <remarks>
    /// 本体のWebの左矢印に当たる。
    /// 送ったあとの扱いは <see cref="Next"/> と同じで、秒数が過ぎれば自動送りへ戻る。
    /// </remarks>
    public static RotationState Previous(RotationState state, TimeSpan monotonic, bool emergency)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Move(state, monotonic, ActiveSlides(emergency), forward: false);
    }

    /// <summary>
    /// 止めるか、止めていたなら解く。
    /// </summary>
    /// <param name="state">いまの状態。</param>
    /// <param name="monotonic">いまの単調時刻。</param>
    /// <returns>次の状態。</returns>
    public static RotationState TogglePause(RotationState state, TimeSpan monotonic)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.PausedUntil is null
            ? state with { PausedUntil = monotonic + PauseLimit }
            : state with { PausedUntil = null, Deadline = monotonic + Duration(state.Current) };
    }

    private static RotationState MoveNext(RotationState state, TimeSpan monotonic, IReadOnlyList<DisplaySlide> active) =>
        Move(state, monotonic, active, forward: true);

    private static RotationState Move(
        RotationState state,
        TimeSpan monotonic,
        IReadOnlyList<DisplaySlide> active,
        bool forward)
    {
        var index = Array.IndexOf(Order, state.Current);
        var direction = forward ? 1 : -1;
        for (var step = 1; step <= Order.Length; step++)
        {
            // 剰余は負になりうるため、長さを足してから取る
            var candidate = Order[((index + (direction * step)) % Order.Length + Order.Length) % Order.Length];
            if (active.Contains(candidate))
            {
                return state with { Current = candidate, Deadline = monotonic + Duration(candidate) };
            }
        }

        // 出してよいスライドは必ず1枚以上ある
        return state;
    }
}
