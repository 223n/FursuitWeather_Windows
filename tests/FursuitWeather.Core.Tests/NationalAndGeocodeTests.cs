using System.Net;
using System.Text;
using System.Text.Json;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Tests;

/// <summary>全国の天気と地点の検索の読み取りと、呼び方を見る。</summary>
public sealed class NationalAndGeocodeTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (FursuitWeatherClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = FursuitWeatherClient.DefaultBaseAddress };
        return (new FursuitWeatherClient(http), handler);
    }

    // ---- 読み取り

    [Fact]
    public void 全国の天気の実物を読める()
    {
        var national = JsonSerializer.Deserialize<NationalResponse>(Fixture("national-demo.json"), FursuitWeatherClient.JsonOptions);

        Assert.NotNull(national);
        Assert.Equal(12, national.Cities.Count);
        Assert.Equal("札幌", national.Cities[0].Name);
        Assert.False(string.IsNullOrEmpty(national.Cities[0].OutdoorWorst.Level));
        Assert.False(string.IsNullOrEmpty(national.Attribution.WeatherData));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", national.Date);
    }

    [Fact]
    public void ミリ秒の付いた生成時刻を読める()
    {
        // Mac版はこの形を読めず、鮮度の注意が一度も出ない疑いがあった
        var national = JsonSerializer.Deserialize<NationalResponse>(Fixture("national-demo.json"), FursuitWeatherClient.JsonOptions);

        Assert.NotEqual(default, national!.GeneratedAt);
        Assert.NotEqual(0, national.GeneratedAt.Millisecond);
    }

    [Fact]
    public void 地点の検索の実物を読める()
    {
        var response = JsonSerializer.Deserialize<GeocodeResponse>(Fixture("geocode-matsuyama.json"), FursuitWeatherClient.JsonOptions);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Results);
        Assert.Equal("松山（愛媛県）", response.Results[0].DisplayName());
    }

    [Theory]
    [InlineData("松山", "", "松山")]
    [InlineData("東京都", "東京都", "東京都")]
    [InlineData("松山", "埼玉県", "松山（埼玉県）")]
    public void 候補の名前(string name, string admin1, string expected)
    {
        Assert.Equal(expected, new GeocodeResult { Name = name, Admin1 = admin1 }.DisplayName());
    }

    [Fact]
    public void 日ごとの予報から洗濯を読める()
    {
        var day = TestData.DemoForecast.Days[0];

        Assert.NotNull(day.Laundry);
        Assert.False(string.IsNullOrEmpty(day.Laundry.Label));
    }

    // ---- 呼び方

    [Fact]
    public async Task 全国の天気を取る()
    {
        var (client, handler) = Client(_ => Json(Fixture("national-demo.json")));

        var national = await client.GetNationalAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, national.Cities.Count);
        Assert.Equal("/api/national", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task 全国の天気が1都市も取れなければ例外にする()
    {
        // APIは0件の応答を返さず、502になる
        var (client, _) = Client(_ => Json("{\"error\":\"x\"}", HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetNationalAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 検索語は前後の空白を落として符号化して送る()
    {
        var (client, handler) = Client(_ => Json(Fixture("geocode-matsuyama.json")));

        var results = await client.SearchLocationsAsync("  松山 ", TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Equal("?q=%E6%9D%BE%E5%B1%B1", handler.Requests[0].Query);
    }

    [Fact]
    public async Task 記号を含む検索語も符号化する()
    {
        var (client, handler) = Client(_ => Json("{\"results\":[]}"));

        var results = await client.SearchLocationsAsync("a&b=c", TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Equal("?q=a%26b%3Dc", handler.Requests[0].Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空の検索語は送らない(string query)
    {
        var (client, handler) = Client(_ => Json("{\"results\":[]}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchLocationsAsync(query, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 長すぎる検索語は送らない()
    {
        var (client, handler) = Client(_ => Json("{\"results\":[]}"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchLocationsAsync(new string('あ', FursuitWeatherClient.MaxLocationQueryLength + 1), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 上限ちょうどの検索語は送る()
    {
        var (client, handler) = Client(_ => Json("{\"results\":[]}"));

        await client.SearchLocationsAsync(new string('あ', FursuitWeatherClient.MaxLocationQueryLength), TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    // ---- 公式の発表の種別の名前

    [Fact]
    public void 発表の種別の名前を1か所から引く()
    {
        Assert.Equal("熱中症警戒アラート", new HeatAlert().KindName);
        Assert.Equal("熱中症特別警戒アラート", new HeatAlert { Special = true }.KindName);
    }
}
