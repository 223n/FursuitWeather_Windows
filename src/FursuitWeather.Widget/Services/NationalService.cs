using System.Net.Http;
using System.Windows.Threading;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Display;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Polling;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 全国の天気を取りに行く。
/// </summary>
/// <remarks>
/// <para>
/// <b>取るのは掲示のあいだだけである。</b>
/// 小窓は全国の天気を使わない。
/// 1回の呼び出しが本体の側で都市の数だけ上流へ広がるため、出していないあいだは取らない。
/// </para>
/// <para>
/// 失敗しても手元の内容は捨てない。取り直せるまで前回の都市を出し続ける。
/// 日付が変わったらその場で取り直す。待つと、日付の変わり目に最長31分「取得できていません」と出続ける。
/// </para>
/// </remarks>
public sealed class NationalService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly FursuitWeatherClient _client;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _tick;
    private readonly Random _jitter = new();

    private PollingSchedule _schedule = new();
    private DateOnly _lastRequestedDate;
    private bool _busy;
    private bool _disposed;

    /// <summary>取得に成功したときに起きる。</summary>
    public event EventHandler? Updated;

    /// <summary>手元にある全国の天気。まだ無ければ null。</summary>
    public NationalResponse? Latest { get; private set; }

    /// <summary>直近の取得に失敗したか。</summary>
    public bool LastAttemptFailed { get; private set; }

    /// <summary>作る。</summary>
    public NationalService()
    {
        _http = FursuitWeatherClient.CreateHttpClient();
        _client = new FursuitWeatherClient(_http);
        _tick = new DispatcherTimer { Interval = TickInterval };
        _tick.Tick += async (_, _) => await PumpAsync().ConfigureAwait(true);
    }

    /// <summary>動かし始める。すぐに1回取りに行く。</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _tick.Start();
        _ = PumpAsync();
    }

    /// <summary>止める。手元の内容は残す。</summary>
    public void Stop() => _tick.Stop();

    /// <summary>
    /// いまの日付で出せる内容か。
    /// </summary>
    /// <param name="now">いまの時刻。</param>
    /// <returns>出してよければ true。</returns>
    /// <remarks>
    /// 対象日が今日でない応答は出さない。
    /// 0時をまたいだ直後に、昨日の天気を今日として掲げないためである。
    /// </remarks>
    public bool IsUsable(DateTimeOffset now) =>
        Latest is { } latest &&
        DateOnly.TryParse(latest.Date, System.Globalization.CultureInfo.InvariantCulture, out var date) &&
        date == DisplayForecast.Today(now);

    private async Task PumpAsync()
    {
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        try
        {
            var wallClock = DateTimeOffset.UtcNow;
            var monotonic = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var today = DisplayForecast.Today(wallClock);

            // 日付が変わったら、間隔を待たずに取り直す
            if (today != _lastRequestedDate)
            {
                _schedule = _schedule with { LastSuccessWallClock = null, LastSuccessMonotonic = null };
            }

            if (!_schedule.IsDue(PollInterval.National, wallClock, monotonic))
            {
                return;
            }

            _lastRequestedDate = today;

            try
            {
                Latest = await _client.GetNationalAsync().ConfigureAwait(true);
                _schedule = _schedule.Succeeded(wallClock, monotonic);
                LastAttemptFailed = false;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // 1都市も取れなければAPIは502を返す。前回の都市はそのまま出し続ける
                _schedule = _schedule.Failed(wallClock, monotonic, _jitter.NextDouble());
                LastAttemptFailed = true;
            }

            Updated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tick.Stop();
        _http.Dispose();
    }
}
