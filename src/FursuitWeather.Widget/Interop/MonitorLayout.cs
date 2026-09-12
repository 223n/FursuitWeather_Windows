using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FursuitWeather.Core.Display;

namespace FursuitWeather.Widget.Interop;

/// <summary>掲示を出す先のモニター。</summary>
/// <param name="Id">再起動や抜き差しのあとも同じであることを期待する識別の値。</param>
/// <param name="Name">設定画面に出す名前。</param>
/// <param name="IsPrimary">主モニターか。</param>
/// <param name="Left">左端（物理ピクセル）。</param>
/// <param name="Top">上端（物理ピクセル）。</param>
/// <param name="Width">幅（物理ピクセル）。</param>
/// <param name="Height">高さ（物理ピクセル）。</param>
internal sealed record MonitorTarget(
    string Id,
    string Name,
    bool IsPrimary,
    int Left,
    int Top,
    int Width,
    int Height)
{
    /// <summary>Coreの選び方へ渡す形にする。</summary>
    /// <returns>Coreのモニター。</returns>
    public DisplayMonitor ToCore() => new(Id, IsPrimary);
}

/// <summary>
/// つながっているモニターを読み、窓をその全面へ置く。
/// </summary>
/// <remarks>
/// <para>
/// <b>WPFの <see cref="Window.Left"/> と <see cref="Window.Top"/> は使わない。</b>
/// Per-Monitor V2では壊れていることが分かっている（<c>docs/architecture.md</c>）。
/// 位置と大きさは物理ピクセルのまま <c>SetWindowPos</c> で当てる。
/// </para>
/// <para>
/// 列挙に <c>EnumDisplayMonitors</c> は使わない。
/// コールバックを渡す形は <c>LibraryImport</c> と噛み合わないためである。
/// <c>EnumDisplayDevices</c> と <c>EnumDisplaySettings</c> で、識別の値と位置と大きさが揃う。
/// </para>
/// </remarks>
internal static partial class MonitorLayout
{
    private const int EnumCurrentSettings = -1;

    /// <summary>モニターの識別の値（<c>\\?\DISPLAY#...</c>）を受け取る。</summary>
    private const uint EddGetDeviceInterfaceName = 0x0000_0001;

    private const uint AttachedToDesktop = 0x0000_0001;
    private const uint PrimaryDevice = 0x0000_0004;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TrueTypeOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;

        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DeviceMode devMode);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// つながっているモニターを読む。
    /// </summary>
    /// <returns>読めたモニター。読めなければ空。</returns>
    /// <remarks>デスクトップに割り当てられていないアダプターは飛ばす。</remarks>
    public static IReadOnlyList<MonitorTarget> Enumerate()
    {
        var targets = new List<MonitorTarget>();

        for (uint index = 0; ; index++)
        {
            var adapter = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & AttachedToDesktop) == 0)
            {
                continue;
            }

            var mode = new DeviceMode { Size = (ushort)Marshal.SizeOf<DeviceMode>() };
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode))
            {
                continue;
            }

            // 識別の値は、アダプター（\\.\DISPLAY1）ではなくモニターの側から取る。
            // アダプターの名前は抜き差しで入れ替わる
            var monitor = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            var hasMonitor = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EddGetDeviceInterfaceName);

            var id = hasMonitor && !string.IsNullOrEmpty(monitor.DeviceId) ? monitor.DeviceId : adapter.DeviceName;
            var name = hasMonitor && !string.IsNullOrEmpty(monitor.DeviceString) ? monitor.DeviceString : adapter.DeviceString;

            targets.Add(new MonitorTarget(
                id,
                $"{name}（{mode.PelsWidth}×{mode.PelsHeight}）",
                (adapter.StateFlags & PrimaryDevice) != 0,
                mode.PositionX,
                mode.PositionY,
                (int)mode.PelsWidth,
                (int)mode.PelsHeight));
        }

        return targets;
    }

    /// <summary>
    /// 窓をモニターの全面へ置く。
    /// </summary>
    /// <param name="window">置く窓。</param>
    /// <param name="target">置く先。</param>
    /// <remarks>
    /// <para>
    /// <c>WindowState.Maximized</c> は使わない。タスクバーの扱いを確かめていないためである。
    /// 前面の取り合いをしないよう、Z順には触らない。
    /// </para>
    /// <para>
    /// 出すのはここではない。出す前に置くために呼ぶため、表示の状態は変えない。
    /// </para>
    /// </remarks>
    public static void Place(Window window, MonitorTarget target)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            IntPtr.Zero,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SwpNoActivate | SwpNoZOrder);
    }
}
