using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 更新の状態を端末へ保存する。
/// </summary>
/// <remarks>
/// <para>
/// 設定とも通知の状態とも別のファイルにする。
/// 更新の途中で書かれる値が、ほかの値を巻き込まないようにするためである。
/// </para>
/// <para>
/// <b>読めなくても失敗として扱わない。</b>
/// 既定の状態から始め直すだけで、危険側へは倒れない。
/// </para>
/// </remarks>
public static class UpdateStateStore
{
    /// <summary>保存先。</summary>
    public static string FilePath { get; } = Path.Combine(WidgetSettings.Directory, "update-state.json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>保存してある状態を読む。</summary>
    /// <returns>読めた状態。無い、または壊れていれば既定の状態。</returns>
    public static UpdateState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UpdateState();
            }

            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(FilePath), Options) ?? new UpdateState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    /// <summary>
    /// 状態を書く。
    /// </summary>
    /// <param name="state">保存する状態。</param>
    /// <returns>書けたら true。</returns>
    /// <remarks>
    /// <para>
    /// いったん別名で書いてから置き換える。
    /// </para>
    /// <para>
    /// <b>インストールの直前の保存は、失敗したら先へ進んではいけない。</b>
    /// 狙いを書けないままインストーラーを起動すると、次の起動で成否を確定できない。
    /// そのため成否を返す。
    /// </para>
    /// </remarks>
    public static bool Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            System.IO.Directory.CreateDirectory(WidgetSettings.Directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
