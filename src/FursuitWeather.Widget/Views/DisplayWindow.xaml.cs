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

    /// <summary>スリープの抑止を要求できたか。検証の画面に出す。</summary>
    public bool SleepSuppressed { get; private set; }

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
        var monitors = MonitorLayout.Enumerate();
        var decision = MonitorChoice.Resolve(preferredId, [.. monitors.Select(m => m.ToCore())]);

        // 出す前に置く。出してから動かすと、いちど別の場所へ描かれてから飛ぶ
        new WindowInteropHelper(this).EnsureHandle();

        var target = decision.Monitor is { } chosen
            ? monitors.FirstOrDefault(m => string.Equals(m.Id, chosen.Id, StringComparison.OrdinalIgnoreCase))
            : null;

        if (target is null)
        {
            // モニターを1台も読めなかった。WPFの知る主モニターいっぱいに広げる。
            // 大きさを決めずに出すと、既定の小さな窓のまま掲示することになる
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }
        else
        {
            MonitorLayout.Place(this, target);
        }

        Show();

        // 出したあとにもう一度当てる。
        // WPFが出すときに大きさを当て直す場合があり、当て直されるとモニターいっぱいにならない
        if (target is not null)
        {
            MonitorLayout.Place(this, target);
        }

        Activate();
        _tick.Start();
        return decision.FellBack;
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

        Closed += (_, _) =>
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            _tick.Stop();
            DisplaySleep.Release();
            Finished?.Invoke(this, EventArgs.Empty);
        };
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
