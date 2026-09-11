namespace FursuitWeather.Core.Display;

/// <summary>掲示の候補になるモニター。</summary>
/// <param name="Id">再起動や抜き差しのあとも同じであることを期待する識別の値。</param>
/// <param name="IsPrimary">主モニターか。</param>
public sealed record DisplayMonitor(string Id, bool IsPrimary);

/// <summary>掲示を出すモニターの決め方の結果。</summary>
/// <param name="Monitor">出すモニター。1台も無ければ null。</param>
/// <param name="FellBack">選んだモニターが見つからず、主モニターへ落としたか。</param>
public sealed record MonitorDecision(DisplayMonitor? Monitor, bool FellBack);

/// <summary>
/// 掲示を出すモニターを決める。
/// </summary>
/// <remarks>
/// モニターの一覧を読むのはUIの層で、ここは選ぶだけにする。
/// </remarks>
public static class MonitorChoice
{
    /// <summary>
    /// 選んだモニターがあればそれを、無ければ主モニターを選ぶ。
    /// </summary>
    /// <param name="preferredId">設定で選んだモニター。選んでいなければ null。</param>
    /// <param name="monitors">いまつながっているモニター。</param>
    /// <returns>決めた結果。</returns>
    public static MonitorDecision Resolve(string? preferredId, IReadOnlyList<DisplayMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            return new MonitorDecision(null, false);
        }

        if (!string.IsNullOrEmpty(preferredId) &&
            monitors.FirstOrDefault(m => string.Equals(m.Id, preferredId, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return new MonitorDecision(chosen, false);
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return new MonitorDecision(primary, !string.IsNullOrEmpty(preferredId));
    }
}
