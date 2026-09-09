using System.Globalization;
using FursuitWeather.Core.Api;

namespace FursuitWeather.Core.Tests;

/// <summary>
/// 座標の丸めを見る。
/// </summary>
/// <remarks>
/// 本体は座標を小数2桁へ丸めて扱うと公開している。
/// Cloudflareのinvocation logがクエリ文字列を丸めずに記録するため、
/// 丸めずに送るとその約束が破れる。
/// </remarks>
public sealed class CoordinateTests
{
    [Theory]
    [InlineData(35.681236, 139.767125, "35.68", "139.77")]
    [InlineData(-33.868820, 151.209290, "-33.87", "151.21")]
    [InlineData(0, 0, "0", "0")]
    public void 小数2桁へ丸める(double lat, double lon, string expectedLat, string expectedLon)
    {
        var coordinate = new Coordinate(lat, lon);

        Assert.Equal(expectedLat, coordinate.LatitudeText);
        Assert.Equal(expectedLon, coordinate.LongitudeText);
    }

    [Theory]
    // ちょうど半分に見える値は、倍精度では厳密に半分ではない。
    // 139.765 は 139.76499999999999 のため、切り上げにはならない。
    [InlineData(139.765, "139.76")]
    // こちらは倍精度でも半分より大きい側に来るため、繰り上がる。
    [InlineData(139.775, "139.78")]
    public void 見かけ上の中間値は倍精度の表現に従う(double lon, string expected)
    {
        Assert.Equal(expected, new Coordinate(0, lon).LongitudeText);
    }

    [Theory]
    // 本体の round2 は Math.round(value * 100) / 100 で、
    // ちょうど半分のときは正の無限大の側へ丸める。
    // .NET の MidpointRounding.AwayFromZero だと -33.88 になり、本体と食い違う。
    [InlineData(-33.875, "-33.87")]
    [InlineData(-0.125, "-0.12")]
    public void 負の中間値は本体と同じ向きへ丸める(double lat, string expected)
    {
        Assert.Equal(expected, new Coordinate(lat, 0).LatitudeText);
    }

    [Fact]
    public void 小数点の記号が文化圏に依存しない()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 小数点にカンマを使う文化圏でも、クエリ文字列は必ずピリオドにする
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var coordinate = new Coordinate(35.68, 139.77);

            Assert.Equal("35.68", coordinate.LatitudeText);
            Assert.Equal("139.77", coordinate.LongitudeText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(90.1, 0)]
    [InlineData(-90.1, 0)]
    [InlineData(double.NaN, 0)]
    public void 範囲の外の緯度は拒む(double lat, double lon)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Coordinate(lat, lon));
    }

    [Theory]
    [InlineData(0, 180.1)]
    [InlineData(0, -180.1)]
    [InlineData(0, double.NaN)]
    public void 範囲の外の経度は拒む(double lat, double lon)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Coordinate(lat, lon));
    }

    [Fact]
    public void 境界の値は受け入れる()
    {
        _ = new Coordinate(90, 180);
        _ = new Coordinate(-90, -180);
        Assert.True(true);
    }

    [Theory]
    [InlineData(35.68, 139.77)]
    [InlineData(-90d, -180d)]
    [InlineData(90d, 180d)]
    public void 範囲の中なら例外を出さずに作れる(double latitude, double longitude)
    {
        Assert.True(Coordinate.TryCreate(latitude, longitude, out var coordinate));
        Assert.Equal(Math.Round(latitude, 2), coordinate.Latitude);
    }

    [Theory]
    [InlineData(355.68, 139.77)]
    [InlineData(35.68, 1399.77)]
    [InlineData(double.NaN, 139.77)]
    [InlineData(double.PositiveInfinity, 139.77)]
    public void 範囲の外なら偽を返して例外を投げない(double latitude, double longitude)
    {
        // 端末に保存された設定は利用者が編集できる。
        // ここで例外を投げると、起動の経路で小窓もトレイも出ないまま落ちる
        Assert.False(Coordinate.TryCreate(latitude, longitude, out var coordinate));
        Assert.Equal(default, coordinate);
    }
}
