using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FursuitWeather.Core.Api;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 端末に保存する設定。
/// </summary>
/// <remarks>
/// <para>
/// 保存先は <c>%LOCALAPPDATA%\FursuitWeather</c> の下に置く。
/// 利用者ごとの領域で、管理者権限を要さない。
/// </para>
/// <para>
/// 座標はここに持つ。本体のWeb版はGPSや地点の検索を持つが、
/// このクライアントはまだ設定画面を持たないため、既定は東京駅の周辺にしてある。
/// </para>
/// </remarks>
public sealed record WidgetSettings
{
    /// <summary>緯度。</summary>
    public double Latitude { get; init; } = DefaultLatitude;

    /// <summary>経度。</summary>
    public double Longitude { get; init; } = DefaultLongitude;

    /// <summary>地点の表示名。</summary>
    public string PlaceName { get; init; } = "東京駅の周辺";

    /// <summary>
    /// 利用者が地点を選んだか。
    /// </summary>
    /// <remarks>
    /// この印を持たない前の版の設定では null になる。
    /// 掲示は、選んでいない端末に「地点が設定されていません」と出す（<c>docs/display.md</c>）。
    /// </remarks>
    public bool? LocationChosen { get; init; }

    /// <summary>
    /// 地点を利用者が選んだとみなせるか。
    /// </summary>
    /// <remarks>
    /// 印を持たない設定では、座標が既定値と違えば選んだものとみなす。
    /// 前から地点を入れていた端末に、注意を誤って出さないためである。
    /// </remarks>
    [JsonIgnore]
    public bool HasChosenLocation => LocationChosen ?? !IsDefaultCoordinate;

    /// <summary>座標が既定値のままか。</summary>
    [JsonIgnore]
    private bool IsDefaultCoordinate =>
        Math.Abs(Latitude - DefaultLatitude) < double.Epsilon &&
        Math.Abs(Longitude - DefaultLongitude) < double.Epsilon;

    private const double DefaultLatitude = 35.68;
    private const double DefaultLongitude = 139.77;

    /// <summary>小窓の位置。まだ動かしていなければ null。</summary>
    public double? WindowLeft { get; init; }

    /// <summary>小窓の位置。まだ動かしていなければ null。</summary>
    public double? WindowTop { get; init; }

    /// <summary>小窓の高さ。</summary>
    public WindowLayer Layer { get; init; } = WindowLayer.AlwaysOnTop;

    /// <summary>トースト通知を使うか。</summary>
    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>Windowsへサインインしたときに自動で起動するか。</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>設定を置くディレクトリ。</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FursuitWeather");

    /// <summary>設定ファイルの場所。</summary>
    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 設定を読む。
    /// </summary>
    /// <returns>読めた設定。無い、または壊れていれば既定値。</returns>
    /// <remarks>
    /// 読めないことを失敗として扱わない。
    /// 設定が壊れていても、既定値で動き続けるほうが安全である。
    /// </remarks>
    public static WidgetSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new WidgetSettings();
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<WidgetSettings>(json, Options) ?? new WidgetSettings();
            return Sanitize(loaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WidgetSettings();
        }
    }

    /// <summary>
    /// 使える値かを確かめ、駄目なら既定へ落とす。
    /// </summary>
    /// <param name="settings">読んだ設定。</param>
    /// <returns>そのまま使える設定。</returns>
    /// <remarks>
    /// <para>
    /// JSONとして読めることと、値として使えることは別である。
    /// 範囲の外の座標はここを素通りし、<see cref="Coordinate"/> を作る時点で例外になる。
    /// それが起動の経路で起きると、小窓もトレイも出ないまま落ちる。
    /// 設定は起動のたびに読むため、以後この端末では毎回落ち続ける。
    /// </para>
    /// <para>
    /// 座標を既定へ戻すときは表示名も戻す。
    /// 「札幌」と出したまま東京の判定を見せるほうが危ない。
    /// 「利用者が選んだ」の印も落とす。落とさないと、掲示が既定の地点を選ばれたものとして掲げる。
    /// </para>
    /// </remarks>
    private static WidgetSettings Sanitize(WidgetSettings settings)
    {
        if (Coordinate.TryCreate(settings.Latitude, settings.Longitude, out _))
        {
            return settings;
        }

        var fallback = new WidgetSettings();
        return settings with
        {
            Latitude = fallback.Latitude,
            Longitude = fallback.Longitude,
            PlaceName = fallback.PlaceName,
            LocationChosen = false,
        };
    }

    /// <summary>
    /// 小窓の位置だけを書き直す。
    /// </summary>
    /// <param name="left">左端。</param>
    /// <param name="top">上端。</param>
    /// <remarks>
    /// ディスクの内容を読み直してから位置だけを差し替える。
    /// 手元のレコードで丸ごと上書きすると、
    /// 設定画面など別の経路で保存された変更を巻き戻してしまう。
    /// </remarks>
    public static void SaveWindowPosition(double left, double top) =>
        (Load() with { WindowLeft = left, WindowTop = top }).Save();

    /// <summary>
    /// 設定を書く。失敗しても本体は止めない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// いったん別名で書いてから置き換える。
    /// <c>WriteAllText</c> は先に中身を切り詰めるため、
    /// 電源断や強制終了と重なると途中で切れたJSONが残る。
    /// 次の起動でそれを読むと設定が丸ごと既定へ戻り、
    /// 札幌にいる利用者へ東京の判定を出すことになる。
    /// </para>
    /// <para>
    /// 小窓を動かすたびに書き直すため、切断の窓に当たる機会は少なくない。
    /// </para>
    /// </remarks>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても動き続ける
        }
    }
}
