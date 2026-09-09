using System.IO;
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
            RunUninstallCleanup();

            // ここで即座に終える。
            //
            // StartupUri に null を代入して小窓を止める書き方は使えない。
            // このプロパティは null を受け付けず、ArgumentNullException を投げる。
            // 実際にそれで落ち、後始末が1行も走らないまま
            // アンインストーラーが「終わった」と見なす状態を作っていた。
            //
            // Shutdown() でも足りない。OnStartup から戻ったあとに
            // StartupUri が評価されるため、閉じる前に小窓が一瞬出る。
            Environment.Exit(0);
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
        Safely(ToastNotifier.UnregisterAll);
        Safely(StartupRegistration.Remove);
    }

    /// <summary>
    /// 何が起きても呼び出し元へ例外を返さない。
    /// </summary>
    /// <param name="step">実行する後始末。</param>
    /// <remarks>
    /// <para>
    /// それぞれの処理も自前で例外を捕まえるが、そちらは種類を並べて書いている。
    /// 並べ損ねた種類が1つあるだけで、後続の後始末が丸ごと走らなくなる。
    /// 実際に <c>UnregisterAll</c> の <see cref="FileNotFoundException"/> で
    /// 自動起動の登録が消し残った。
    /// </para>
    /// <para>
    /// ここは後始末専用の経路で、失敗しても続けるのが常に正しい。
    /// そのため型を絞らずに受ける。
    /// </para>
    /// </remarks>
    private static void Safely(Action step)
    {
#pragma warning disable CA1031 // 後始末はどの例外でも止めない
        try
        {
            step();
        }
        catch (Exception)
        {
            // 消せなくてもアンインストールは続ける
        }
#pragma warning restore CA1031
    }
}
