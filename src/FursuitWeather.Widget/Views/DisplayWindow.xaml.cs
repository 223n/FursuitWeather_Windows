using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FursuitWeather.Core.Display;
using FursuitWeather.Widget.Interop;

namespace FursuitWeather.Widget.Views;

/// <summary>
/// 会場へ掲げる全画面のスライド。
/// </summary>
/// <remarks>
/// <para>
/// <b>小窓とは別の、不透明な窓である。</b>
/// 小窓を引き伸ばさない理由は3つある（<c>docs/display.md</c>）。
/// 透過の窓はClearTypeが効かないこと、小窓はキー入力を受け取らないこと、
/// クリックスルーのガードが付いていて入力を素通しすることである。
/// </para>
/// <para>
/// 取得と通知と更新は小窓の側が持つ。この窓は受け取って描くだけにする。
/// 主の窓にもしない。主にすると、掲示を閉じたときにアプリごと終わる。
/// </para>
/// </remarks>
internal sealed partial class DisplayWindow : Window
{
    /// <summary>掲示を終えるホットキーの識別の値。小窓のクリックスルーとは別にする。</summary>
    private const int ExitHotKeyId = 0xF002;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkD = 0x44;
    private const int WmHotKey = 0x0312;

    /// <summary>モニターの構成が変わったことの知らせ。</summary>
    private const int WmDisplayChange = 0x007E;

    /// <summary>操作の帯とカーソルを隠すまでの時間。</summary>
    private static readonly TimeSpan IdleBeforeHiding = TimeSpan.FromSeconds(3);

    /// <summary>手元のデータで描き直す間隔。</summary>
    /// <remarks>
    /// 取得の知らせは成功したときにしか来ない。
    /// 時刻が進んだだけで変わるもの（いまの時間の行、鮮度、先読み）を追うために要る。
    /// </remarks>
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMinutes(1);

    /// <summary>切り替えの、消える時間と出る時間。</summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.5);

    private readonly DisplayViewModel _viewModel = new();
    private readonly DispatcherTimer _tick;

    private string? _preferredId;
    private string? _originalId;
    private string? _currentId;
    private bool _hotKeyRegistered;

    private RotationState _rotation;
    private DisplayInputs _inputs = new();
    private TimeSpan _startedAt;
    private TimeSpan _lastRedraw;
    private TimeSpan _lastActivity;
    private (int X, int Y) _shift = (0, 0);
    private int _fade;
    private bool _operatorWindow = true;
    private bool _emergency;
    private bool _controlsVisible;
    private bool _closing;

    /// <summary>掲示を終えたときに起きる。</summary>
    /// <remarks>利用者が終えたときも、ほかの経路で閉じたときも1回だけ起きる。</remarks>
    public event EventHandler? Finished;

    /// <summary>モニターの置き方が変わったときに起きる。</summary>
    /// <remarks>上の帯の注意を出し直すために、外へ知らせる。</remarks>
    public event EventHandler? MonitorChanged;

    /// <summary>スリープの抑止を要求できたか。検証の画面に出す。</summary>
    public bool SleepSuppressed { get; private set; }

    /// <summary>掲示を終えるホットキーを登録できなかったか。</summary>
    public bool HotkeyFailed => !_hotKeyRegistered;

    /// <summary>選んだモニターが見つからず、主モニターへ出しているか。</summary>
    public bool MonitorMissing { get; private set; }

    /// <summary>掲示先のモニターが外れ、ほかのモニターへ移したか。</summary>
    public bool MonitorMoved { get; private set; }

    /// <summary>掲示の窓を作る。</summary>
    public DisplayWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        var monotonic = Monotonic();
        _startedAt = monotonic;
        _lastRedraw = monotonic;
        _lastActivity = monotonic;
        _rotation = DisplayRotation.Start(monotonic);
        _viewModel.Select(_rotation.Current);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += OnTick;
    }

    /// <summary>
    /// 出す先のモニターを決めて掲げる。
    /// </summary>
    /// <param name="preferredId">設定で選んだモニター。選んでいなければ null。</param>
    /// <returns>選んだモニターが見つからず、主モニターへ落としたなら true。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="Window.Show"/> より先にハンドルを作り、置いてから出す。
    /// 位置と大きさは物理ピクセルのまま当てるため、<c>SetWindowPos</c> を使う。
    /// </para>
    /// <para>
    /// XAMLで <c>Width</c> と <c>Height</c> を決めていないのは、
    /// 決めると <see cref="Window.Show"/> がその大きさへ戻し、モニターの大きさと食い違うためである。
    /// </para>
    /// </remarks>
    public bool ShowOn(string? preferredId)
    {
        _preferredId = preferredId;

        // 出す前に置く。出してから動かすと、いちど別の場所へ描かれてから飛ぶ
        new WindowInteropHelper(this).EnsureHandle();
        ApplyMonitor(initial: true);

        Show();

        // 出したあとにもう一度当てる。
        // WPFが出すときに大きさを当て直す場合があり、当て直されるとモニターいっぱいにならない
        ApplyMonitor(initial: true);

        _originalId = _currentId;

        Activate();
        _tick.Start();
        return MonitorMissing;
    }

    /// <summary>
    /// いまつながっているモニターを読み直し、窓を置き直す。
    /// </summary>
    /// <param name="initial">掲示を始めるときの呼び出しなら true。</param>
    /// <remarks>
    /// <para>
    /// 開いているあいだに掲示先が外れたら、残ったモニターへ移して続ける。
    /// 無人の端末で、一瞬抜けただけで止まったままにしないためである。
    /// </para>
    /// <para>
    /// 戻ってきたら元のモニターへ戻し、移したことの注意も消す。
    /// </para>
    /// </remarks>
    private void ApplyMonitor(bool initial)
    {
        var monitors = MonitorLayout.Enumerate();
        var decision = MonitorChoice.Resolve(_preferredId, [.. monitors.Select(m => m.ToCore())]);

        var target = decision.Monitor is { } chosen
            ? monitors.FirstOrDefault(m => string.Equals(m.Id, chosen.Id, StringComparison.OrdinalIgnoreCase))
            : null;

        MonitorMissing = decision.FellBack;

        if (target is null)
        {
            if (!initial)
            {
                return;
            }

            // モニターを1台も読めなかった。WPFの知る主モニターいっぱいに広げる。
            // 大きさを決めずに出すと、既定の小さな窓のまま掲示することになる
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            return;
        }

        if (!initial && _currentId is { } previous &&
            !monitors.Any(m => string.Equals(m.Id, previous, StringComparison.OrdinalIgnoreCase)))
        {
            // 出していたモニターが外れた
            MonitorMoved = true;
        }

        MonitorLayout.Place(this, target);
        _currentId = target.Id;

        if (_originalId is { } original && string.Equals(target.Id, original, StringComparison.OrdinalIgnoreCase))
        {
            // 元のモニターへ戻れた
            MonitorMoved = false;
        }
    }

    /// <summary>
    /// 描く材料を差し替える。
    /// </summary>
    /// <param name="inputs">材料。</param>
    /// <remarks>
    /// 呼ぶのはUIのスレッドからにする。
    /// 回線の回復から直に来る知らせは、別のスレッドで起きうる。
    /// </remarks>
    public void Update(DisplayInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        _inputs = inputs;
        Redraw();
    }

    /// <summary>掲示を終える。</summary>
    public void Finish() => Close();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 掲示のあいだ、画面と端末を眠らせない
        SleepSuppressed = DisplaySleep.Keep();

        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);

        // 抜ける経路その3。全画面の窓がタスクバーを覆うと、トレイに手が届かなくなる
        _hotKeyRegistered = RegisterHotKey(handle, ExitHotKeyId, ModControl | ModAlt | ModNoRepeat, VkD);

        Closed += (_, _) =>
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            _tick.Stop();

            if (_hotKeyRegistered)
            {
                UnregisterHotKey(handle, ExitHotKeyId);
            }

            DisplaySleep.Release();
            Finished?.Invoke(this, EventArgs.Empty);
        };
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == ExitHotKeyId)
        {
            Finish();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmDisplayChange)
        {
            ApplyMonitor(initial: false);
            MonitorChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var monotonic = Monotonic();
        var now = DateTimeOffset.UtcNow;

        _viewModel.Tick(now);
        ApplyBurnInShift(monotonic);
        HideControlsWhenIdle(monotonic);

        var next = DisplayRotation.Tick(_rotation, monotonic, _emergency);
        var moved = next.Current != _rotation.Current;
        _rotation = next;

        if (moved)
        {
            ShowSlide(next.Current, animate: true);
        }

        _viewModel.IsPaused = _rotation.IsPaused;

        // 運営者向けの注意は60秒で消す。1分ごとの描き直しに任せると、最長で倍まで残る
        var expired = _operatorWindow && monotonic - _startedAt >= DisplayBand.OperatorNoticeLifetime;

        if (expired || monotonic - _lastRedraw >= RedrawInterval)
        {
            Redraw();
        }
    }

    /// <summary>手元の材料で描き直す。</summary>
    private void Redraw()
    {
        var now = DateTimeOffset.UtcNow;
        _lastRedraw = Monotonic();

        // 掲示に入ってから60秒のあいだだけ、運営者向けの注意を出す
        _operatorWindow = Monotonic() - _startedAt < DisplayBand.OperatorNoticeLifetime;
        var inputs = _inputs with
        {
            Notices = _inputs.Notices with { OperatorWindow = _operatorWindow },
        };

        _viewModel.Apply(inputs, now);
        _emergency = EmergencySteps.ShouldShow(DisplayViewModel.CurrentHour(inputs.Forecast, now));
    }

    /// <summary>焼き付きを避けるため、描く位置を少しずつずらす。</summary>
    private void ApplyBurnInShift(TimeSpan monotonic)
    {
        var offset = BurnInShift.Offset(monotonic - _startedAt);
        if (offset == _shift)
        {
            return;
        }

        _shift = offset;
        Shift.X = offset.X;
        Shift.Y = offset.Y;
    }

    /// <summary>
    /// スライドを差し替える。
    /// </summary>
    /// <param name="slide">出すスライド。</param>
    /// <param name="animate">切り替えの効果を付けるか。</param>
    /// <remarks>
    /// 自動で送るときだけ、0.5秒で消してから0.5秒で出す。
    /// 手で送ったときと、Windowsのアニメーションを切っている端末では、効果なしで差し替える。
    /// </remarks>
    private void ShowSlide(DisplaySlide slide, bool animate)
    {
        // 消えている途中に手で送られたら、古い効果の終わりで巻き戻さない
        var generation = ++_fade;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            SlideHost.BeginAnimation(OpacityProperty, null);
            SlideHost.Opacity = 1;
            _viewModel.Select(slide);
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, FadeDuration);
        fadeOut.Completed += (_, _) =>
        {
            // 閉じたあとに効果が終わることがある。閉じた窓を描き直さない
            if (_closing || generation != _fade)
            {
                return;
            }

            _viewModel.Select(slide);
            SlideHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
        };

        SlideHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>手で送る。送ったスライドの秒数が過ぎれば、自動送りへ戻る。</summary>
    private void Advance(bool forward)
    {
        var monotonic = Monotonic();
        _rotation = forward
            ? DisplayRotation.Next(_rotation, monotonic, _emergency)
            : DisplayRotation.Previous(_rotation, monotonic, _emergency);

        ShowSlide(_rotation.Current, animate: false);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            case Key.Escape:
                Finish();
                e.Handled = true;
                break;
            case Key.Right:
                Advance(forward: true);
                e.Handled = true;
                break;
            case Key.Left:
                Advance(forward: false);
                e.Handled = true;
                break;
            case Key.Space:
                TogglePause();
                e.Handled = true;
                break;
            default:
                break;
        }

        ShowControls();
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // 操作の帯のボタンは、そちらで処理されてここへ来ない
        Advance(forward: true);
        ShowControls();
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => TogglePause();

    private void OnNext(object sender, RoutedEventArgs e) => Advance(forward: true);

    private void OnFinish(object sender, RoutedEventArgs e) => Finish();

    private void TogglePause()
    {
        _rotation = DisplayRotation.TogglePause(_rotation, Monotonic());
        _viewModel.IsPaused = _rotation.IsPaused;
    }

    /// <summary>操作の帯とカーソルを出す。</summary>
    private void ShowControls()
    {
        _lastActivity = Monotonic();

        if (_controlsVisible)
        {
            return;
        }

        _controlsVisible = true;
        ControlBar.Visibility = Visibility.Visible;
        Cursor = Cursors.Arrow;
    }

    /// <summary>しばらく触られていなければ、操作の帯とカーソルを隠す。</summary>
    private void HideControlsWhenIdle(TimeSpan monotonic)
    {
        if (!_controlsVisible || monotonic - _lastActivity < IdleBeforeHiding)
        {
            return;
        }

        _controlsVisible = false;
        ControlBar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.None;
    }

    /// <summary>単調時刻。利用者が時計を戻しても巡回が止まらないようにする。</summary>
    private static TimeSpan Monotonic() => TimeSpan.FromMilliseconds(Environment.TickCount64);
}
