using System.Diagnostics;
using System.Runtime.InteropServices;
using FursuitWeather.Core.Update;
using Windows.Networking.Connectivity;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 更新を自動で進めてよいかの判断に使う、端末の様子を読む。
/// </summary>
/// <remarks>
/// <para>
/// 判断そのものは <see cref="UpdateGate"/> が行う。
/// ここは Windows のAPIを呼んで値にするだけである。
/// </para>
/// <para>
/// <b>分からないときは安全側に倒す。</b>
/// 従量制課金かどうかが読めないときは、課金されるものとして扱う。
/// 大きな取得を黙って流すほうが、取得を見送るより害が大きい。
/// </para>
/// </remarks>
public static partial class UpdateEnvironment
{
    /// <summary>
    /// 取得するファイルがこれより大きければ、固定の上限つきの接続でも見送る。
    /// </summary>
    /// <remarks>
    /// インストーラーは170MBを超えるため、実質いつも見送る。
    /// </remarks>
    private const long FixedCostLimitBytes = 50L * 1024 * 1024;

    private const int QunsAcceptsNotifications = 5;

    private static readonly Stopwatch Started = Stopwatch.StartNew();

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>いまの様子を読む。</summary>
    /// <param name="downloadSize">これから取得するファイルの大きさ。分からなければ0。</param>
    /// <returns>ゲートへ渡す値。</returns>
    public static UpdateConditions Read(long downloadSize) => new()
    {
        IsMetered = IsMetered(downloadSize),
        IsOnBattery = IsOnBattery(),
        AcceptsNotifications = AcceptsNotifications(),
        Uptime = Started.Elapsed,
    };

    /// <summary>
    /// 従量制課金とみなすべき接続か。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 公式の実装ノートが「大きな転送の直前にコストを再評価せよ」としている。
    /// 保留を解く判定のときではなく、実際に通信を投げる直前に呼ぶこと。
    /// </para>
    /// <para>
    /// <c>GetInternetConnectionProfile()</c> は null を返しうる。
    /// そのときは安全側に倒し、課金されるものとして扱う。
    /// </para>
    /// </remarks>
    private static bool IsMetered(long downloadSize)
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return true;
            }

            var cost = profile.GetConnectionCost();
            if (cost.Roaming || cost.OverDataLimit || cost.BackgroundDataUsageRestricted)
            {
                return true;
            }

            return cost.NetworkCostType switch
            {
                NetworkCostType.Unrestricted => false,
                NetworkCostType.Variable => true,
                NetworkCostType.Fixed => cost.ApproachingDataLimit || downloadSize > FixedCostLimitBytes || downloadSize <= 0,

                // 知らない値は、課金されるものとして扱う
                _ => true,
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 電池で動いているか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows App SDK の <c>PowerManager</c> を使う。すでに依存しているため追加の費用が無い。
    /// </para>
    /// <para>
    /// 省電力の状態だけには頼らない。
    /// Windows 11 24H2 以降で省電力のモデルが変わり、APIが追いついていない可能性が指摘されている。
    /// 電源の種類を主に見る。
    /// </para>
    /// <para>
    /// 読めないときは「電池ではない」とする。
    /// この値で見送るのは取得だけであり、読めない端末で更新を永久に止めるほうが害が大きい。
    /// </para>
    /// </remarks>
    private static bool IsOnBattery()
    {
        try
        {
            return Microsoft.Windows.System.Power.PowerManager.PowerSourceKind ==
                Microsoft.Windows.System.Power.PowerSourceKind.DC;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or TypeInitializationException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 割り込んでよい状態か。
    /// </summary>
    /// <remarks>
    /// 全画面、プレゼンテーション、ロック、応答不可の時間帯などで偽になる。
    /// 読めないときは偽とする。インストールを見送るだけで、害が小さい側である。
    /// </remarks>
    private static bool AcceptsNotifications()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state == QunsAcceptsNotifications;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
