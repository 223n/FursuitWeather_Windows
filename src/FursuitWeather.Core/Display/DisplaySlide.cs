namespace FursuitWeather.Core.Display;

/// <summary>掲示のスライド。</summary>
/// <remarks>
/// 並びは巡回の順である。
/// 本体の <c>public/display.js</c> の <c>SLIDES</c> と <c>EMERGENCY_SLIDE</c> と同じ順にする。
/// </remarks>
public enum DisplaySlide
{
    /// <summary>いまの判定。</summary>
    Now,

    /// <summary>この後の予報。</summary>
    Hours,

    /// <summary>3日間の天気。</summary>
    Days,

    /// <summary>全国の天気。</summary>
    National,

    /// <summary>もしものとき。条件を満たすあいだだけ最後に加える。</summary>
    Emergency,
}
