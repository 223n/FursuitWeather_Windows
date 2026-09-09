using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Changes;

/// <summary>出した通知の記録。抑制の判断に使う。</summary>
/// <param name="Kind">種類。</param>
/// <param name="At">出した時刻。</param>
/// <param name="Signature">内容の署名。同じ署名なら再び出さない。</param>
public sealed record NotificationRecord(NotificationKind Kind, DateTimeOffset At, string Signature);

/// <summary>通知の判断に使う、前回までの状態。</summary>
/// <remarks>
/// アプリの再起動をまたいで保存する。
/// 保存できていなくても、基準を張り直して黙るだけで害は出ない。
/// </remarks>
public sealed record ChangeState
{
    /// <summary>この状態を保存した時刻。</summary>
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>地点の識別子。変わったら基準を張り直す。</summary>
    public string LocationKey { get; init; } = string.Empty;

    /// <summary>基準にしたレスポンスの生成時刻。</summary>
    public DateTimeOffset BaselineGeneratedAt { get; init; }

    /// <summary>前回みた連続活動時間（分）。</summary>
    public int LastMinutes { get; init; }

    /// <summary>前回みたレベルID。</summary>
    public string LastLevel { get; init; } = string.Empty;

    /// <summary>前回みた補正後のWBGT（℃）。</summary>
    public double LastSuitWbgt { get; init; }

    /// <summary>着用中止を知らせたときの補正後のWBGT（℃）。回復の判定に使う。</summary>
    public double? DiscontinuedSuitWbgt { get; init; }

    /// <summary>公式のアラートが出ている状態か。</summary>
    public bool AlertActive { get; init; }

    /// <summary>出した通知の履歴。</summary>
    public IReadOnlyList<NotificationRecord> History { get; init; } = [];

    /// <summary>いまの予報から基準を作り直す。</summary>
    /// <param name="forecast">予報。</param>
    /// <param name="hour">基準にする時間。</param>
    /// <param name="alertActive">公式のアラートが出ているか。</param>
    /// <param name="locationKey">地点の識別子。</param>
    /// <param name="now">いまの時刻。</param>
    /// <param name="history">引き継ぐ履歴。</param>
    /// <param name="discontinuedSuitWbgt">
    /// 引き継ぐ、着用中止を知らせたときの補正後のWBGT。
    /// 地点が変わったときは引き継がない。
    /// </param>
    /// <returns>張り直した状態。</returns>
    public static ChangeState Rebase(
        ForecastResponse forecast,
        HourForecast? hour,
        bool alertActive,
        string locationKey,
        DateTimeOffset now,
        IReadOnlyList<NotificationRecord>? history = null,
        double? discontinuedSuitWbgt = null)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        return new ChangeState
        {
            SavedAt = now,
            LocationKey = locationKey,
            BaselineGeneratedAt = forecast.GeneratedAt,
            LastMinutes = hour?.Outdoor.ActivityMinutes ?? 0,
            LastLevel = hour?.Outdoor.Level ?? string.Empty,
            LastSuitWbgt = hour?.Outdoor.SuitWbgt ?? 0d,
            DiscontinuedSuitWbgt = discontinuedSuitWbgt,
            AlertActive = alertActive,
            History = history ?? [],
        };
    }
}
