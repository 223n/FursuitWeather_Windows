using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FursuitWeather.Widget.Services;
using FursuitWeather.Widget.Views;

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

    /// <summary>1つだけ動かすための名前。利用者のセッションごとに分かれる。</summary>
    private const string InstanceName = @"Local\FursuitWeather.Widget";

    /// <summary>2つ目の起動が、1つ目に小窓を出させる合図の名前。</summary>
    private const string RevealName = @"Local\FursuitWeather.Widget.Reveal";

    // どれも static で持つ。ローカル変数にすると GC でハンドルが閉じ、2つ目を見逃す
    private static Mutex? _instance;
    private static EventWaitHandle? _reveal;
    private static RegisteredWaitHandle? _revealWait;

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

        // 2つ目は、1つ目に小窓を出させて自分は終わる。
        // 動かし続けると、トレイのアイコンも暑さの通知も二重になり、状態のファイルを奪い合う。
        // 更新の途中で通知を押されて起動したときにも起きる
        if (!ClaimSingleInstance())
        {
            Environment.Exit(0);
        }

        // 無言で死なせない。
        // 小窓はタスクバーに出ず、Alt+Tabにも現れないため、
        // 落ちても利用者からは「起動しなかった」としか見えない
        DispatcherUnhandledException += OnUnhandledException;

        base.OnStartup(e);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>
    /// このセッションで1つ目の起動かを確かめ、そうなら2つ目からの合図を待ち受ける。
    /// </summary>
    /// <returns>1つ目なら true。</returns>
    /// <remarks>
    /// <para>
    /// 作れたかどうか（createdNew）では見分けない。
    /// 2つ目がハンドルを持ったまま1つ目が終わると、持ち主のいないミューテックスが残り、
    /// 3つ目が「作れなかった」と見て誰も動かない状態になる。
    /// 実際に持てるかを <see cref="WaitHandle.WaitOne(int)"/> で確かめる。
    /// </para>
    /// <para>
    /// 前の持ち主が解放せずに終わっていれば <see cref="AbandonedMutexException"/> になる。
    /// そのときは持てているので、引き継ぐ。
    /// </para>
    /// </remarks>
    private bool ClaimSingleInstance()
    {
        _instance = new Mutex(initiallyOwned: false, InstanceName);
        _reveal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, RevealName);

        bool owned;
        try
        {
            owned = _instance.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            _reveal.Set();
            return false;
        }

        _revealWait = ThreadPool.RegisterWaitForSingleObject(
            _reveal,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as WidgetWindow)?.RevealForUser()),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    /// <summary>
    /// 終えると決めたところで、1つ目の座を明け渡す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ミューテックスは、明け渡さないとプロセスが消えるまで持ち主のまま残る。
    /// 終わりかけのあいだに起動した2つ目は「もう動いている」と見て黙って終わり、
    /// 最後に何も動いていない状態になる。
    /// クラッシュの画面を出して OK を待っているあいだが、いちばん長い。
    /// </para>
    /// <para>
    /// 持ち主の UI スレッドから呼ぶこと。ほかのスレッドからは明け渡せない。
    /// </para>
    /// </remarks>
    private static void ReleaseSingleInstance()
    {
        _revealWait?.Unregister(null);
        _revealWait = null;

        if (_instance is not { } instance)
        {
            return;
        }

        _instance = null;
        try
        {
            instance.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 持っていなかった。明け渡すものが無い
        }

        instance.Dispose();
    }

    /// <summary>
    /// 拾えなかった例外を、利用者と記録の両方へ残してから終わる。
    /// </summary>
    /// <param name="sender">送り主。</param>
    /// <param name="e">起きた例外。</param>
    /// <remarks>
    /// <para>
    /// 握りつぶして動き続けさせない。
    /// 判定を出すアプリが半端な状態で動き続けるほうが危ない。
    /// </para>
    /// <para>
    /// ただし黙って終わらせもしない。
    /// 記録を <c>crash.txt</c> へ書き、画面にも出してから終える。
    /// </para>
    /// </remarks>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var path = Path.Combine(WidgetSettings.Directory, "crash.txt");

        // 画面で OK を待つあいだに起動し直されても、そちらが1つ目として動けるようにする
        Safely(ReleaseSingleInstance);

        Safely(() =>
        {
            Directory.CreateDirectory(WidgetSettings.Directory);
            File.WriteAllText(
                path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}{e.Exception}"));
        });

        Safely(() => MessageBox.Show(
            "FursuitWeather が続けられない状態になりました。" + Environment.NewLine +
            $"内容: {e.Exception.Message}" + Environment.NewLine + Environment.NewLine +
            $"詳しい記録: {path}" + Environment.NewLine + Environment.NewLine +
            "設定が原因のことがあります。直らないときは同じ場所の settings.json を消してみてください。",
            "FursuitWeather",
            MessageBoxButton.OK,
            MessageBoxImage.Error));

        // 処理済みにしてから自分で終える。既定の異常終了の画面を重ねない
        e.Handled = true;
        Shutdown(1);
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
