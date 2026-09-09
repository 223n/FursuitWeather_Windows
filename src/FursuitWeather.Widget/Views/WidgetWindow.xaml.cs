using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Notifications;
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
    private SettingsWindow? _settingsWindow;
    private readonly ToastNotifier _toast = new();
    private readonly NotificationDispatcher _dispatcher;
    private WidgetSettings _settings = WidgetSettings.Load();
    private ForecastSnapshot? _snapshot;
    private bool _hotKeyRegistered;
    private bool _selfTestDetector;

    /// <summary>小窓を作る。</summary>
    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _dispatcher = new NotificationDispatcher(_toast);

        // トーストが出せなかったぶんを小窓とトレイへ倒す。
        // 通知だけが静かに壊れる状態を作らないための受け皿である
        _dispatcher.FellBack += (_, message) => Dispatcher.Invoke(() => ShowFallback(message));
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
        ApplyLayer();
        // NotificationInvoked を Register より先に付ける。順序を誤ると
        // 通知の処理のために新しいプロセスが起動する
        ApplyNotificationSetting(initial: true);
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

        // 変化の検知から文面までの配線を、実データで確かめるためのスイッチ。
        // 予報を取れてからでないと判定できないため、最初の取得を待つ
        _selfTestDetector = args.Contains("--self-test-detector", StringComparer.Ordinal);

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
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top && IsMostlyVisible(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        ResetPosition();
    }

    /// <summary>既定の位置へ戻す。主モニターの右上。</summary>
    /// <remarks>右下はトーストの出現位置と衝突するため避ける。</remarks>
    private void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
    }

    /// <summary>
    /// その位置に置いたとき、十分に画面へ入るかを見る。
    /// </summary>
    /// <remarks>
    /// モニターの構成が変わると、保存した位置が画面の外になることがある。
    /// 小窓はタスクバーにもAlt+Tabにも出ないため、
    /// 画面の外に出ると掴む手段が無くなる。復元の前に必ず確かめる。
    /// </remarks>
    private bool IsMostlyVisible(double left, double top)
    {
        var target = new Rect(left, top, Width, Height);

        foreach (var area in MonitorAreas())
        {
            var overlap = Rect.Intersect(target, area);
            if (overlap.IsEmpty)
            {
                continue;
            }

            // 面積の半分以上が入っていれば掴める
            if (overlap.Width * overlap.Height >= target.Width * target.Height * 0.5)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>つながっているモニターの作業領域。</summary>
    private static IEnumerable<Rect> MonitorAreas()
    {
        // WPF は多モニターの情報を持たないため、仮想画面の全体と主モニターの作業領域で近似する。
        // 主モニターより左や上にモニターがある構成では、仮想画面の側が効く
        yield return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        yield return SystemParameters.WorkArea;
    }

    private void SaveWindowPosition() => WidgetSettings.SaveWindowPosition(Left, Top);

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        ResetPosition();
        SaveWindowPosition();
        _settings = WidgetSettings.Load();
        ApplyLayer(ShowRequest.User);
    }

    /// <summary>取得を始める。</summary>
    private void StartService()
    {
        var coordinate = new Coordinate(_settings.Latitude, _settings.Longitude);
        _service = new ForecastService(coordinate);

        _service.Updated += (_, snapshot) => OnForecastUpdated(snapshot);

        _service.Failed += (_, _) =>
            _viewModel.ApplyFailure(_service.ConsecutiveFailures, DateTimeOffset.UtcNow);

        _service.Start();
    }

    /// <summary>
    /// 取得できた内容を、表示と通知の両方へ流す。
    /// </summary>
    /// <param name="snapshot">取得できた内容。</param>
    /// <remarks>
    /// 表示と通知で同じ時刻を使う。別々に時計を読むと、
    /// 小窓には出ていない時間の悪化を通知が指す、といった食い違いが起こりうる。
    /// </remarks>
    private void OnForecastUpdated(ForecastSnapshot snapshot)
    {
        _snapshot = snapshot;
        _viewModel.Apply(snapshot.Forecast, snapshot.Alert, _settings.PlaceName, snapshot.RetrievedAt);

        _dispatcher.Process(
            snapshot.Forecast,
            snapshot.Alert,
            new Coordinate(_settings.Latitude, _settings.Longitude),
            _settings.PlaceName,
            snapshot.RetrievedAt);

        if (_selfTestDetector)
        {
            _selfTestDetector = false;
            RunDetectorSelfTest(snapshot);
        }
    }

    /// <summary>
    /// トーストの代わりに、小窓とトレイで知らせる。
    /// </summary>
    /// <param name="message">届かなかった通知。</param>
    /// <remarks>
    /// <para>
    /// トーストが出せないのは、ランタイムが無いときと、
    /// 利用者がWindowsの側で通知を切っているときである。
    /// どちらでも、安全に関わる知らせを黙って捨てない。
    /// </para>
    /// <para>
    /// バルーンはトーストとは別の経路（<c>Shell_NotifyIcon</c>）を通る。
    /// 片方が壊れていても、もう片方が届くことがある。
    /// </para>
    /// <para>
    /// <b>利用者がWindowsの側で通知を切っている場合は、割り込まない。</b>
    /// 小窓の文字は書き換えるが、隠れている小窓を出したりバルーンを鳴らしたりはしない。
    /// 環境の不備を埋めるのが目的であって、利用者の選択を覆すのが目的ではない。
    /// </para>
    /// </remarks>
    private void ShowFallback(NotificationMessage message)
    {
        // 小窓の文字は常に書き換える。小窓はもともと判定を出し続ける場所であり、割り込みではない
        _viewModel.ApplyNotice($"{message.Title}　{string.Join("　", message.Lines)}", DateTimeOffset.UtcNow);

        if (_toast.IsBlockedByUser)
        {
            UpdateTrayState();
            return;
        }

        try
        {
            TrayIcon.ShowNotification(message.Title, string.Join(Environment.NewLine, message.Lines));
        }
        catch (InvalidOperationException)
        {
            // バルーンも出せなければ、小窓の表示だけが残る
        }

        // 「トレイだけ」を選んでいても、届かなかった知らせは目に入る場所へ出す
        if (_settings.Layer != WindowLayer.TrayOnly)
        {
            Show();
        }

        UpdateTrayState();
    }

    private void OnRefreshNow(object sender, RoutedEventArgs e) => _service?.RefreshNow();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        // 二重に開かせない。あとから保存したほうが、先に開いた画面の古い値で巻き戻る
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var state = _settings.NotificationsEnabled
            ? _toast.DescribeSetting()
            : "この設定で切っています";

        _settingsWindow = new SettingsWindow(_settings, state)
        {
            Owner = IsVisible ? this : null,
        };

        var dialog = _settingsWindow;
        bool? answer;
        try
        {
            answer = dialog.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }

        if (answer != true || dialog.Result is not { } updated)
        {
            return;
        }
        var locationChanged =
            Math.Abs(updated.Latitude - _settings.Latitude) > double.Epsilon ||
            Math.Abs(updated.Longitude - _settings.Longitude) > double.Epsilon;

        _settings = updated;
        ApplyLayer();
        ApplyNotificationSetting(initial: false);

        if (locationChanged)
        {
            // 前の地点の判定を残さない。取得できるまで「取得中」を見せる
            _viewModel.ApplyLocationPending(_settings.PlaceName);
            _service?.ChangeLocation(new Coordinate(_settings.Latitude, _settings.Longitude));
        }
        else
        {
            // 表示名だけ変わった場合に、次の取得を待たずに反映する
            _service?.RefreshNow();
        }
    }

    /// <summary>設定に合わせて通知の登録を入れ直す。</summary>
    /// <param name="initial">起動時の呼び出しなら true。</param>
    /// <remarks>
    /// 起動時に1回だけ登録すると、設定で切って入れ直しても再起動まで効かない。
    /// 設定を保存するたびに当て直す。
    /// </remarks>
    private void ApplyNotificationSetting(bool initial)
    {
        // 切っているあいだは判定も保存もしない。
        // 理由は NotificationDispatcher.Process の注記にある
        _dispatcher.Enabled = _settings.NotificationsEnabled;

        if (_settings.NotificationsEnabled)
        {
            var ok = _toast.Initialize(_ => Dispatcher.Invoke(() => ApplyLayer(ShowRequest.Notification)));

            if (!ok && !initial)
            {
                // 黙って壊れない。設定したのに効かないことを利用者へ返す
                ShowDialog(
                    "通知を有効にできませんでした。" + Environment.NewLine +
                    "Windows App SDK のランタイムが入っていない可能性があります。",
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            _toast.Shutdown();
        }

        UpdateTrayState();
    }

    /// <summary>
    /// 小窓を親にしてよいかを見たうえで、案内を出す。
    /// </summary>
    /// <remarks>
    /// 隠れている小窓を親にすると、案内が背面へ回って触れなくなる。
    /// 見えていないときは親を付けず、最前面で出す。
    /// </remarks>
    private void ShowDialog(string text, MessageBoxImage icon)
    {
        if (IsVisible)
        {
            MessageBox.Show(this, text, "FursuitWeather", MessageBoxButton.OK, icon);
            return;
        }

        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Topmost = true,
            ShowActivated = false,
        };

        host.Show();
        try
        {
            MessageBox.Show(host, text, "FursuitWeather", MessageBoxButton.OK, icon);
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>表示を求めてきた経路。</summary>
    private enum ShowRequest
    {
        /// <summary>設定を当てるだけ。表示は求めない。</summary>
        None,

        /// <summary>利用者が明示的に求めた。</summary>
        User,

        /// <summary>通知が押された。</summary>
        Notification,
    }

    /// <summary>
    /// 設定に合わせて小窓の高さと表示を整える。
    /// </summary>
    /// <param name="request">表示を求めてきた経路。</param>
    /// <remarks>
    /// 表示する経路をここ1本に集約する。
    /// 通知のクリックが設定を飛び越えて最前面へ出すようなことを防ぐ。
    /// </remarks>
    private void ApplyLayer(ShowRequest request = ShowRequest.None)
    {
        // Topmost は必ず設定から導く。分岐によって前の値が残らないようにする
        Topmost = _settings.Layer == WindowLayer.AlwaysOnTop;

        if (_settings.Layer == WindowLayer.TrayOnly)
        {
            // 「トレイだけ」を選んでいるなら、通知から押されても小窓は出さない。
            // 利用者が明示的に求めたときだけ出す
            if (request == ShowRequest.User)
            {
                Show();
            }
            else
            {
                Hide();
            }

            return;
        }

        Show();
    }

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
        if (!_settings.NotificationsEnabled)
        {
            ShowDialog(
                "この設定で通知を切っています。" + Environment.NewLine +
                "設定で「トースト通知を使う」にチェックを入れてから試してください。",
                MessageBoxImage.Information);
            return;
        }

        if (!_toast.IsRegistered)
        {
            ShowDialog(
                "通知を登録できていません。" + Environment.NewLine +
                "Windows App SDK のランタイムが入っていない可能性があります。",
                MessageBoxImage.Warning);
            return;
        }

        var shown = _toast.Show(
            "着用中止（動作の確認）",
            ["15時ごろ 危険 ・ 連続10分 → 0分", "これは通知が出るかを確かめるための表示です"],
            urgent: false);

        if (!shown)
        {
            ShowDialog(
                $"通知を出せませんでした。{Environment.NewLine}設定: {_toast.DescribeSetting()}",
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// いまの予報で悪化が起きたとみなし、実際に通知を出してみる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「通知を試す」が確かめるのはトーストの発行だけである。
    /// こちらは、予報の取得から変化の検知、文面の組み立て、発行までを通す。
    /// </para>
    /// <para>
    /// 基準と履歴には触らない。試したせいで本物の通知が抑制されると本末転倒である。
    /// </para>
    /// </remarks>
    private void OnPreviewNotification(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } snapshot)
        {
            ShowDialog(
                "まだ予報を取得できていません。" + Environment.NewLine +
                "「いま取り直す」を試してから、もう一度実行してください。",
                MessageBoxImage.Information);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var messages = NotificationDispatcher.Preview(
            snapshot.Forecast,
            snapshot.Alert,
            new Coordinate(_settings.Latitude, _settings.Longitude),
            _settings.PlaceName,
            now);

        if (messages.Count == 0)
        {
            ShowDialog(
                "いまの予報では、出す通知がありませんでした。" + Environment.NewLine +
                "判定が45分・ほぼ安全から悪化していない状態です。",
                MessageBoxImage.Information);
            return;
        }

        var shown = _dispatcher.Deliver(messages, now);
        var body = string.Join(
            Environment.NewLine,
            messages.Select(m => $"・{m.Title}{Environment.NewLine}　{string.Join(Environment.NewLine + "　", m.Lines)}"));

        ShowDialog(
            string.Create(
                CultureInfo.InvariantCulture,
                $"いまの予報から{messages.Count}件を組み立て、{shown}件をトーストで出しました。{Environment.NewLine}{Environment.NewLine}{body}"),
            MessageBoxImage.Information);
    }

    /// <summary>変化の検知から文面までを実データで通し、結果をファイルへ書く。</summary>
    /// <param name="snapshot">取得できた内容。</param>
    private void RunDetectorSelfTest(ForecastSnapshot snapshot)
    {
        var now = snapshot.RetrievedAt;
        var messages = NotificationDispatcher.Preview(
            snapshot.Forecast,
            snapshot.Alert,
            new Coordinate(_settings.Latitude, _settings.Longitude),
            _settings.PlaceName,
            now);

        var shown = _dispatcher.Deliver(messages, now);

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"generatedAt={snapshot.Forecast.GeneratedAt:O}")
            .AppendLine(CultureInfo.InvariantCulture, $"hours={snapshot.Forecast.Hours.Count}")
            .AppendLine(CultureInfo.InvariantCulture, $"alert={(snapshot.Alert is null ? "none" : snapshot.Alert.PrefectureName)}")
            .AppendLine(CultureInfo.InvariantCulture, $"messages={messages.Count}")
            .AppendLine(CultureInfo.InvariantCulture, $"shown={shown}");

        foreach (var message in messages)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"message={message.Describe()}");
        }

        report.AppendLine().AppendLine(_dispatcher.Describe(now));

        try
        {
            System.IO.Directory.CreateDirectory(WidgetSettings.Directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(WidgetSettings.Directory, "detector-selftest.txt"),
                report.ToString());
        }
        catch (System.IO.IOException)
        {
            // 記録に失敗しても本体は止めない
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
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // DragMove はドラッグが終わるまで戻らない
        DragMove();

        // 動かし終えたところで保存する。
        // 終了時だけに頼ると、強制終了やクラッシュで位置を失う
        SaveWindowPosition();
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
                $"通知: 設定で{(_settings.NotificationsEnabled ? "入" : "切")} / 登録{(_toast.IsRegistered ? "済" : "なし")} / {_toast.DescribeSetting()}")
            .AppendLine(CultureInfo.InvariantCulture, $"自動起動: {StartupRegistration.GetState()}")
            .AppendLine(CultureInfo.InvariantCulture, $"小窓の高さ: {_settings.Layer}")
            .AppendLine()
            .AppendLine(_dispatcher.Describe(DateTimeOffset.UtcNow))
            .AppendLine()
            .AppendLine("Per-Monitor V2 が効いているかは、タスクマネージャーの")
            .AppendLine("「詳細」タブで「DPI 認識」の列を出して確かめてください。")
            .Append("「システム拡張」ではなく「モニターごと (V2)」と出れば正しい状態です。")
            .ToString();

        ShowDialog(text, MessageBoxImage.Information);
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
