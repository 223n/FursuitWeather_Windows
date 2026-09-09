using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FursuitWeather.Core.Api;
using FursuitWeather.Widget.Interop;
using FursuitWeather.Widget.Services;
using H.NotifyIcon;

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
public partial class WidgetWindow : Window, IDisposable
{
    private const int HotKeyId = 0xF001;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF = 0x46;
    private const int WmHotKey = 0x0312;

    private readonly WidgetViewModel _viewModel = new();
    private ClickThroughGuard? _clickThrough;
    private ForecastService? _service;
    private readonly ToastNotifier _toast = new();
    private WidgetSettings _settings = WidgetSettings.Load();
    private bool _hotKeyRegistered;

    /// <summary>小窓を作る。</summary>
    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
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

        _clickThrough = new ClickThroughGuard(this);
        _clickThrough.Changed += (_, _) => UpdateTrayState();

        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);

        // 解除の経路その2。効かなくてもトレイと自動の復帰が残る
        _hotKeyRegistered = RegisterHotKey(handle, HotKeyId, ModControl | ModAlt | ModNoRepeat, VkF);

        UpdateTrayState();
        // NotificationInvoked を Register より先に付ける。順序を誤ると
        // 通知の処理のために新しいプロセスが起動する
        _toast.Initialize(_ => Dispatcher.Invoke(() => { Show(); Activate(); }));
        StartService();
        // 明示的に作る。作られていないとクリックスルーの解除の経路が1つ減る
        TrayIcon.ForceCreate();

        // 自動で戻る仕組みが本当に効くかを機械で確かめるためのスイッチ。
        // 起動と同時にクリックスルーを入れる。人が触らなくても猶予で戻ることを外から観測できる
        // 自動で戻る仕組みが効くかを、人が触らずに外から観測するためのスイッチ。
        // 起動と同時にクリックスルーを入れる
        var args = Environment.GetCommandLineArgs();

        if (args.Contains("--self-test-clickthrough", StringComparer.Ordinal))
        {
            _clickThrough.Enable();
        }

        // 通知が本当に出るかを、人が触らずに確かめるためのスイッチ。
        // 結果をファイルへ書き、外から読めるようにする
        if (args.Contains("--self-test-notification", StringComparer.Ordinal))
        {
            RunNotificationSelfTest();
        }

        Closed += (_, _) =>
        {
            if (_hotKeyRegistered)
            {
                UnregisterHotKey(handle, HotKeyId);
            }

            SaveWindowPosition();
            Dispose();
        };
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            _clickThrough?.Toggle();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>前に置いた位置へ戻す。無ければ主モニターの右上へ置く。</summary>
    /// <remarks>右下はトーストの出現位置と衝突するため避ける。</remarks>
    private void PositionAtTopRight()
    {
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            Left = left;
            Top = top;
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
    }

    private void SaveWindowPosition() =>
        (_settings with { WindowLeft = Left, WindowTop = Top }).Save();

    /// <summary>取得を始める。</summary>
    private void StartService()
    {
        var coordinate = new Coordinate(_settings.Latitude, _settings.Longitude);
        _service = new ForecastService(coordinate);

        _service.Updated += (_, snapshot) =>
            _viewModel.Apply(snapshot.Forecast, snapshot.Alert, _settings.PlaceName, DateTimeOffset.UtcNow);

        _service.Failed += (_, _) => _viewModel.ApplyFailure(_service.ConsecutiveFailures);

        _service.Start();
    }

    private void OnRefreshNow(object sender, RoutedEventArgs e) => _service?.RefreshNow();

    /// <summary>自己テストの結果をファイルへ書く。</summary>
    private void RunNotificationSelfTest()
    {
        var shown = _toast.Show(
            "着用中止（自己テスト）",
            ["15時ごろ 危険 ・ 連続10分 → 0分", "通知が出るかを確かめています"]);

        var report = string.Join(Environment.NewLine,
            $"registered={_toast.IsRegistered}",
            $"available={_toast.IsAvailable}",
            $"setting={_toast.DescribeSetting()}",
            $"urgentSupported={Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder.IsUrgentScenarioSupported()}",
            $"shown={shown}");

        try
        {
            System.IO.Directory.CreateDirectory(WidgetSettings.Directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(WidgetSettings.Directory, "notification-selftest.txt"),
                report);
        }
        catch (System.IO.IOException)
        {
            // 記録に失敗しても本体は止めない
        }
    }

    /// <summary>
    /// 通知が本当に出るかを、この端末で確かめる。
    /// </summary>
    /// <remarks>
    /// 未パッケージのWPFで <c>AppNotificationManager</c> が動くかは、
    /// 通知の設計全体が乗っている前提である。実機で1回通しておく。
    /// </remarks>
    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        if (!_toast.IsRegistered)
        {
            MessageBox.Show(
                this,
                "通知を登録できていません。" + Environment.NewLine +
                "Windows App SDK のランタイムが入っていない可能性があります。",
                "FursuitWeather",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var shown = _toast.Show(
            "着用中止（動作の確認）",
            ["15時ごろ 危険 ・ 連続10分 → 0分", "これは通知が出るかを確かめるための表示です"],
            urgent: false);

        if (!shown)
        {
            MessageBox.Show(
                this,
                $"通知を出せませんでした。{Environment.NewLine}設定: {_toast.DescribeSetting()}",
                "FursuitWeather",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>トレイの表示を、いまの状態に合わせる。</summary>
    private void UpdateTrayState()
    {
        var state = _clickThrough?.Describe() ?? "クリックスルー: 切";
        TrayIcon.ToolTipText = $"FursuitWeather\n{state}";
        ClickThroughItem.IsChecked = _clickThrough?.IsEnabled ?? false;
        PinItem.IsEnabled = _clickThrough?.IsEnabled ?? false;
        PinItem.IsChecked = _clickThrough?.IsPinned ?? false;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e) => _clickThrough?.Toggle();

    private void OnPinClickThrough(object sender, RoutedEventArgs e)
    {
        if (_clickThrough is null)
        {
            return;
        }

        if (_clickThrough.IsPinned)
        {
            _clickThrough.Disable();
        }
        else
        {
            _clickThrough.Pin();
        }
    }

    private void OnToggleVisibility(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
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
            .AppendLine(CultureInfo.InvariantCulture, $"{_clickThrough?.Describe()}")
            .AppendLine(CultureInfo.InvariantCulture,
                $"ホットキー(Ctrl+Alt+F): {(_hotKeyRegistered ? "登録できている" : "登録できていない")}")
            .AppendLine(CultureInfo.InvariantCulture,
                $"通知の登録: {(_toast.IsRegistered ? "できている" : "できていない")} / 設定: {_toast.DescribeSetting()}")
            .AppendLine()
            .AppendLine("Per-Monitor V2 が効いているかは、タスクマネージャーの")
            .AppendLine("「詳細」タブで「DPI 認識」の列を出して確かめてください。")
            .Append("「システム拡張」ではなく「モニターごと (V2)」と出れば正しい状態です。")
            .ToString();

        MessageBox.Show(this, text, "検証に使う情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>
    /// 抱えている資源を放す。
    /// </summary>
    /// <remarks>
    /// WPF の窓は本来 <see cref="IDisposable"/> を実装しないが、
    /// 取得の常駐とトレイのアイコンを所有するため、閉じるときに明示的に放す。
    /// </remarks>
    public void Dispose()
    {
        _clickThrough?.Stop();
        _service?.Dispose();
        _toast.Dispose();
        TrayIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
