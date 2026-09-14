using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FursuitWeather.Core.Api;
using FursuitWeather.Core.Display;
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
    private UpdateService? _updates;
    private NationalService? _national;
    private DisplayWindow? _display;
    private bool _forecastFailed;
    private bool _teardown;

    /// <summary>掲示中に届いた更新の知らせ。終えたあとに出す。</summary>
    private string? _heldUpdateNotice;

    /// <summary>起動したときの更新の成否。掲示で始めるなら運営者向けの注意へ回す。</summary>
    private (string Title, string[] Lines)? _startupUpdateToast;
    private string? _startupUpdateNotice;

    /// <summary>起動したらそのまま掲示へ入るか。</summary>
    private readonly bool _startInDisplay;
    private SettingsWindow? _settingsWindow;
    private readonly ToastNotifier _toast = new();
    private readonly NotificationDispatcher _dispatcher;
    private WidgetSettings _settings = WidgetSettings.Load();
    private ForecastSnapshot? _snapshot;
    private bool _hotKeyRegistered;
    private bool _selfTestDetector;
    private bool _closed;

    /// <summary>小窓を作る。</summary>
    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _dispatcher = new NotificationDispatcher(_toast);

        // トーストが出せなかったぶんを小窓とトレイへ倒す。
        // 通知だけが静かに壊れる状態を作らないための受け皿である
        _dispatcher.FellBack += (_, message) => Dispatcher.Invoke(() => ShowFallback(message));

        // 掲示で始めるかは、窓を出す前に決める。
        // 更新のあとの起動し直しでは、設定ではなくインストールを始めたときに掲示していたかで決める。
        // 起動の引数には頼らない。自動起動も更新のあとの起動し直しも、引数を渡さないためである
        _startInDisplay = _settings.StartInDisplay || UpdateStateStore.Load().ResumeDisplayAfterInstall;

        if (_settings.Layer == WindowLayer.TrayOnly || _startInDisplay)
        {
            StartHidden();
        }
    }

    /// <summary>
    /// 小窓を出さずに起動する。「トレイだけ」のときに使う。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Window.SourceInitialized"/> の中で隠しても、小窓は出たままになる。
    /// SourceInitialized は <see cref="Window.Show"/> の途中で起き、WPF はそのあとで窓を出す。
    /// その時点の WPF は窓をまだ「見えていない」と記録しているため、<see cref="Window.Hide"/> は何もせずに戻る。
    /// 実際に「トレイだけ」を選んでも、起動のたびに小窓が出ていた。
    /// </para>
    /// <para>
    /// 先に隠しておくと、StartupUri は窓を出さない。Visibility が決まっている窓には手を出さない作りである。
    /// ハンドルは EnsureHandle で作る。出さずに作っても SourceInitialized は起きるため、
    /// トレイ、通知、ホットキー、更新は、出すときと同じ経路でつながる。
    /// </para>
    /// <para>
    /// ハンドルはコンストラクターを抜けてから作る。
    /// StartupUri の読み込みの中で作ると、SourceInitialized で起きた例外が XAML の例外に包まれ、
    /// 落ちたときの画面に本当の理由が出ない。
    /// </para>
    /// </remarks>
    private void StartHidden()
    {
        Hide();
        Dispatcher.BeginInvoke(() => new WindowInteropHelper(this).EnsureHandle());
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
        // 明示的に作る。作られていないとクリックスルーの解除の経路が1つ減る。
        // 引数を省くと、H.NotifyIcon はプロセス全体を効率モード（EcoQoS と IDLE の優先度）にする。
        // 重い処理と重なったとき、予報の取得と通知が後回しにされうるため切る
        TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        StartUpdates(Environment.GetCommandLineArgs());

        var args = Environment.GetCommandLineArgs();

        // 自動で戻る仕組みが効くかを、人が触らずに外から観測するためのスイッチ。
        // 起動と同時にクリックスルーを入れる
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

        // 掲示の見た目と巡回を、人が触らずに確かめるためのスイッチ。
        // 起動したらそのまま掲示へ入る
        if (args.Contains("--self-test-display", StringComparer.Ordinal))
        {
            Dispatcher.BeginInvoke(StartDisplay);
        }

        // 変化の検知から文面までの配線を、実データで確かめるためのスイッチ。
        // 予報を取れてからでないと判定できないため、最初の取得を待つ
        _selfTestDetector = args.Contains("--self-test-detector", StringComparer.Ordinal);

        // 掲示は、起動の処理を抜けてから始める。
        // ここで始めると、掲示のあいだ小窓を隠す処理が効かない。
        // SourceInitialized は Show の途中で起き、そこでの Hide は何もせずに戻る
        if (_startInDisplay)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StartDisplay();
                DeliverStartupUpdateToast();
            });
        }
        else
        {
            DeliverStartupUpdateToast();
        }

        Closed += (_, _) =>
        {
            _closed = true;

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

    /// <summary>
    /// 更新の確認を始める。
    /// </summary>
    /// <param name="args">起動の引数。</param>
    /// <remarks>
    /// <para>
    /// 起動したら最初に、前回のインストールの成否を確定する。
    /// 本体を起動し直すのはインストーラーであり、ここで狙った版と照らす。
    /// </para>
    /// <para>
    /// <c>--update-manifest-url=</c> で確認先を差し替えられる。
    /// <c>latest</c> はrcを指さないため、rcを試すときに版を直接指す。
    /// 差し替えても署名の検証は変わらず効くため、偽のマニフェストは通らない。
    /// </para>
    /// </remarks>
    private void StartUpdates(string[] args)
    {
        const string UrlSwitch = "--update-manifest-url=";
        var url = args.FirstOrDefault(a => a.StartsWith(UrlSwitch, StringComparison.Ordinal))?[UrlSwitch.Length..];
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            url = UpdateService.LatestManifestUrl;
        }

        _updates = new UpdateService(url);

        // 出すのはここではない。掲示で始めるなら、トーストではなく運営者向けの注意へ回す
        var outcome = _updates.ReconcileAtStartup();
        if (outcome == Core.Update.InstallOutcome.Succeeded)
        {
            _startupUpdateToast = ("FursuitWeather を更新しました", [_updates.LastMessage]);
            _startupUpdateNotice = _updates.LastMessage;
        }
        else if (outcome == Core.Update.InstallOutcome.Failed)
        {
            _startupUpdateToast = ("更新が途中で終わりました", [_updates.LastMessage, "トレイの「更新を確認」から入れ直せます"]);
            _startupUpdateNotice = _updates.LastMessage;
        }

        _updates.Changed += (_, _) => UpdateUpdateMenu();
        _updates.Notice += (_, message) => OnUpdateNotice(message);
        _updates.InstallReady += (_, _) => InstallUpdate(manual: false);

        UpdateUpdateMenu();
        _updates.Start();

        if (args.Contains("--self-test-update", StringComparer.Ordinal))
        {
            _ = RunUpdateSelfTestAsync(install: args.Contains("--self-test-update-install", StringComparer.Ordinal));
        }
    }

    /// <summary>更新の知らせを、割り込むかどうか決めて出す。</summary>
    /// <param name="message">知らせの本文。</param>
    /// <remarks>
    /// 掲示のあいだは捨てずに持ち越し、終えたあとに出し直す。
    /// 来場者の見る画面にトーストを重ねないためである。
    /// </remarks>
    private void OnUpdateNotice(string message)
    {
        if (_updates is null)
        {
            return;
        }

        if (_display is not null)
        {
            _heldUpdateNotice = message;
            UpdateUpdateMenu();
            return;
        }

        // 取得前の知らせで「インストール」へ誘うと、押しても無効の項目に行き当たる
        var hint = _updates.HasDownloadedUpdate
            ? "トレイの「更新をインストール」から入れられます"
            : "トレイの「更新を確認」から受け取れます";

        if (_updates.DecidePrompt() == Core.Update.PromptChannel.Toast &&
            _toast.Show("FursuitWeather の更新", [message, hint]))
        {
            _updates.RecordPrompt();
        }

        UpdateUpdateMenu();
    }

    /// <summary>トレイの更新の項目を、いまの状態に合わせる。</summary>
    private void UpdateUpdateMenu()
    {
        if (_updates is null)
        {
            return;
        }

        // 見出しに出すのは、実際に入る版である。見つけた版を出すと、新しい版が出た直後に食い違う
        InstallUpdateItem.IsEnabled = _updates.HasDownloadedUpdate;
        InstallUpdateItem.Header = _updates.DownloadedVersion is { } version
            ? string.Create(CultureInfo.InvariantCulture, $"更新をインストール（{version}）")
            : "更新をインストール";

        UpdateTrayState();
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates is null)
        {
            return;
        }

        // 取得のゲートで止まっていたら、理由と大きさを見せてから尋ねる
        await _updates.CheckThenOfferDownloadAsync(
            question => ShowDialog(question, MessageBoxImage.Question, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            .ConfigureAwait(true);

        ShowDialog(_updates.LastMessage, MessageBoxImage.Information);
    }

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => InstallUpdate(manual: true);

    /// <summary>
    /// インストーラーへ引き渡し、自分を終える。
    /// </summary>
    /// <param name="manual">利用者が押したか。</param>
    /// <remarks>
    /// インストーラーは <c>/WAITPID</c> でこのプロセスの終了を待つ。
    /// 起動できたら<b>すぐに</b>終える。トレイと通知の登録は、終了の処理で片付く。
    /// </remarks>
    private void InstallUpdate(bool manual)
    {
        if (_updates is null)
        {
            return;
        }

        // 設定画面が開いているあいだは、自動では入れない。
        // 入れると設定画面ごとアプリが終わり、未保存の入力が黙って消える。
        // TryInstall を呼ばないので「このプロセスで試した」印も立たず、閉じたあとの見直しが拾い直す
        if (!manual && _settingsWindow is not null)
        {
            return;
        }

        var reason = _updates.TryInstall(manual);
        if (reason is null)
        {
            ShutdownApp();
            return;
        }

        // 自動の経路で見送ったときは黙る。理由はサービスが状態に残し、診断の画面に出る
        if (manual)
        {
            ShowDialog(reason, MessageBoxImage.Information);
        }
    }

    /// <summary>更新の確認を1回通し、結果をファイルへ書く。</summary>
    /// <param name="install">取得できたら、そのまま入れるか。</param>
    /// <remarks>
    /// <c>--self-test-update-install</c> を付けると、取得のあとにインストールまで進む。
    /// 「更新をインストール」を押したのと同じ経路を通る。
    /// 入れるのは署名とハッシュを通したものだけで、押した場合と変わらない。
    /// 引き渡しは自分を終えるため、人が画面を触らずに確かめる手段が他に無い。
    /// </remarks>
    private async Task RunUpdateSelfTestAsync(bool install)
    {
        if (_updates is null)
        {
            return;
        }

        await _updates.CheckNowAsync().ConfigureAwait(true);

        try
        {
            System.IO.Directory.CreateDirectory(WidgetSettings.Directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(WidgetSettings.Directory, "update-selftest.txt"),
                string.Join(
                    Environment.NewLine,
                    _updates.Describe(),
                    string.Create(CultureInfo.InvariantCulture, $"stage={_updates.State.Stage}"),
                    string.Create(CultureInfo.InvariantCulture, $"available={_updates.AvailableVersion?.ToString() ?? "none"}"),
                    string.Create(CultureInfo.InvariantCulture, $"downloaded={_updates.HasDownloadedUpdate}")));
        }
        catch (System.IO.IOException)
        {
            // 記録に失敗しても本体は止めない
        }

        if (install && _updates.HasDownloadedUpdate)
        {
            InstallUpdate(manual: true);
        }
    }

    /// <summary>取得を始める。</summary>
    private void StartService()
    {
        var coordinate = new Coordinate(_settings.Latitude, _settings.Longitude);
        _service = new ForecastService(coordinate);

        _service.Updated += (_, snapshot) => OnForecastUpdated(snapshot);

        _service.Failed += (_, _) =>
        {
            _forecastFailed = true;
            _viewModel.ApplyFailure(_service.ConsecutiveFailures, DateTimeOffset.UtcNow);

            // 掲示にも、取り直せていないことを出す
            PushDisplay();
        };

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
        _forecastFailed = false;
        _viewModel.Apply(snapshot.Forecast, snapshot.Alert, _settings.PlaceName, snapshot.RetrievedAt);
        PushDisplay();

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

        // 「トレイだけ」を選んでいても、届かなかった知らせは目に入る場所へ出す。
        // 出すかどうかの判断は1か所（ApplyLayer）に任せる
        ApplyLayer(ShowRequest.Notification);

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

        _settingsWindow = new SettingsWindow(_settings, state, _updates)
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
            _snapshot = null;
            _service?.ChangeLocation(new Coordinate(_settings.Latitude, _settings.Longitude));
        }
        else
        {
            // 表示名だけ変わった場合に、次の取得を待たずに反映する
            _service?.RefreshNow();
        }

        // 掲示にも、地点と注意の変化をその場で映す
        PushDisplay();
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
    private MessageBoxResult ShowDialog(string text, MessageBoxImage icon, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        if (IsVisible)
        {
            return MessageBox.Show(this, text, "FursuitWeather", buttons, icon);
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
            return MessageBox.Show(host, text, "FursuitWeather", buttons, icon);
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

        // 終わりかけに小窓を出そうとしない。
        // 閉じた窓に Show を呼ぶと例外になり、終了の経路で落ちる
        if (_closed || _teardown)
        {
            return;
        }

        // 掲示のあいだは、どの経路から求められても小窓を出さない。
        // 来場者の見る画面に小窓が重なるためである
        if (_display is not null)
        {
            Hide();
            return;
        }

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

        // 割り込まないと決めた更新の知らせも、ここには必ず出す
        var tip = _updates?.TrayLine is { } update
            ? $"FursuitWeather\n{state}\n{update}"
            : $"FursuitWeather\n{state}";

        // 更新の知らせは毎分渡し直されるため、変わったときだけ書く。書くたびにシェルへ通知が飛ぶ
        if (!string.Equals(TrayIcon.ToolTipText, tip, StringComparison.Ordinal))
        {
            TrayIcon.ToolTipText = tip;
        }

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

    /// <summary>
    /// 2つ目の起動から求められて、小窓を出す。
    /// </summary>
    /// <remarks>
    /// 2つ目は自分では何もせずに終わる。
    /// 隠した小窓を出したくて起動した人に、何も起きないように見せないためである。
    /// </remarks>
    public void RevealForUser()
    {
        // 終わる途中で合図が来ることがある。閉じた窓を出そうとすると例外で落ちる
        if (_closed)
        {
            return;
        }

        ApplyLayer(ShowRequest.User);
    }

    private void OnToggleVisibility(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        // 掲示のあいだは出さない。判断は ApplyLayer に任せる
        ApplyLayer(ShowRequest.User);
    }

    private void OnToggleDisplay(object sender, RoutedEventArgs e)
    {
        if (_display is null)
        {
            StartDisplay();
        }
        else
        {
            _display.Finish();
        }
    }

    /// <summary>
    /// 掲示を始める。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 全国の天気は掲示のあいだだけ取りに行く。
    /// 1回の呼び出しが本体の側で都市の数だけ上流へ広がるためである。
    /// </para>
    /// <para>
    /// 小窓は隠す。取得と通知と更新はこのまま小窓の側が持ち、掲示の窓へは描く材料だけを渡す。
    /// </para>
    /// </remarks>
    private void StartDisplay()
    {
        if (_display is not null)
        {
            _display.Activate();
            return;
        }

        if (_national is null)
        {
            _national = new NationalService();
            _national.Updated += (_, _) => PushDisplay();
        }

        _national.Start();

        var window = new DisplayWindow();
        window.Finished += (_, _) => StopDisplay();

        // モニターが外れて移したときは、上の帯の注意を出し直す
        window.MonitorChanged += (_, _) => PushDisplay();
        _display = window;

        // トーストは1件も出さない。判定と保存は掲示の外と同じように回す
        _dispatcher.Suppressed = true;

        // 自動のインストールは保留する。取得は止めない
        if (_updates is not null)
        {
            _updates.DisplayActive = true;
        }

        // 出す前に隠す。掲示の窓が前に出るまでのあいだ、小窓が映り込まないようにする
        ApplyLayer();
        UpdateDisplayMenu();

        // 選んでいなければ主モニターへ出す
        window.ShowOn(_settings.DisplayMonitorId);
        PushDisplay();
    }

    /// <summary>掲示を終えたあとの後始末。</summary>
    /// <remarks>
    /// 保留していた更新は、1分ごとの見直しが拾う。ここでは何も急がせない。
    /// 持ち越した更新の知らせだけは、出せる時間帯なら出し直す。
    /// </remarks>
    private void StopDisplay()
    {
        _display = null;
        _national?.Stop();
        _dispatcher.Suppressed = false;

        // 起動したときの更新の成否は、1回の掲示で出し終える。
        // 残すと、次に掲示を始めたときにまた運営者向けの注意へ出る
        _startupUpdateNotice = null;

        if (_updates is not null)
        {
            _updates.DisplayActive = false;
        }

        UpdateDisplayMenu();

        // 小窓を元の設定どおりに戻す
        ApplyLayer();

        // 終わりかけなら出さない。終了の途中でトーストを出しても読めない
        if (_heldUpdateNotice is { } held && !_teardown && !_closed)
        {
            _heldUpdateNotice = null;
            OnUpdateNotice(held);
        }
    }

    /// <summary>
    /// 起動したときの更新の成否を知らせる。
    /// </summary>
    /// <remarks>
    /// 掲示で始めたときはトーストを出さず、運営者向けの注意へ回す。
    /// 来場者の見る画面に、起動直後のトーストを重ねないためである。
    /// </remarks>
    private void DeliverStartupUpdateToast()
    {
        if (_startupUpdateToast is not { } toast)
        {
            return;
        }

        _startupUpdateToast = null;

        if (_display is not null)
        {
            return;
        }

        _startupUpdateNotice = null;
        _toast.Show(toast.Title, toast.Lines);
    }

    /// <summary>掲示へ、いま手元にある材料を渡す。</summary>
    /// <remarks>
    /// 掲示を出していなければ何もしない。
    /// 描く材料はここで組み、掲示の窓は受け取って描くだけにする。
    /// </remarks>
    private void PushDisplay()
    {
        if (_display is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        _display.Update(new DisplayInputs
        {
            Forecast = _snapshot?.Forecast,
            Alert = _snapshot?.Alert,

            // 対象日が今日でない応答は渡さない。昨日の天気を今日として掲げないためである
            National = _national is { } national && national.IsUsable(now) ? national.Latest : null,
            PlaceName = _settings.PlaceName,
            Notices = new DisplayNoticeInputs
            {
                ForecastFailed = _forecastFailed,
                MonitorMissing = _display.MonitorMissing,
                MonitorMoved = _display.MonitorMoved,
                HotkeyFailed = _display.HotkeyFailed,
                UpdateResult = _startupUpdateNotice,
                DefaultLocation = !_settings.HasChosenLocation,
                DefaultPlaceName = _settings.PlaceName,
            },
        });
    }

    /// <summary>トレイの掲示の項目を、いまの状態に合わせる。</summary>
    private void UpdateDisplayMenu() =>
        DisplayItem.Header = _display is null ? "掲示を始める" : "掲示を終える";

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
            .AppendLine(CultureInfo.InvariantCulture,
                $"掲示: {(_display is null ? "出していない" : "出している")} / スリープの抑止: {(_display?.SleepSuppressed == true ? "要求できた" : "していない")}")
            .AppendLine(CultureInfo.InvariantCulture,
                $"掲示のホットキー(Ctrl+Alt+D): {(_display is null ? "掲示していない" : _display.HotkeyFailed ? "登録できていない" : "登録できている")}")
            .AppendLine(CultureInfo.InvariantCulture,
                $"起動したら掲示で始める: {(_settings.StartInDisplay ? "入" : "切")}")
            .AppendLine(CultureInfo.InvariantCulture,
                $"地点: {(_settings.HasChosenLocation ? "利用者が選んだ" : "既定のまま")}")
            .AppendLine()
            .AppendLine(_dispatcher.Describe(DateTimeOffset.UtcNow))
            .AppendLine(_updates?.Describe() ?? "更新: 未起動")
            .AppendLine()
            .AppendLine("Per-Monitor V2 が効いているかは、タスクマネージャーの")
            .AppendLine("「詳細」タブで「DPI 認識」の列を出して確かめてください。")
            .Append("「システム拡張」ではなく「モニターごと (V2)」と出れば正しい状態です。")
            .ToString();

        ShowDialog(text, MessageBoxImage.Information);
    }

    private void OnExit(object sender, RoutedEventArgs e) => ShutdownApp();

    /// <summary>
    /// アプリを終える。
    /// </summary>
    /// <remarks>
    /// 終えると決めた印を先に立てる。
    /// 掲示の窓が閉じると小窓を出し直す作りのため、印が無いと終了の途中で出そうとする。
    /// </remarks>
    private void ShutdownApp()
    {
        _teardown = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 抱えている資源を放す。
    /// </summary>
    /// <remarks>
    /// WPF の窓は本来 <see cref="IDisposable"/> を実装しないが、
    /// 取得の常駐とトレイのアイコンを所有するため、閉じるときに明示的に放す。
    /// </remarks>
    public void Dispose()
    {
        _teardown = true;
        _clickThrough?.Stop();

        // 掲示の窓は小窓の持ち物である。閉じないと、小窓を閉じてもアプリが終われない
        _display?.Finish();
        _national?.Dispose();
        _service?.Dispose();
        _updates?.Dispose();
        _toast.Dispose();
        TrayIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
