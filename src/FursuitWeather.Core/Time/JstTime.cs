using System.Globalization;

namespace FursuitWeather.Core.Time;

/// <summary>
/// APIが返すタイムゾーンなしの日本時間を扱う。
/// </summary>
/// <remarks>
/// <para>
/// <c>hours[].time</c> は <c>2026-08-15T09:00</c> のような、オフセットを持たない文字列である。
/// これを <see cref="DateTimeOffset"/> で受けてはならない。
/// <see cref="DateTimeOffset"/> はオフセットが無いときに実行するマシンのローカルの値を補うため、
/// 日本時間の開発機では正しく動き、UTCのCIでだけ9時間ずれる。
/// テストが通ってしまう種類の不具合になる。
/// </para>
/// <para>
/// そのため <see cref="DateTime"/>（<see cref="DateTimeKind.Unspecified"/>）で受け、
/// 使う直前にこのクラスで日本時間として明示的に解釈する。
/// </para>
/// </remarks>
public static class JstTime
{
    /// <summary>日本時間のタイムゾーン。</summary>
    /// <remarks>
    /// Windows は <c>Tokyo Standard Time</c>、それ以外は <c>Asia/Tokyo</c> を使う。
    /// .NET 8 以降の Windows は IANA 名も解決できるが、
    /// ICU を使わない構成に備えて両方を試す。
    /// </remarks>
    public static TimeZoneInfo Tokyo { get; } = ResolveTokyo();

    /// <summary>APIが返す時刻の書式。</summary>
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
    ];

    /// <summary>
    /// タイムゾーンなしの日本時間の文字列を <see cref="DateTime"/> として読む。
    /// </summary>
    /// <param name="value">APIが返した時刻の文字列。</param>
    /// <returns>
    /// 読めたときは <see cref="DateTimeKind.Unspecified"/> の <see cref="DateTime"/>。
    /// 読めなければ null。
    /// </returns>
    public static DateTime? ParseLocal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value,
            Formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// タイムゾーンなしの日本時間の文字列を、絶対時刻へ変換する。
    /// </summary>
    /// <param name="value">APIが返した時刻の文字列。</param>
    /// <returns>読めたときは日本時間として解釈した絶対時刻。読めなければ null。</returns>
    public static DateTimeOffset? ToInstant(string? value)
    {
        var local = ParseLocal(value);
        return local is null ? null : ToInstant(local.Value);
    }

    /// <summary>
    /// 日本時間として解釈して絶対時刻へ変換する。
    /// </summary>
    /// <param name="local">タイムゾーンを持たない日本時間。</param>
    /// <returns>絶対時刻。</returns>
    public static DateTimeOffset ToInstant(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = Tokyo.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    /// <summary>
    /// 絶対時刻を日本時間の壁時計へ写す。
    /// </summary>
    /// <param name="instant">絶対時刻。</param>
    /// <returns>タイムゾーンを持たない日本時間。</returns>
    public static DateTime ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Tokyo).DateTime;

    private static TimeZoneInfo ResolveTokyo()
    {
        foreach (var id in (string[])["Asia/Tokyo", "Tokyo Standard Time"])
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // 次の候補を試す
            }
            catch (InvalidTimeZoneException)
            {
                // 次の候補を試す
            }
        }

        // どちらも解決できない環境では、日本標準時が固定で +09:00 であることに頼る。
        // 日本は現在サマータイムを採用していない。
        return TimeZoneInfo.CreateCustomTimeZone("FursuitWeather.Jst", TimeSpan.FromHours(9), "JST", "JST");
    }
}
