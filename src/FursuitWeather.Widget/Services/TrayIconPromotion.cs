using System.IO;
using System.Security;
using Microsoft.Win32;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// トレイのアイコンを、利用者が表へ出しているかを読む。
/// </summary>
/// <remarks>
/// <para>
/// Windows 11 は、通知領域のアイコンごとに <c>HKCU\Control Panel\NotifyIconSettings</c> の下へキーを作り、
/// <c>ExecutablePath</c> と、表へ出したときに <c>IsPromoted</c>（1）を書く。
/// 開発機のレジストリで、この形のキーが実行ファイルごとに並ぶことを確かめた。
/// </para>
/// <para>
/// <b>文書化されていない場所である。</b>
/// 読めないときや形が変わったときは null を返し、呼び元は「出ていない」とみなす。
/// ここで分かるのは案内を省いてよいかだけで、アイコンを動かすことはできない。
/// </para>
/// </remarks>
public static class TrayIconPromotion
{
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";

    /// <summary>
    /// 指定の実行ファイルのアイコンが、表へ出ているかを返す。
    /// </summary>
    /// <param name="executablePath">実行ファイルの場所。</param>
    /// <returns>出ていれば true、出ていなければ false、読めなければ null。</returns>
    public static bool? IsPromoted(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (root is null)
            {
                return null;
            }

            var found = false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var icon = root.OpenSubKey(name);
                if (icon?.GetValue("ExecutablePath") is not string path ||
                    !string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                if (icon.GetValue("IsPromoted") is int promoted && promoted != 0)
                {
                    return true;
                }
            }

            // 初めての起動では、キーがまだ作られていないことがある
            return found ? false : null;
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
