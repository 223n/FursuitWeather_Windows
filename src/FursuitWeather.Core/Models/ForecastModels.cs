using System.Text.Json.Serialization;

namespace FursuitWeather.Core.Models;

/// <summary>予報地点。気象モデルの格子点へ丸められた座標。</summary>
/// <param name="Latitude">緯度。</param>
/// <param name="Longitude">経度。</param>
/// <param name="Timezone">タイムゾーン。通常は Asia/Tokyo。</param>
public sealed record ForecastLocation(double Latitude, double Longitude, string Timezone);

/// <summary>気象データの出典表記。画面の見える位置へ必ず描く。</summary>
/// <param name="WeatherData">出典の名前。</param>
/// <param name="WeatherDataUrl">出典のURL。</param>
/// <param name="License">ライセンスの表記。</param>
public sealed record Attribution(string WeatherData, string WeatherDataUrl, string License);

/// <summary>1時間分の気象値。</summary>
public sealed record HourlyWeather
{
    /// <summary>ローカル時刻。タイムゾーンを持たない日本時間の文字列。</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>気温（℃）。</summary>
    public double Temperature { get; init; }

    /// <summary>相対湿度（%）。</summary>
    public double Humidity { get; init; }

    /// <summary>体感温度（℃）。</summary>
    public double ApparentTemperature { get; init; }

    /// <summary>降水量（mm）。</summary>
    public double Precipitation { get; init; }

    /// <summary>降水確率（%）。上流が提供しない場合と欠測時は null。</summary>
    public double? PrecipitationProbability { get; init; }

    /// <summary>WMOの天気コード。欠測時は -1。</summary>
    public int WeatherCode { get; init; }

    /// <summary>全天日射量（W/m²）。</summary>
    public double SolarRadiation { get; init; }

    /// <summary>風速（m/s）。</summary>
    public double WindSpeed { get; init; }

    /// <summary>天気コードが欠測かどうか。</summary>
    [JsonIgnore]
    public bool IsWeatherCodeMissing => WeatherCode < 0;
}

/// <summary>暑熱側と低温側を合わせた活動レベル。</summary>
public enum ActivityLevel
{
    /// <summary>API が知らない値を返したとき。</summary>
    Unknown = 0,

    /// <summary>ほぼ安全。</summary>
    Safe,

    /// <summary>注意。</summary>
    Caution,

    /// <summary>警戒。</summary>
    Warning,

    /// <summary>厳重警戒。</summary>
    Severe,

    /// <summary>危険。</summary>
    Danger,

    /// <summary>快適。低温側の基準。</summary>
    Optimal,

    /// <summary>低温注意。</summary>
    ColdCaution,

    /// <summary>低温警戒。</summary>
    ColdWarning,

    /// <summary>低温危険。</summary>
    ColdDanger,
}

/// <summary>冷房の要否。</summary>
public enum CoolingNeed
{
    /// <summary>API が知らない値を返したとき。</summary>
    Unknown = 0,

    /// <summary>不要。</summary>
    None,

    /// <summary>推奨。</summary>
    Recommended,

    /// <summary>必須。</summary>
    Required,
}

/// <summary>活動の判定。屋外と屋内で共通の形。</summary>
public record ActivityAssessment
{
    /// <summary>素のWBGT推定値（℃）。</summary>
    public double Wbgt { get; init; }

    /// <summary>着ぐるみの着衣補正を加えたあとのWBGT（℃）。</summary>
    public double SuitWbgt { get; init; }

    /// <summary>レベルID。API が返す文字列のまま保持する。</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>日本語のラベル。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>深刻度。0が快適で4が危険。</summary>
    public int Grade { get; init; }

    /// <summary>1回あたりの連続活動時間の目安（分）。0は着用中止。</summary>
    public int ActivityMinutes { get; init; }

    /// <summary>注意文。</summary>
    public string Advice { get; init; } = string.Empty;

    /// <summary>レベルIDを列挙へ写す。知らない値は <see cref="ActivityLevel.Unknown"/> になる。</summary>
    [JsonIgnore]
    public ActivityLevel LevelId => Level switch
    {
        "safe" => ActivityLevel.Safe,
        "caution" => ActivityLevel.Caution,
        "warning" => ActivityLevel.Warning,
        "severe" => ActivityLevel.Severe,
        "danger" => ActivityLevel.Danger,
        "optimal" => ActivityLevel.Optimal,
        "coldCaution" => ActivityLevel.ColdCaution,
        "coldWarning" => ActivityLevel.ColdWarning,
        "coldDanger" => ActivityLevel.ColdDanger,
        _ => ActivityLevel.Unknown,
    };
}

/// <summary>屋内の活動判定。冷房の要否が加わる。</summary>
public sealed record IndoorAssessment : ActivityAssessment
{
    /// <summary>冷房の要否。API が返す文字列のまま保持する。</summary>
    public string Cooling { get; init; } = string.Empty;

    /// <summary>冷房の要否の日本語ラベル。</summary>
    public string CoolingLabel { get; init; } = string.Empty;

    /// <summary>冷房の要否を列挙へ写す。知らない値は <see cref="CoolingNeed.Unknown"/> になる。</summary>
    [JsonIgnore]
    public CoolingNeed CoolingNeed => Cooling switch
    {
        "none" => CoolingNeed.None,
        "recommended" => CoolingNeed.Recommended,
        "required" => CoolingNeed.Required,
        _ => CoolingNeed.Unknown,
    };
}

/// <summary>1時間分の予報。</summary>
public sealed record HourForecast
{
    /// <summary>ローカル時刻。タイムゾーンを持たない日本時間の文字列。</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>気象値。</summary>
    public HourlyWeather Weather { get; init; } = new();

    /// <summary>天気の日本語ラベル。</summary>
    public string WeatherLabel { get; init; } = string.Empty;

    /// <summary>屋外の判定。</summary>
    public ActivityAssessment Outdoor { get; init; } = new();

    /// <summary>屋内の判定。</summary>
    public IndoorAssessment Indoor { get; init; } = new();
}

/// <summary>日別の代表的な判定。</summary>
public sealed record LevelSummary
{
    /// <summary>レベルID。</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>日本語のラベル。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>深刻度。</summary>
    public int Grade { get; init; }
}

/// <summary>洗濯の判定。</summary>
/// <remarks>
/// 掲示では文字だけを出す。
/// レベルから配色を引く表は、判定の <c>grade</c> 以外から配色を引くことになるため持たない。
/// </remarks>
public sealed record LaundryAssessment
{
    /// <summary>レベルID。</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>日本語のラベル。</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>1日分の予報のまとめ。</summary>
public sealed record DayForecast
{
    /// <summary>日付。YYYY-MM-DD の形。</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>最低気温（℃）。</summary>
    public double TemperatureMin { get; init; }

    /// <summary>最高気温（℃）。</summary>
    public double TemperatureMax { get; init; }

    /// <summary>日中の代表的な天気コード。</summary>
    public int WeatherCode { get; init; }

    /// <summary>天気の日本語ラベル。</summary>
    public string WeatherLabel { get; init; } = string.Empty;

    /// <summary>日の出の時刻。上流が提供しない場合と欠測時は null。</summary>
    public string? Sunrise { get; init; }

    /// <summary>日の入りの時刻。上流が提供しない場合と欠測時は null。</summary>
    public string? Sunset { get; init; }

    /// <summary>日中でもっとも厳しい屋外の判定。</summary>
    public LevelSummary OutdoorWorst { get; init; } = new();

    /// <summary>日中でもっとも穏やかな屋外の判定。</summary>
    public LevelSummary OutdoorBest { get; init; } = new();

    /// <summary>屋外の活動に向く時間帯。</summary>
    public IReadOnlyList<string> RecommendedHours { get; init; } = [];

    /// <summary>日中に冷房が必須となる時間があるか。</summary>
    public bool CoolingRequired { get; init; }

    /// <summary>その日の素のWBGTの最大値（℃）。</summary>
    public double MaxWbgt { get; init; }

    /// <summary>その日の最大風速（m/s）。</summary>
    public double MaxWindSpeed { get; init; }

    /// <summary>洗濯の判定。古いAPIや欠測では null。</summary>
    public LaundryAssessment? Laundry { get; init; }
}

/// <summary>急な暑さの注意。</summary>
public sealed record SuddenHeatWarning
{
    /// <summary>対象の日付。</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>直近7日の平均最高気温（℃）。</summary>
    public double RecentAverageMax { get; init; }

    /// <summary>対象日の最高気温（℃）。</summary>
    public double TargetMax { get; init; }
}

/// <summary><c>GET /api/forecast</c> のレスポンス。</summary>
public sealed record ForecastResponse
{
    /// <summary>予報地点。</summary>
    public ForecastLocation Location { get; init; } = new(0, 0, "Asia/Tokyo");

    /// <summary>レスポンスを作った時刻。ISO 8601 のUTC。</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>使った気象モデル。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>出典の表記。画面へ必ず描く。</summary>
    public Attribution Attribution { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>通年の注意事項。</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>急な暑さの注意。該当しないときと判定できないときは null。</summary>
    public SuddenHeatWarning? SuddenHeat { get; init; }

    /// <summary>
    /// 1時間ごとの予報。
    /// 欠測の時間は要素ごと落ちるため、1時間ごとの連続を保証しない。
    /// 添字を時刻とみなさず、<see cref="HourForecast.Time"/> で突き合わせること。
    /// </summary>
    public IReadOnlyList<HourForecast> Hours { get; init; } = [];

    /// <summary>日別のまとめ。</summary>
    public IReadOnlyList<DayForecast> Days { get; init; } = [];
}
