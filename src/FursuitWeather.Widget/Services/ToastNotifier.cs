using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// トースト通知を出す。
/// </summary>
/// <remarks>
/// <para>
/// 未パッケージのアプリでも <c>Register</c> がCOMサーバーの登録を行う。
/// AppUserModelIDもスタートメニューのショートカットも要らない。
/// ただし表示名とアイコンをシェル任せにすると、exeの名前とシェル既定へ劣化するため、
/// 明示する版の <c>Register</c> を使う。
/// </para>
/// <para>
/// <b>昇格して実行してはいけない。</b>管理者権限で動かすと <c>Show</c> が無言で失敗する。
/// </para>
/// <para>
/// 何を出すかの判断は <see cref="Core.Changes.ChangeDetector"/> が持つ。
/// ここは出す手段だけを持つ。
/// </para>
/// </remarks>
public sealed class ToastNotifier : IDisposable
{
    private const string DisplayName = "FursuitWeather";

    private Action<AppNotificationActivatedEventArgs>? _onInvoked;
    private bool _hooked;
    private bool _registered;
    private bool _disposed;

    /// <summary>登録できているか。</summary>
    public bool IsRegistered => _registered;

    /// <summary>通知が使える状態か。</summary>
    /// <remarks>
    /// ランタイムが無い、利用者が通知を切っている、といった場合に偽になる。
    /// 偽のときは小窓とトレイへ倒す。通知だけが静かに壊れる状態を作らないためである。
    /// </remarks>
    public bool IsAvailable =>
        _registered && AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;

    /// <summary>
    /// 利用者がWindowsの側で通知を切っているか。
    /// </summary>
    /// <remarks>
    /// 登録はできているのに出せない状態を指す。
    /// ランタイムが無くて登録できない状態とは区別する。
    /// 前者は利用者が選んだ結果であり、後者は環境の不備だからである。
    /// </remarks>
    public bool IsBlockedByUser =>
        _registered && AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled;

    /// <summary>いまの設定を文字で返す。診断に使う。</summary>
    public string DescribeSetting()
    {
        if (!_registered)
        {
            return "登録できていません";
        }

        return AppNotificationManager.Default.Setting switch
        {
            AppNotificationSetting.Enabled => "有効",
            AppNotificationSetting.DisabledForApplication => "このアプリについて無効",
            AppNotificationSetting.DisabledForUser => "この利用者について無効",
            AppNotificationSetting.DisabledByGroupPolicy => "グループポリシーで無効",
            AppNotificationSetting.DisabledByManifest => "マニフェストで無効",
            _ => "不明",
        };
    }

    /// <summary>
    /// 通知を使えるようにする。
    /// </summary>
    /// <param name="onInvoked">通知が押されたときに呼ぶ処理。</param>
    /// <returns>登録できたら true。</returns>
    /// <remarks>
    /// <c>NotificationInvoked</c> のハンドラーを <c>Register</c> より先に付ける。
    /// 順序を誤ると、通知の処理のために新しいプロセスが起動する。
    /// </remarks>
    public bool Initialize(Action<AppNotificationActivatedEventArgs>? onInvoked = null)
    {
        if (_registered)
        {
            return true;
        }

        try
        {
            var manager = AppNotificationManager.Default;

            if (onInvoked is not null)
            {
                _onInvoked = onInvoked;
            }

            // ハンドラーは1回だけ付ける。入れ直すたびに足すと、通知1回で処理が何度も走る
            if (!_hooked)
            {
                manager.NotificationInvoked += (_, args) => _onInvoked?.Invoke(args);
                _hooked = true;
            }

            manager.Register(DisplayName, IconUri());
            _registered = true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or TypeInitializationException or DllNotFoundException)
        {
            // ランタイムが無いなど。通知は諦め、小窓とトレイで伝える
            _registered = false;
        }

        return _registered;
    }

    /// <summary>
    /// 通知を出す。
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="lines">本文。最大2行まで使われる。</param>
    /// <param name="urgent">重い通知として出すか。</param>
    /// <returns>出せたら true。</returns>
    /// <remarks>
    /// 重い通知は利用者がアプリごとに一度許可する仕組みのため、
    /// 着用中止級と公式発表だけに限ること。濫用すると拒否されて全部止まる。
    /// </remarks>
    public bool Show(string title, IReadOnlyList<string> lines, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);

        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder().AddText(title);
            foreach (var line in lines.Take(2))
            {
                builder = builder.AddText(line);
            }

            if (urgent && AppNotificationBuilder.IsUrgentScenarioSupported())
            {
                builder = builder.SetScenario(AppNotificationScenario.Urgent);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 押すと別の場所を開くボタンを付けて、通知を出す。
    /// </summary>
    /// <param name="title">見出し。</param>
    /// <param name="lines">本文。最大2行まで使われる。</param>
    /// <param name="buttonText">ボタンの文字。</param>
    /// <param name="target">ボタンで開く先。</param>
    /// <returns>出せたら true。</returns>
    /// <remarks>
    /// ボタンは開く先を Windows に直接渡す。
    /// このアプリの活性化を通らないため、アプリが終わったあとに押されても動く。
    /// </remarks>
    public bool ShowWithLink(string title, IReadOnlyList<string> lines, string buttonText, Uri target)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(buttonText);
        ArgumentNullException.ThrowIfNull(target);

        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder().AddText(title);
            foreach (var line in lines.Take(2))
            {
                builder = builder.AddText(line);
            }

            builder = builder.AddButton(new AppNotificationButton(buttonText).SetInvokeUri(target));

            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 活性化の種類が通知かを、別のスレッドで読む。
    /// </summary>
    /// <returns>通知だと読めたら true。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="Initialize"/> で登録したあとに呼ぶ。
    /// <c>GetActivatedEventArgs</c> は <c>Register</c> より後に呼ぶ決まりがある（<c>docs/architecture.md</c> の「通知」）。
    /// </para>
    /// <para>
    /// <b>通知の引数を持って起動されたのに COM の呼び出しが来ないと、2秒ほど待ってから <see cref="TimeoutException"/> を投げる。</b>
    /// HRESULT は <c>0x800705B4</c>。実機で確かめた。
    /// 捕まえ損ねた版は、起動の途中で落ちた。
    /// UIのスレッドで呼ぶと、落ちなくてもそのあいだ起動が止まるため、別のスレッドで読む。
    /// </para>
    /// </remarks>
    public static Task<bool> ReadNotificationActivationAsync() => Task.Run(() =>
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.AppNotification;
        }
        catch (Exception e) when (e is TimeoutException or COMException or InvalidOperationException
            or TypeInitializationException or DllNotFoundException)
        {
            return false;
        }
    });

    /// <summary>
    /// 登録を解く。
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> と違い、あとで <see cref="Initialize"/> し直せる。
    /// 設定で通知を切ったときに使う。
    /// </remarks>
    public void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
            // 解除に失敗しても続ける
        }

        _registered = false;
    }

    /// <summary>
    /// この端末からこのアプリの通知の登録を消す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>アンインストールのときだけ呼ぶ。</b>
    /// 終了のたびに呼ぶ <see cref="Shutdown"/> と違い、
    /// 通知センターに残っている過去の通知ごと消える。
    /// </para>
    /// <para>
    /// ランタイムが先に消えていると例外になる。
    /// アンインストールを止めないため、握りつぶす。
    /// </para>
    /// <para>
    /// <b>登録が無いときは <see cref="FileNotFoundException"/> が飛ぶ。</b>
    /// WinRT のHRESULTがそう写るためで、実機で確認している。
    /// これを捕まえ損ねると、アンインストールの後始末が1行も走らずにプロセスが落ち、
    /// 自動起動の登録が端末に残る。
    /// </para>
    /// </remarks>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException
            or TypeInitializationException or DllNotFoundException or FileNotFoundException)
        {
            // 消せなくてもアンインストールは続ける
        }
    }

    /// <summary>通知のアイコンの場所。</summary>
    private static Uri IconUri()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "tray.ico");
        return new Uri(path, UriKind.Absolute);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 終了時は Unregister。UnregisterAll はアンインストールのときだけ
        Shutdown();
    }
}
