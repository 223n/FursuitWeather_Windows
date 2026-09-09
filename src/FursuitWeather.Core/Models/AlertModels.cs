namespace FursuitWeather.Core.Models;

/// <summary>環境省の熱中症警戒アラートの発表。</summary>
/// <remarks>
/// 反映されるのは当日5時の発表分だけである。
/// 前日17時に出る翌日分の予告は含まれない。
/// </remarks>
public sealed record HeatAlert
{
    /// <summary>発表の対象になった都道府県の名前。</summary>
    /// <remarks>
    /// 県境の付近では隣の県と判定されることがあるため、画面には必ず県名を併記する。
    /// </remarks>
    public string PrefectureName { get; init; } = string.Empty;

    /// <summary>熱中症特別警戒アラートか。警戒より深刻な段階を指す。</summary>
    public bool Special { get; init; }

    /// <summary>対象の日付。</summary>
    public string TargetDate { get; init; } = string.Empty;
}

/// <summary><c>GET /api/alert</c> のレスポンス。</summary>
public sealed record AlertResponse
{
    /// <summary>
    /// 発表の内容。発表が無いとき、取得に失敗したとき、提供期間の外のときは null。
    /// </summary>
    /// <remarks>
    /// 上流の異常でも予報の表示を巻き込まないよう、APIは常に200で null を返す。
    /// クライアント側も「取れなければ発表なし」として扱う。
    /// </remarks>
    public HeatAlert? Alert { get; init; }
}
