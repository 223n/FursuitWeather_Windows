using System.Windows;
using System.Windows.Threading;
using FursuitWeather.Widget.Interop;

namespace FursuitWeather.Widget.Views;

/// <summary>
/// クリックスルーの入り切りを、閉じ込められない形で扱う。
/// </summary>
/// <remarks>
/// <para>
/// クリックスルーを有効にした窓は、一切のマウス入力を受け付けない。
/// 小窓はタスクバーにもAlt+Tabにも出ないため、解除の手段を失うと操作できなくなる。
/// </para>
/// <para>
/// 実際にこれで詰まった。解除の経路を1つしか用意していなかったのが原因である。
/// 経路を3つに増やし、そのうち1つは人の操作を要さないものにした。
/// </para>
/// <list type="number">
/// <item>タスクトレイのメニュー。常に手が届く</item>
/// <item>グローバルホットキー</item>
/// <item><b>猶予の時間が過ぎたら自動で戻す。</b>ほかの2つが効かなくても必ず戻る</item>
/// </list>
/// </remarks>
internal sealed class ClickThroughGuard
{
    private readonly Window _window;
    private readonly DispatcherTimer _revertTimer;

    /// <summary>
    /// 自動で戻すまでの猶予。
    /// </summary>
    /// <remarks>
    /// 「保つ」を選ばないかぎり、この時間で必ず戻る。
    /// 閉じ込められない状態を、人の操作に頼らずに保証するためである。
    /// </remarks>
    public static readonly TimeSpan RevertAfter = TimeSpan.FromSeconds(30);

    /// <summary>いま素通しにしているか。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>猶予を解いて保ち続けるか。</summary>
    public bool IsPinned { get; private set; }

    /// <summary>状態が変わったときに起きる。</summary>
    public event EventHandler? Changed;

    /// <summary>ガードを作る。</summary>
    /// <param name="window">対象のウィンドウ。</param>
    public ClickThroughGuard(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _revertTimer = new DispatcherTimer { Interval = RevertAfter };
        _revertTimer.Tick += (_, _) => Disable();
    }

    /// <summary>入り切りを切り替える。</summary>
    public void Toggle()
    {
        if (IsEnabled)
        {
            Disable();
        }
        else
        {
            Enable();
        }
    }

    /// <summary>素通しにする。猶予が過ぎたら自動で戻る。</summary>
    public void Enable()
    {
        IsEnabled = true;
        IsPinned = false;
        WindowChrome.SetClickThrough(_window, true);

        _revertTimer.Stop();
        _revertTimer.Start();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>素通しをやめる。</summary>
    public void Disable()
    {
        _revertTimer.Stop();
        IsEnabled = false;
        IsPinned = false;
        WindowChrome.SetClickThrough(_window, false);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 猶予を解き、戻さずに保ち続ける。
    /// </summary>
    /// <remarks>
    /// トレイのメニューから明示的に選んだときだけ呼ぶ。
    /// トレイは素通しの影響を受けないため、ここで保っても操作の手段は残る。
    /// </remarks>
    public void Pin()
    {
        if (!IsEnabled)
        {
            Enable();
        }

        _revertTimer.Stop();
        IsPinned = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>いまの状態を短い文で表す。</summary>
    public string Describe() => (IsEnabled, IsPinned) switch
    {
        (false, _) => "クリックスルー: 切",
        (true, true) => "クリックスルー: 入（保持中）",
        (true, false) => "クリックスルー: 入（まもなく自動で戻ります）",
    };

    /// <summary>猶予の計測を止める。窓を閉じるときに呼ぶ。</summary>
    public void Stop() => _revertTimer.Stop();
}
