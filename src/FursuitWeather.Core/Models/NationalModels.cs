namespace FursuitWeather.Core.Models;

/// <summary>全国の天気の1都市分。</summary>
public sealed record NationalCity
{
    /// <summary>都市名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>緯度。</summary>
    public double Latitude { get; init; }

    /// <summary>経度。</summary>
    public double Longitude { get; init; }

    /// <summary>日中の代表的な天気コード。</summary>
    public int WeatherCode { get; init; }

    /// <summary>天気の日本語ラベル。</summary>
    public string WeatherLabel { get; init; } = string.Empty;

    /// <summary>最低気温（℃）。</summary>
    public double TemperatureMin { get; init; }

    /// <summary>最高気温（℃）。</summary>
    public double TemperatureMax { get; init; }

    /// <summary>日中（9時から18時）でもっとも厳しい屋外の判定。</summary>
    public LevelSummary OutdoorWorst { get; init; } = new();
}

/// <summary><c>GET /api/national</c> のレスポンス。</summary>
/// <remarks>
/// <para>
/// 取れなかった都市は含まれない。
/// 1都市も取れなければAPIは502を返すため、0件の応答は来ない。
/// </para>
/// <para>
/// 都市の総数は返らない。
/// 欠けを数えるには本体の都市の一覧を写すことになるため、件数の欠けは扱わない。
/// </para>
/// </remarks>
public sealed record NationalResponse
{
    /// <summary>生成した時刻。</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>対象の日付。日本時間の当日で、YYYY-MM-DD の形。</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>使った気象モデル。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>出典。</summary>
    public Attribution Attribution { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>取れた都市。</summary>
    public IReadOnlyList<NationalCity> Cities { get; init; } = [];
}
