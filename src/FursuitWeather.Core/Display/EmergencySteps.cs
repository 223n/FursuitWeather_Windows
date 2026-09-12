using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Display;

/// <summary>もしものときの1つの手順。</summary>
/// <param name="Title">見出し。</param>
/// <param name="Detail">中身。</param>
public sealed record EmergencyStep(string Title, string Detail);

/// <summary>
/// 熱中症の応急対応の要点。
/// </summary>
/// <remarks>
/// <para>
/// <b>文は本体の <c>public/display.js</c> の <c>renderEmergencySlide</c> から写した。</b>
/// 本体の文が変わったら、ここも直すこと。
/// 本体の <c>/emergency</c> のページは別の文なので、同期の相手にしない。
/// </para>
/// <para>
/// いまは本体のWeb、Mac版、Windows版の3か所に写しがあり、ずれても機械では気付けない。
/// 1か所に置いて配ることを、本体へ提案する予定である（<c>docs/display.md</c>）。
/// </para>
/// </remarks>
public static class EmergencySteps
{
    /// <summary>5つの手順。</summary>
    public static IReadOnlyList<EmergencyStep> Items { get; } =
    [
        new("意識を確認", "反応がない・おかしいときは、ためらわず119番通報"),
        new("着ぐるみから出す", "ヘッド → ハンド → ジッパーの順で脱がせる"),
        new("涼しい場所へ", "冷房の効いた室内か、風通しのよい日陰へ"),
        new("体を冷やす", "首・脇の下・足の付け根を冷却。水をかけてあおぐ"),
        new("水分・塩分", "意識がはっきりして自力で飲めるときだけ少しずつ"),
    ];

    /// <summary>詳しい手順への案内。</summary>
    public const string MoreInfo = "詳しい手順: fursuit-weather.223n.tech/emergency";

    /// <summary>
    /// もしものときを加えるか。
    /// </summary>
    /// <param name="current">いまの時間の行。無ければ null。</param>
    /// <returns>加えるなら true。</returns>
    /// <remarks>
    /// 本体と同じく、いまの屋外判定の <c>grade</c> が3以上で、低温ではないときに加える。
    /// 低温の <c>grade 4</c> に熱中症の手順を出すのは誤誘導になる。
    /// 判定が無いときは加えない。
    /// </remarks>
    public static bool ShouldShow(HourForecast? current) =>
        current is not null &&
        current.Outdoor.Grade >= 3 &&
        !DisplayTone.IsColdLevel(current.Outdoor.Level);
}
