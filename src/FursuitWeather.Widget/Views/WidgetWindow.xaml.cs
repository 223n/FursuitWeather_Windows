using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FursuitWeather.Widget.Interop;

namespace FursuitWeather.Widget.Views;

/// <summary>
/// 壁紙の上に置く小窓。
/// </summary>
/// <remarks>
/// Step 1 の確認のための最小の実装。
/// 手で書いた値を描き、次の3つを実機で確かめる。
/// <list type="number">
/// <item>ClearType が無効になった状態の文字が読めるか</item>
/// <item>alpha が 0 の余白がクリックを下へ通し、カード面は掴めるか</item>
/// <item><c>app.manifest</c> の Per-Monitor V2 が効いているか</item>
/// </list>
/// </remarks>
public partial class WidgetWindow : Window
{
    private const int HotKeyId = 0xF001;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF = 0x46;
    private const int WmHotKey = 0x0312;

    private bool _clickThrough;

    /// <summary>小窓を作る。</summary>
    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = WidgetViewModel.CreateSample();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowChrome.ApplyOverlayStyles(this);
        PositionAtTopRight();

        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(OnWindowMessage);

        // クリックスルーを解除するための帯域外の経路。
        // 有効にした窓は一切のマウス入力を受け付けないため、これが無いと操作できなくなる
        if (!RegisterHotKey(handle, HotKeyId, ModControl | ModAlt | ModNoRepeat, VkF))
        {
            MessageBox.Show(
                this,
                "ホットキー（Ctrl+Alt+F）を登録できませんでした。\n" +
                "クリックスルーを有効にすると、解除できなくなる恐れがあります。",
                "FursuitWeather",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Closed += (_, _) => UnregisterHotKey(handle, HotKeyId);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            ToggleClickThrough();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>既定の位置は主モニターの右上。右下はトーストの出現位置と衝突する。</summary>
    private void PositionAtTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e) => ToggleClickThrough();

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        WindowChrome.SetClickThrough(this, _clickThrough);

        if (_clickThrough)
        {
            MessageBox.Show(
                this,
                "クリックスルーを有効にしました。\n" +
                "この状態では小窓はマウス入力を受け付けません。\n" +
                "Ctrl+Alt+F で解除できます。",
                "FursuitWeather",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnShowDiagnostics(object sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var handle = new WindowInteropHelper(this).Handle;

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"DPI の倍率: X={dpi.DpiScaleX:0.##} / Y={dpi.DpiScaleY:0.##}")
            .AppendLine(CultureInfo.InvariantCulture, $"1インチあたり: X={dpi.PixelsPerInchX:0} / Y={dpi.PixelsPerInchY:0}")
            .AppendLine(CultureInfo.InvariantCulture, $"ウィンドウの位置: Left={Left:0} / Top={Top:0}")
            .AppendLine(CultureInfo.InvariantCulture, $"作業領域: {SystemParameters.WorkArea}")
            .AppendLine(CultureInfo.InvariantCulture, $"ウィンドウハンドル: 0x{handle.ToInt64():X}")
            .AppendLine(CultureInfo.InvariantCulture, $"クリックスルー: {(_clickThrough ? "有効" : "無効")}")
            .AppendLine()
            .AppendLine("Per-Monitor V2 が効いているかは、タスクマネージャーの")
            .AppendLine("「詳細」タブで「DPI 認識」の列を出して確かめてください。")
            .Append("「システム拡張」ではなく「モニターごと (V2)」と出れば正しい状態です。")
            .ToString();

        MessageBox.Show(this, text, "検証に使う情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
