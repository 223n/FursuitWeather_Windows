namespace FursuitWeather.Core.Display;

/// <summary>
/// 焼き付きを避けるために、描く位置を少しずつずらす。
/// </summary>
/// <remarks>
/// Mac版と同じく、3分ごとに1ずつ4か所を巡る。
/// 値は1280×720の組みの座標の上の量で、拡大すると実際のずれも大きくなる。
/// </remarks>
public static class BurnInShift
{
    /// <summary>ずらす間隔。</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(3);

    private static readonly (int X, int Y)[] Positions = [(0, 0), (1, 0), (1, 1), (0, 1)];

    /// <summary>いまのずれを引く。</summary>
    /// <param name="elapsed">掲示を始めてからの時間。</param>
    /// <returns>横と縦のずれ。</returns>
    public static (int X, int Y) Offset(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return Positions[0];
        }

        var step = (int)(elapsed.Ticks / Period.Ticks % Positions.Length);
        return Positions[step];
    }
}
