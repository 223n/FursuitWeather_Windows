using System.Globalization;

namespace FursuitWeather.Core.Api;

/// <summary>
/// APIへ送る座標。
/// </summary>
/// <remarks>
/// <para>
/// 緯度と経度は小数2桁（およそ1km四方）へ丸めてから送る。
/// 本体はこの丸めを公開の約束としており、Cloudflareのinvocation logが
/// クエリ文字列を丸めずに記録するため、丸めずに送るとその約束が破れる。
/// </para>
/// <para>
/// 丸める処理はUIの層ではなく、この型を通すHTTPクライアントの層に置く。
/// </para>
/// </remarks>
public readonly record struct Coordinate
{
    /// <summary>緯度の下限。</summary>
    public const double MinLatitude = -90d;

    /// <summary>緯度の上限。</summary>
    public const double MaxLatitude = 90d;

    /// <summary>経度の下限。</summary>
    public const double MinLongitude = -180d;

    /// <summary>経度の上限。</summary>
    public const double MaxLongitude = 180d;

    /// <summary>小数2桁へ丸めた緯度。</summary>
    public double Latitude { get; }

    /// <summary>小数2桁へ丸めた経度。</summary>
    public double Longitude { get; }

    /// <summary>丸めたうえで範囲を検証する。</summary>
    /// <param name="latitude">緯度。</param>
    /// <param name="longitude">経度。</param>
    /// <exception cref="ArgumentOutOfRangeException">範囲の外か、数値でないとき。</exception>
    public Coordinate(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || latitude is < MinLatitude or > MaxLatitude)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "緯度は-90から90の範囲である必要があります。");
        }

        if (double.IsNaN(longitude) || longitude is < MinLongitude or > MaxLongitude)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "経度は-180から180の範囲である必要があります。");
        }

        Latitude = Round(latitude);
        Longitude = Round(longitude);
    }

    /// <summary>
    /// 例外を出さずに作る。
    /// </summary>
    /// <param name="latitude">緯度。</param>
    /// <param name="longitude">経度。</param>
    /// <param name="coordinate">作れたときの座標。</param>
    /// <returns>作れたら true。</returns>
    /// <remarks>
    /// 端末に保存された設定など、利用者が編集しうる値を通すときに使う。
    /// 範囲の外の値でそのまま構築すると例外になり、
    /// 起動の経路で投げると小窓もトレイも出ないまま落ちる。
    /// </remarks>
    public static bool TryCreate(double latitude, double longitude, out Coordinate coordinate)
    {
        try
        {
            coordinate = new Coordinate(latitude, longitude);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            coordinate = default;
            return false;
        }
    }

    /// <summary>クエリ文字列へ入れる形にする。</summary>
    /// <returns>小数2桁までの不変文化圏の表記。</returns>
    public string LatitudeText => Latitude.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>クエリ文字列へ入れる形にする。</summary>
    /// <returns>小数2桁までの不変文化圏の表記。</returns>
    public string LongitudeText => Longitude.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>小数2桁へ丸める。</summary>
    /// <remarks>
    /// <para>
    /// 本体の <c>src/logic/round.ts</c> の <c>round2</c> は <c>Math.round(value * 100) / 100</c> である。
    /// JavaScript の <c>Math.round</c> は「ちょうど半分のときは正の無限大の側へ」丸めるため、
    /// .NET の <see cref="MidpointRounding.AwayFromZero"/> とは負の値で結果が食い違う。
    /// </para>
    /// <para>
    /// 丸めた座標は上流のキャッシュのキーになる。
    /// 本体と違う値を送るとキャッシュが分かれてしまうため、同じ演算をなぞる。
    /// </para>
    /// </remarks>
    private static double Round(double value) => Math.Floor((value * 100d) + 0.5d) / 100d;

    /// <inheritdoc />
    public override string ToString() => $"{LatitudeText},{LongitudeText}";
}
