using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FursuitWeather.Core.Notifications;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 通知の判断に使う状態を、端末へ保存する。
/// </summary>
/// <remarks>
/// <para>
/// 設定とは別のファイルにする。
/// 設定は利用者が編集しうるもの、こちらはアプリが書き換え続ける作業用の値である。
/// 混ぜると、設定を消したいだけの人が基準まで消してしまう。
/// </para>
/// <para>
/// <b>読めなくても失敗として扱わない。</b>
/// 基準を失うと変化の検知は1回ぶん黙るだけで、危険側へは倒れない。
/// ここで例外を投げると、取得のたびにアプリが落ちる。
/// </para>
/// </remarks>
public static class NotificationStateStore
{
    /// <summary>保存先。</summary>
    public static string FilePath { get; } = Path.Combine(WidgetSettings.Directory, "notification-state.json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>保存してある状態を読む。</summary>
    /// <returns>読めた状態。無い、または壊れていれば空の状態。</returns>
    public static NotificationState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new NotificationState();
            }

            return JsonSerializer.Deserialize<NotificationState>(File.ReadAllText(FilePath), Options)
                ?? new NotificationState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new NotificationState();
        }
    }

    /// <summary>
    /// 状態を書く。
    /// </summary>
    /// <param name="state">保存する状態。</param>
    /// <remarks>
    /// いったん別名で書いてから置き換える。
    /// 書いている途中で電源が落ちても、途中まで書けたファイルを次回に読ませない。
    /// </remarks>
    public static void Save(NotificationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            System.IO.Directory.CreateDirectory(WidgetSettings.Directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても動き続ける。次の取得で基準を張り直すだけである
        }
    }
}
