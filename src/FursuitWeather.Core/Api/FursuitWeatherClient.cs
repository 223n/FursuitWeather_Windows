using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Api;

/// <summary>本体のAPIを読むクライアント。</summary>
public sealed class FursuitWeatherClient
{
    /// <summary>本体の公開URL。</summary>
    public static readonly Uri DefaultBaseAddress = new("https://fursuit-weather.223n.tech/");

    /// <summary>
    /// 連絡先の分かるUser-Agent。
    /// 本体が上流に対して名乗っている形に揃える。
    /// </summary>
    public const string UserAgent = "FursuitWeather_Windows/1.0 (+https://github.com/223n/FursuitWeather_Windows)";

    /// <summary>
    /// HTTPのタイムアウト。
    /// Worker側が上流に10秒待ち、5xxのときに1回だけ再試行する。
    /// 10秒で切るとこの正常な復旧の経路を潰すため、余裕を取る。
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>読み取りに使う設定。知らないフィールドは無視する。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// クライアントを作る。
    /// </summary>
    /// <param name="http">
    /// 使い回す <see cref="HttpClient"/>。
    /// 呼び出しのたびに作り直すとポートが枯渇するため、
    /// 単一のインスタンスを保持して渡すこと。
    /// </param>
    public FursuitWeatherClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>
    /// 使い回す前提の <see cref="HttpClient"/> を作る。
    /// </summary>
    /// <param name="baseAddress">基点のURL。省略すると本体の公開URLを使う。</param>
    /// <returns>設定済みの <see cref="HttpClient"/>。</returns>
    /// <remarks>
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> を短く保ち、
    /// DNSの変更へ追随できるようにする。
    /// </remarks>
    public static HttpClient CreateHttpClient(Uri? baseAddress = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout = Timeout,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>地点の予報を取る。</summary>
    /// <param name="coordinate">座標。小数2桁へ丸められた値だけが送られる。</param>
    /// <param name="days">予報の日数。1から4まで。</param>
    /// <param name="cancellationToken">取り消しの合図。</param>
    /// <returns>予報。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> が範囲の外のとき。</exception>
    /// <exception cref="HttpRequestException">通信に失敗したとき。</exception>
    /// <exception cref="JsonException">レスポンスを読めなかったとき。</exception>
    public async Task<ForecastResponse> GetForecastAsync(
        Coordinate coordinate,
        int days = 4,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "予報の日数は1から4の範囲である必要があります。");
        }

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"api/forecast?lat={coordinate.LatitudeText}&lon={coordinate.LongitudeText}&days={days}");

        return await GetAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>デモデータを取る。気象APIへ接続できない環境の確認に使う。</summary>
    /// <param name="cancellationToken">取り消しの合図。</param>
    /// <returns>当日からの3日分の決まったデモデータ。</returns>
    public Task<ForecastResponse> GetDemoForecastAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/forecast?demo=1", cancellationToken);

    private async Task<ForecastResponse> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var forecast = await response.Content
            .ReadFromJsonAsync<ForecastResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return forecast ?? throw new JsonException("予報のレスポンスが空でした。");
    }
}
