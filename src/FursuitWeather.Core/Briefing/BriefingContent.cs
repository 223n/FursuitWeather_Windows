using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Briefing;

/// <summary>朝のブリーフィングの中身。</summary>
/// <remarks>
/// 文面の組み立てとトーストの発行はUIの層が行う。
/// ここは「何を伝えるか」までを決める。
/// </remarks>
public sealed record BriefingContent
{
    /// <summary>対象の日付（日本時間）。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>日中でもっとも厳しい屋外の判定。</summary>
    public required LevelSummary Worst { get; init; }

    /// <summary>最高気温（℃）。</summary>
    public double TemperatureMax { get; init; }

    /// <summary>最低気温（℃）。</summary>
    public double TemperatureMin { get; init; }

    /// <summary>
    /// 屋外の活動に適した時間帯。
    /// </summary>
    /// <remarks>
    /// 過ぎた時間帯は落としてある。
    /// 遅れて出したときに、もう来ない時間を勧めないようにするためである。
    /// </remarks>
    public required IReadOnlyList<string> RecommendedHours { get; init; }

    /// <summary>日中に冷房が必須となる時間があるか。</summary>
    public bool CoolingRequired { get; init; }

    /// <summary>急な暑さの注意。該当しないときは null。</summary>
    public SuddenHeatWarning? SuddenHeat { get; init; }

    /// <summary>公式の熱中症警戒アラートが出ているか。</summary>
    public bool AlertActive { get; init; }

    /// <summary>低温側の判定か。熱中症の応急対応への導線を出すかの判断に使う。</summary>
    public bool IsCold { get; init; }

    /// <summary>
    /// 適した時間帯が1つも無いか。
    /// </summary>
    /// <remarks>
    /// 「今日は着られない」ことを、はっきり伝えるために使う。
    /// </remarks>
    public bool HasNoRecommendedHours => RecommendedHours.Count == 0;
}
