using System.Net.Http;
using System.Windows.Threading;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Models;
using FursuitWeather.Core.Polling;
using Microsoft.Win32;

namespace FursuitWeather.Widget.Services;

/// <summary>取得した内容。</summary>
/// <param name="Forecast">予報。</param>
/// <param name="Alert">公式の発表。無ければ null。</param>
/// <param name="RetrievedAt">取得した時刻。</param>
public sealed record ForecastSnapshot(ForecastResponse Forecast, HeatAlert? Alert, DateTimeOffset RetrievedAt);

/// <summary>
/// 予報と公式の発表を定期的に取りに行く。
/// </summary>
/// <remarks>
/// <para>
/// 間隔の判断は <see cref="PollingSchedule"/> に任せる。
/// ここが持つのは、時計を読むことと通信することだけである。
/// </para>
/// <para>
/// 30秒ごとのtickで様子を見て、間隔に達していれば取りに行く。
/// スリープからの復帰と回線の回復は「すぐ取り直すきっかけ」としてのみ使い、
/// 届かなくてもtickの周期で必ず復帰できるようにする。
/// </para>
/// </remarks>
public sealed class ForecastService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly FursuitWeatherClient _client;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _tick;
    private readonly Random _jitter = new();

    private Coordinate _coordinate;
    private PollingSchedule _forecastSchedule = new();
    private PollingSchedule _alertSchedule = new();
    private ForecastResponse? _forecast;
    private HeatAlert? _alert;
    private bool _busy;
    private bool _disposed;

    /// <summary>取得に成功したときに起きる。</summary>
    public event EventHandler<ForecastSnapshot>? Updated;

    /// <summary>取得に失敗したときに起きる。</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>いま失敗が続いている回数。</summary>
    public int ConsecutiveFailures => _forecastSchedule.ConsecutiveFailures;

    /// <summary>取りに行く相手を決めて作る。</summary>
    /// <param name="coordinate">地点。</param>
    public ForecastService(Coordinate coordinate)
    {
        _coordinate = coordinate;
        _http = FursuitWeatherClient.CreateHttpClient();
        _client = new FursuitWeatherClient(_http);
        _tick = new DispatcherTimer { Interval = TickInterval };
        _tick.Tick += async (_, _) => await PumpAsync().ConfigureAwait(true);
    }

    /// <summary>動かし始める。すぐに1回取りに行く。</summary>
    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _tick.Start();
        _ = PumpAsync();
    }

    /// <summary>いま取りに行く。間隔の判断を飛ばす。</summary>
    public void RefreshNow()
    {
        _forecastSchedule = _forecastSchedule.Resumed();
        _alertSchedule = _alertSchedule.Resumed();
        _forecastSchedule = _forecastSchedule with { LastSuccessWallClock = null, LastSuccessMonotonic = null };
        _ = PumpAsync();
    }

    /// <summary>地点を変える。</summary>
    /// <param name="coordinate">新しい地点。</param>
    public void ChangeLocation(Coordinate coordinate)
    {
        _coordinate = coordinate;
        _forecast = null;
        _alert = null;
        RefreshNow();
    }

    private async Task PumpAsync()
    {
        // 重ねて走らせない。前の取得が終わるまで待つ
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        try
        {
            var wallClock = DateTimeOffset.UtcNow;
            var monotonic = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var changed = false;

            if (_forecastSchedule.IsDue(PollInterval.Forecast, wallClock, monotonic))
            {
                try
                {
                    _forecast = await _client.GetForecastAsync(_coordinate).ConfigureAwait(true);
                    _forecastSchedule = _forecastSchedule.Succeeded(wallClock, monotonic);
                    changed = true;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    _forecastSchedule = _forecastSchedule.Failed(wallClock, monotonic, _jitter.NextDouble());
                    Failed?.Invoke(this, e);
                }
            }

            if (_alertSchedule.IsDue(PollInterval.Alert, wallClock, monotonic))
            {
                try
                {
                    _alert = (await _client.GetAlertAsync(_coordinate).ConfigureAwait(true)).Alert;
                    _alertSchedule = _alertSchedule.Succeeded(wallClock, monotonic);
                    changed = true;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    // 公式の発表はベストエフォート。取れなくても予報の表示は続ける
                    _alertSchedule = _alertSchedule.Failed(wallClock, monotonic, _jitter.NextDouble());
                }
            }

            if (changed && _forecast is not null)
            {
                Updated?.Invoke(this, new ForecastSnapshot(_forecast, _alert, wallClock));
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _forecastSchedule = _forecastSchedule.Resumed();
            _alertSchedule = _alertSchedule.Resumed();
            _ = PumpAsync();
        }
    }

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
        {
            return;
        }

        _forecastSchedule = _forecastSchedule.Resumed();
        _alertSchedule = _alertSchedule.Resumed();
        _ = PumpAsync();
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _http.Dispose();
    }
}
