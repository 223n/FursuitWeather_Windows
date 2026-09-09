using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public double Latitude { get; init; } = 35.68;

    /// <summary>経度。</summary>
    public double Longitude { get; init; } = 139.77;

    /// <summary>地点の表示名。</summary>
    public string PlaceName { get; init; } = "東京駅の周辺";

    /// <summary>小窓の位置。まだ動かしていなければ null。</summary>
    public double? WindowLeft { get; init; }

    /// <summary>小窓の位置。まだ動かしていなければ null。</summary>
    public double? WindowTop { get; init; }

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
            return JsonSerializer.Deserialize<WidgetSettings>(json, Options) ?? new WidgetSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WidgetSettings();
        }
    }

    /// <summary>設定を書く。失敗しても本体は止めない。</summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても動き続ける
        }
    }
}
