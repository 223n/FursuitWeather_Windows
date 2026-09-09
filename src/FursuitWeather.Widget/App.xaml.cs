using System.Windows;
using FursuitWeather.Widget.Services;

namespace FursuitWeather.Widget;

/// <summary>アプリの入口。</summary>
public partial class App : Application
{
    /// <summary>
    /// アンインストーラーが渡す引数。
    /// </summary>
    /// <remarks>
    /// 画面を出さずに後始末だけをして終わる。
    /// アンインストーラーは昇格しないため、この呼び出しも利用者の権限で走る。
    /// </remarks>
    private const string UninstallCleanupSwitch = "--uninstall-cleanup";

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Args.Contains(UninstallCleanupSwitch, StringComparer.Ordinal))
        {
            // StartupUri を先に消す。base.OnStartup のあとで小窓が作られるため、
            // ここで消さないとアンインストールの最中に画面が出る
            StartupUri = null;
            RunUninstallCleanup();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// アンインストールのときに端末へ残るものを消す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 消すのは次の2つで、どちらもファイルを消すだけでは残る。
    /// </para>
    /// <list type="number">
    /// <item>通知の登録。<c>UnregisterAll</c> はアンインストールのときだけ呼ぶ</item>
    /// <item>自動起動の登録。<c>HKCU</c> のRunキーの値</item>
    /// </list>
    /// <para>
    /// 設定と通知の状態（<c>%LOCALAPPDATA%\FursuitWeather</c>）は消さない。
    /// 入れ直したときに地点の設定が戻るほうが親切であり、
    /// 消したい人は自分で消せる場所に置いてあるためである。
    /// </para>
    /// <para>
    /// どちらの失敗もアンインストールを止めない。
    /// ここで例外を投げると、アンインストーラーが途中で止まって
    /// 「消せないアプリ」ができあがる。
    /// </para>
    /// </remarks>
    private static void RunUninstallCleanup()
    {
        ToastNotifier.UnregisterAll();
        StartupRegistration.Remove();
    }
}
