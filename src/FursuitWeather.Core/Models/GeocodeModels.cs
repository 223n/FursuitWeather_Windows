namespace FursuitWeather.Core.Models;

/// <summary>地点の検索の1候補。</summary>
public sealed record GeocodeResult
{
    /// <summary>地名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>都道府県などの上位の区分。分からなければ空。</summary>
    public string Admin1 { get; init; } = string.Empty;

    /// <summary>緯度。</summary>
    public double Latitude { get; init; }

    /// <summary>経度。</summary>
    public double Longitude { get; init; }

    /// <summary>候補の一覧に出す名前。「名前（都道府県）」の形。</summary>
    /// <returns>表示用の名前。</returns>
    public string DisplayName() =>
        string.IsNullOrWhiteSpace(Admin1) || string.Equals(Admin1, Name, StringComparison.Ordinal)
            ? Name
            : $"{Name}（{Admin1}）";
}

/// <summary><c>GET /api/geocode</c> のレスポンス。</summary>
public sealed record GeocodeResponse
{
    /// <summary>候補。見つからなければ空。</summary>
    public IReadOnlyList<GeocodeResult> Results { get; init; } = [];
}
