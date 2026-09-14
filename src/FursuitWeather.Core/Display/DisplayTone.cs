namespace FursuitWeather.Core.Display;

/// <summary>判定の配色の区分。</summary>
public enum LevelTone
{
    /// <summary><c>grade 0</c>。</summary>
    Grade0,

    /// <summary><c>grade 1</c>。</summary>
    Grade1,

    /// <summary><c>grade 2</c>。</summary>
    Grade2,

    /// <summary><c>grade 3</c>。</summary>
    Grade3,

    /// <summary><c>grade 4</c>。</summary>
    Grade4,

    /// <summary>低温。レベルの名前が <c>cold</c> で始まるもの。</summary>
    Cold,
}

/// <summary>
/// 判定から配色と記号を引く。
/// </summary>
/// <remarks>
/// <para>
/// クライアントに残してよい4項目のうち「<c>grade</c> とレベルの名前から配色と記号を引く」に当たる。
/// 判定そのものは複製しない。
/// </para>
/// <para>
/// <b>配色に使う低温は、名前が <c>cold</c> で始まるものだけにする。</b>
/// 本体のWebとMac版と同じである。
/// <see cref="Changes.LevelClassification.IsCold(Models.ActivityLevel)"/> は <c>optimal</c> も低温側に含む分類で、
/// 通知の向き（悪化と回復）を決めるためのものである。こちらとは使い分ける。
/// </para>
/// </remarks>
public static class DisplayTone
{
    /// <summary>低温の印。</summary>
    /// <remarks>本体のバッジが低温のときだけ前へ置く、温度計のアイコンに当たる。</remarks>
    public const string ColdSymbol = "❄";

    /// <summary>
    /// <c>grade</c> ごとの記号。
    /// </summary>
    /// <remarks>
    /// 本体の <c>GRADE_SYMBOLS</c> と同じ並びだが、<c>grade 4</c> だけ違う。
    /// 本体は <c>ban</c> のアイコンを使い、<c>grade 3</c> と別の形にしている。
    /// こちらはアイコンをまだ持たないため、どちらも「✕」になる。
    /// 見分けはラベルの文字（厳重警戒と着用中止）が担う。
    /// アイコンを入れる段階で分ける（<c>docs/open-questions.md</c>）。
    /// </remarks>
    private static readonly string[] GradeSymbols = ["◎", "○", "△", "✕", "✕"];

    /// <summary>配色に使う低温か。</summary>
    /// <param name="level">レベルの名前。</param>
    /// <returns>名前が <c>cold</c> で始まれば true。</returns>
    public static bool IsColdLevel(string? level) =>
        level is not null && level.StartsWith("cold", StringComparison.Ordinal);

    /// <summary>配色の区分を引く。</summary>
    /// <param name="level">レベルの名前。</param>
    /// <param name="grade">深刻度。範囲の外は0から4へ丸める。</param>
    /// <returns>配色の区分。</returns>
    public static LevelTone From(string? level, int grade) =>
        IsColdLevel(level) ? LevelTone.Cold : (LevelTone)Math.Clamp(grade, 0, 4);

    /// <summary><c>grade</c> の記号を引く。</summary>
    /// <param name="grade">深刻度。範囲の外は0から4へ丸める。</param>
    /// <returns>記号。</returns>
    public static string GradeSymbol(int grade) => GradeSymbols[Math.Clamp(grade, 0, 4)];

    /// <summary>
    /// バッジに出す記号を引く。
    /// </summary>
    /// <param name="level">レベルの名前。</param>
    /// <param name="grade">深刻度。</param>
    /// <returns>記号。低温なら印に <c>grade</c> の記号を添える。</returns>
    /// <remarks>
    /// <b>低温でも <c>grade</c> の記号を落とさない。</b>
    /// 落とすと、低温の注意と警戒と危険が同じ記号になり、文字でしか見分けられなくなる。
    /// 本体のバッジも、低温の印と <c>grade</c> の記号を並べている。
    /// </remarks>
    public static string Symbol(string? level, int grade) =>
        IsColdLevel(level) ? ColdSymbol + GradeSymbol(grade) : GradeSymbol(grade);
}
