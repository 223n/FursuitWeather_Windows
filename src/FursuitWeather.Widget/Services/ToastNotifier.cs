using System.IO;
using System.Runtime.InteropServices;
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
        try
        {
            var manager = AppNotificationManager.Default;

            if (onInvoked is not null)
            {
                manager.NotificationInvoked += (_, args) => onInvoked(args);
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

        if (!_registered)
        {
            return;
        }

        try
        {
            // 終了時は Unregister。UnregisterAll はアンインストールのときだけ
            AppNotificationManager.Default.Unregister();
        }
        catch (COMException)
        {
            // 解除に失敗しても終了は妨げない
        }
    }
}
