using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FursuitWeather.Widget.Interop;

/// <summary>
/// 小窓の見た目と振る舞いをWin32の側から整える。
/// </summary>
/// <remarks>
/// 根拠は <c>docs/open-questions.md</c> の「小窓の高さ」にある。
/// 最背面への固定は実測の結果、目的と正反対の挙動になると分かったため採らない。
/// </remarks>
internal static partial class WindowChrome
{
    private const int GwlExStyle = -20;

    /// <summary>フォーカスを奪わない。</summary>
    private const int WsExNoActivate = 0x0800_0000;

    /// <summary>タスクバーとAlt+Tabに出さない。</summary>
    private const int WsExToolWindow = 0x0000_0080;

    /// <summary>マウスの入力を素通しする。</summary>
    private const int WsExTransparent = 0x0000_0020;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// フォーカスを奪わず、タスクバーにも出ないようにする。
    /// </summary>
    /// <param name="window">対象のウィンドウ。</param>
    /// <remarks>
    /// <see cref="Window.SourceInitialized"/> より後に呼ぶこと。
    /// それより前ではハンドルがまだ無い。
    /// </remarks>
    public static void ApplyOverlayStyles(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>
    /// マウスの入力を素通しするかを切り替える。
    /// </summary>
    /// <param name="window">対象のウィンドウ。</param>
    /// <param name="enabled">素通しするなら true。</param>
    /// <remarks>
    /// <b>有効にしたウィンドウは、一切のマウス入力を受け付けない。</b>
    /// 解除する経路を必ず別に用意すること。
    /// トレイのメニューとグローバルホットキーの両方を勧める。
    /// </remarks>
    public static void SetClickThrough(Window window, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        var updated = enabled
            ? style | WsExTransparent
            : style & ~WsExTransparent;

        _ = SetWindowLong(handle, GwlExStyle, updated);
    }
}
