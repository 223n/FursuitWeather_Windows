using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Widget.Services;

/// <summary>
/// 更新を確認し、取得し、インストーラーへ引き渡す。
/// </summary>
/// <remarks>
/// <para>
/// 判断はすべてCoreに置いてある。
/// ここが持つのは、時計を読むこと、通信すること、ファイルを抱えること、
/// インストーラーを起動することだけである。
/// </para>
/// <para>
/// 検証の順序は <see cref="ManifestVerifier"/> が固定している。
/// ここで中身を先に覗いてはいけない。
/// </para>
/// </remarks>
public sealed class UpdateService : IDisposable
{
    /// <summary>
    /// 確認先。安定版の最新を指す。
    /// </summary>
    /// <remarks>
    /// <b>一度公開したURLは利用者の手元に焼き込まれ、事実上変えられない。</b>
    /// GitHubの <c>latest</c> は「最新の非プレリリースかつ非ドラフト」であり、rcを指さない。
    /// </remarks>
    public const string LatestManifestUrl =
        "https://github.com/223n/FursuitWeather_Windows/releases/latest/download/update.json";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>マニフェストの大きさの上限。これより大きければ読まない。</summary>
    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>署名の大きさの上限。DERのP-256なら72バイト前後に収まる。</summary>
    private const int MaxSignatureBytes = 1024;

    private readonly HttpClient _http;
    private readonly DispatcherTimer _tick;
    private readonly UpdateDownloadStore _store;
    private readonly string _manifestUrl;
    private readonly double _phase;

    private UpdateState _state;
    private ManifestVerification? _available;
    private DownloadedUpdate? _download;
    private bool _noticePending;
    private bool _busy;
    private bool _disposed;
    private bool _answeredThisSession;
    private int _unansweredChecks;
    private DateTimeOffset? _lastUnansweredAt;
    private bool _autoInstallAttempted;

    /// <summary>状態が変わったときに起きる。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 利用者へ知らせたいことがあるときに起きる。
    /// </summary>
    /// <remarks>受け取った側がトーストか、トレイか、小窓かを選ぶ。</remarks>
    public event EventHandler<string>? Notice;

    /// <summary>自動の扱いでインストールへ進んでよくなったときに起きる。</summary>
    /// <remarks>
    /// 自分を終えるのはUIの層の仕事である。
    /// ここはインストーラーを起動できる状態になったことを知らせるだけにする。
    /// </remarks>
    public event EventHandler? InstallReady;

    /// <summary>いま動いている版。</summary>
    public SemanticVersion Running { get; }

    /// <summary>いまの状態。</summary>
    public UpdateState State => _state;

    /// <summary>取得を終えて、インストールできる更新があるか。</summary>
    public bool HasDownloadedUpdate => _download is not null;

    /// <summary>取得を終えた更新の版。無ければ null。</summary>
    /// <remarks>
    /// インストールで入るのはこの版である。
    /// <see cref="AvailableVersion"/> と取り違えないこと。新しい版が出た直後は食い違いうる。
    /// </remarks>
    public SemanticVersion? DownloadedVersion => _download?.Version;

    /// <summary>見つかった更新の版。無ければ null。</summary>
    public SemanticVersion? AvailableVersion => _available?.Version;

    /// <summary>見つかった更新の配布物の大きさ。無ければ null。</summary>
    public long? AvailableSize => _available?.Package?.Size;

    /// <summary>
    /// トレイのツールチップに足す1行。出すものが無ければ null。
    /// </summary>
    /// <remarks>
    /// 割り込まないと決めた知らせの受け皿である。
    /// トーストを出さなくても、更新があることはここで分かる。
    /// </remarks>
    public string? TrayLine
    {
        get
        {
            if (_download is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"更新: {_download.Version} を入れられます");
            }

            if (_state.Stage == UpdateStage.Failed)
            {
                return "更新: 前回のインストールが途中で終わりました";
            }

            if (_available?.Version is { } version && UpdateCheckSchedule.IsPending(_state.Stage))
            {
                return string.Create(CultureInfo.InvariantCulture, $"更新: {version} が出ています");
            }

            // 途中のまま起動し、まだ確かめ直せていない。
            // 見つけた更新はメモリにしか持たないため、確かめ直すまで版を出せない
            return _state.Stage is UpdateStage.UpdateAvailable
                or UpdateStage.DownloadHeld
                or UpdateStage.DownloadPaused
                or UpdateStage.Downloaded
                or UpdateStage.InstallHeld
                ? "更新: 途中の更新を確かめ直しています"
                : null;
        }
    }

    /// <summary>直近の結果を1行で。診断と設定画面に出す。</summary>
    public string LastMessage { get; private set; } = "まだ確認していません";

    /// <summary>作る。</summary>
    /// <param name="manifestUrl">確認先。通常は <see cref="LatestManifestUrl"/>。</param>
    public UpdateService(string manifestUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);

        _manifestUrl = manifestUrl;
        Running = ReadRunningVersion();
        _phase = DevicePhase();
        _state = UpdateStateStore.Load();
        _store = new UpdateDownloadStore(Path.Combine(WidgetSettings.Directory, "update"));

        _http = new HttpClient { Timeout = DownloadTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            string.Create(CultureInfo.InvariantCulture, $"FursuitWeather_Windows/{Running} (+https://github.com/223n/FursuitWeather_Windows)"));

        _tick = new DispatcherTimer { Interval = TickInterval };
        _tick.Tick += async (_, _) => await OnTickAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 起動したときに、前回のインストールの成否を確定する。
    /// </summary>
    /// <returns>確定した結論。</returns>
    /// <remarks>
    /// 本体を起動し直すのはインストーラーである（ランタイムを入れたあとの <c>ssPostInstall</c> か、中止したときの <c>DeinitializeSetup</c>）。
    /// ここで狙った版と照らし、成功か中断かを決める。
    /// </remarks>
    public InstallOutcome ReconcileAtStartup()
    {
        var (next, outcome) = UpdateLedger.Reconcile(_state, Running, DateTimeOffset.UtcNow);

        // Releasesから手で入れて失敗の版を追い越していれば、自動の遮断も解く
        var forgotten = UpdateLedger.ForgetOvertakenFailures(next, Running);
        if (!ReferenceEquals(forgotten, _state))
        {
            _state = forgotten;
            UpdateStateStore.Save(_state);
        }

        if (outcome != InstallOutcome.None)
        {
            LastMessage = outcome == InstallOutcome.Succeeded
                ? string.Create(CultureInfo.InvariantCulture, $"{Running} に更新しました")
                : "前回のインストールが途中で終わりました。次は確認してから実行します";
        }

        // 入れ終えたなら、置いたものはもう要らない。
        // 途中のままなら残す。次の確認で、署名を通したマニフェストと照らしてから使い回す。
        // 起動のたびに消すと、入れるのを待っている利用者が再起動のたびに200MB近くを取り直す
        if (outcome == InstallOutcome.Succeeded || !UpdateCheckSchedule.IsPending(_state.Stage))
        {
            _store.CleanExcept(null);
        }

        return outcome;
    }

    /// <summary>動かし始める。</summary>
    public void Start() => _tick.Start();

    /// <summary>
    /// いま確認する。
    /// </summary>
    /// <returns>終わるまで待つタスク。</returns>
    /// <remarks>
    /// 利用者が押したときの経路である。周期も抑制も通さない。
    /// ただし取得へ進むかは、押したのが「確認」なのでゲートに従う。
    /// </remarks>
    public Task CheckNowAsync() => CheckAsync(manual: true);

    /// <summary>
    /// いま取得する。
    /// </summary>
    /// <returns>終わるまで待つタスク。</returns>
    /// <remarks>利用者が押したときの経路である。取得のゲートを通さない。</remarks>
    public async Task DownloadNowAsync()
    {
        if (_busy || _available is null)
        {
            return;
        }

        _busy = true;
        try
        {
            await DownloadCoreAsync(manual: true).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OnTickAsync()
    {
        if (_busy || _disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // 確かな答えを得られなかった確認のあとは、間を空けてから試し直す。毎分は叩かない
        var waiting = _lastUnansweredAt is { } at &&
            now < at + UpdateCheckSchedule.UnansweredRetryDelay(_unansweredChecks);

        var due = UpdateCheckSchedule.IsDue(
            _state,
            now,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            UpdateEnvironment.Read(0).Uptime,
            _phase,
            answeredThisSession: _answeredThisSession || waiting);

        if (due)
        {
            await CheckAsync(manual: false).ConfigureAwait(true);
        }
        else
        {
            await ResumeIfUnblockedAsync().ConfigureAwait(true);
        }

        // 時間帯の外などで割り込まなかった知らせを、出せるときが来たら出す。
        // 出すかどうかは受け取った側が決める。出さなければ残り、次の tick でまた渡す
        if (_noticePending && !_disposed)
        {
            RaiseNotice();
        }
    }

    /// <summary>
    /// 保留と中断を、条件が解けたところで先へ進める。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通信はしない。見るのは手元の状態と環境だけである。
    /// 周期の確認だけに頼ると、電池で保留したものがAC電源に挿しても翌日まで動かない。
    /// 失敗のあとの1分、5分、15分の待ちも、使われないまま切れる。
    /// </para>
    /// <para>
    /// 自動のインストールは、このプロセスで1回しか試さない。
    /// 起動できなかったものを毎分試し直さないためである。
    /// </para>
    /// </remarks>
    private async Task ResumeIfUnblockedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        if (_download is null &&
            _available?.Package is { } package &&
            _state.Stage is UpdateStage.DownloadHeld or UpdateStage.DownloadPaused)
        {
            var gate = UpdateGate.ForDownload(_state, UpdateEnvironment.Read(package.Size), now);
            if (!gate.CanProceed)
            {
                // 見送る理由が変わったら書き換える。電池から従量制へ、のように移りうる
                if (gate.Outcome == GateOutcome.Hold && _state.Stage == UpdateStage.DownloadHeld)
                {
                    var text = UpdateGate.Describe(gate.Reason);
                    if (!string.Equals(text, LastMessage, StringComparison.Ordinal))
                    {
                        SetStage(UpdateStage.DownloadHeld, text);
                    }
                }

                return;
            }

            _busy = true;
            try
            {
                await DownloadCoreAsync(manual: false).ConfigureAwait(true);
            }
            finally
            {
                _busy = false;
            }

            return;
        }

        if (_download is not null &&
            !_autoInstallAttempted &&
            _state.Mode == UpdateMode.Automatic &&
            _state.Stage is UpdateStage.Downloaded or UpdateStage.InstallHeld)
        {
            var gate = UpdateGate.ForInstall(_state, UpdateEnvironment.Read(0), now);
            if (gate.CanProceed)
            {
                InstallReady?.Invoke(this, EventArgs.Empty);
            }
            else if (gate.Outcome == GateOutcome.Hold)
            {
                HoldInstall(gate.Reason);
            }
        }
    }

    private async Task CheckAsync(bool manual)
    {
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        try
        {
            // 確かめられなかったときは、前の状態を変えない
            var before = _state.Stage;
            SetStage(UpdateStage.Checking);

            var wall = DateTimeOffset.UtcNow;
            var monotonic = TimeSpan.FromMilliseconds(Environment.TickCount64);

            byte[] manifest;
            byte[] signature;
            try
            {
                using var cts = new CancellationTokenSource(ManifestTimeout);
                manifest = await GetLimitedAsync(_manifestUrl, MaxManifestBytes, cts.Token).ConfigureAwait(true);
                signature = await GetLimitedAsync(_manifestUrl + ".sig", MaxSignatureBytes, cts.Token).ConfigureAwait(true);
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // 安定版がまだ1つも出ていない。更新は無いものとして扱う。
                // 失敗として数えると、正式版を出すまで警告が出続ける
                MarkAnswered();
                Finish(wall, monotonic, accepted: null, UpdateStage.Idle, "公開されている安定版はまだありません");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
            {
                // タイムアウトは TaskCanceledException だけとは限らない。
                // 書き込みの待ちの最中に切れると、基底の OperationCanceledException のまま来る
                MarkUnanswered(wall);
                Finish(wall, monotonic, accepted: null, before, "確認できませんでした。回線の状態を確かめてください");
                return;
            }

            // 署名を通すまで中身を見ない。順序は検証器の側が持つ
            var result = ManifestVerifier.Verify(
                manifest,
                signature,
                ReleaseKey.PublicKeyPem,
                Running,
                _state.LastManifestGeneratedAt,
                Environment.OSVersion.Version.Build,
                ProcessArch());

            if (result.Verdict == UpdateVerdict.SignatureInvalid)
            {
                // 誰が作ったか分からない応答で、前に検証を通したものを捨てない。
                // 公衆Wi-Fiの認証ページや一時の不具合でも起きる。答えを得られなかったものとして扱う
                MarkUnanswered(wall);
                Finish(wall, monotonic, accepted: null, before, Describe(result));
                return;
            }

            MarkAnswered();

            if (result.Verdict == UpdateVerdict.NotNewer)
            {
                // いまの版が追いついた。Releasesから手で入れた場合もここへ来る。
                // 取ってあったものも、見つけてあった更新も、もう要らない
                DropDownload();
                _store.CleanExcept(null);
                _available = null;
                _noticePending = false;
                Finish(wall, monotonic, result.Manifest?.GeneratedAt, UpdateStage.Idle, Describe(result));
                return;
            }

            if (!result.HasUpdate || result.Version is not { } version || result.Package is not { } package)
            {
                // 署名を通ったマニフェストが、見つけてあった更新を否定した。
                // リリースを取り下げたとき（古いマニフェストが返る）もここへ来る。
                // 進めるのをやめる。前の答えを信じて取りに行くと、取り下げた版を入れてしまう。
                //
                // 置き場のファイルは消さない。一時の食い違いで同じ版がまた返れば、照らしてから使い回す。
                // 途中の状態でなくなるため、次の起動で片付く
                DropDownload();
                _available = null;
                _noticePending = false;
                Finish(wall, monotonic, result.Manifest?.GeneratedAt, UpdateStage.Idle, Describe(result));
                return;
            }

            _available = result;
            _state = UpdateLedger.RecordAvailable(_state, version.ToString(), package.Sha256);

            // 狙いが変わったら、前に取ったものは使わない。
            // 残すと、新しい版の名前で古い取得物を入れ、次の起動で失敗として数える
            if (_download is not null && !_download.IsFor(version, package))
            {
                DropDownload();
            }

            if (_download is not null)
            {
                // もう手元にある。同じものを取り直さない
                Finish(
                    wall,
                    monotonic,
                    result.Manifest?.GeneratedAt,
                    UpdateStage.Downloaded,
                    string.Create(CultureInfo.InvariantCulture, $"{version} の準備ができています"));
                SetNotice();
                return;
            }

            Finish(
                wall,
                monotonic,
                result.Manifest?.GeneratedAt,
                UpdateStage.UpdateAvailable,
                string.Create(CultureInfo.InvariantCulture, $"{version} が出ています"));

            await DownloadCoreAsync(manual: false).ConfigureAwait(true);

            _ = manual;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 取得して、大きさとハッシュを確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取ったものは掴んだまま持つ。検証から実行までのあいだに差し替えられないようにする。
    /// </para>
    /// <para>
    /// 送られてきた量がマニフェストの大きさを超えたら、そこで止める。
    /// 取得先を信用しているわけではない。正しさを担うのは署名とハッシュである。
    /// </para>
    /// <para>
    /// 同じ配布物が手元にあれば取り直さない。
    /// 置いてあるものは、署名を通したマニフェストの値とハッシュで照らしてから使う。
    /// </para>
    /// </remarks>
    private async Task DownloadCoreAsync(bool manual)
    {
        if (_available?.Package is not { } package || _available.Version is not { } version)
        {
            return;
        }

        if (_download is not null && _download.IsFor(version, package))
        {
            _state = UpdateLedger.RecordDownloaded(_state);
            Save(string.Create(CultureInfo.InvariantCulture, $"{version} の準備ができています"));
            return;
        }

        // 狙いの違うものは抱えない
        DropDownload();

        var fileName = ManifestVerifier.PackageFileName(package);

        // 通信しないので、取得のゲートより先に見る。
        // 前のプロセスが取ったものや、入れるのに失敗したものがここで見つかる
        var reused = await Task.Run(() => _store.FindVerified(fileName, package.Size, package.Sha256)).ConfigureAwait(true);
        if (reused is not null)
        {
            Adopt(reused, version, package, manual);
            return;
        }

        // 置き場に、いまの狙いに使えるものは無い。見送る場合もここで片付ける。
        // 見送りの前に消さないと、手放した旧版の実体が保留のあいだ残り続ける。
        // 途中で終わった取得の残りもここで消える
        _store.CleanExcept(null);

        if (!manual)
        {
            var gate = UpdateGate.ForDownload(_state, UpdateEnvironment.Read(package.Size), DateTimeOffset.UtcNow);
            if (!gate.CanProceed)
            {
                _state = _state with
                {
                    Stage = gate.Outcome == GateOutcome.Hold ? UpdateStage.DownloadHeld : UpdateStage.UpdateAvailable,
                };
                Save(gate.Outcome == GateOutcome.Hold
                    ? UpdateGate.Describe(gate.Reason)
                    : string.Create(CultureInfo.InvariantCulture, $"{version} が出ています。取得は押したときだけ行います"));
                SetNotice();
                return;
            }
        }

        SetStage(UpdateStage.Downloading, string.Create(CultureInfo.InvariantCulture, $"{version} を取得しています"));

        UpdateDownload? download = null;
        try
        {
            download = _store.Create(fileName);

            using (var cts = new CancellationTokenSource(DownloadTimeout))
            using (var response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(true))
            {
                response.EnsureSuccessStatusCode();

                using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(true);
                using var target = download.OpenForWriting();
                await CopyLimitedAsync(source, target, package.Size, cts.Token).ConfigureAwait(true);
            }

            download.Hold();

            // 200MB近くのハッシュはUIのスレッドで取らない。掴んだハンドル越しに測ることは変わらない
            var held = download;
            var sha256 = await Task.Run(held.ComputeSha256).ConfigureAwait(true);
            if (download.Length != package.Size ||
                !string.Equals(sha256, package.Sha256, StringComparison.Ordinal))
            {
                download.Dispose();
                FailDownload(version, package, "取得したファイルがマニフェストと一致しません。破損か改ざんの可能性があります");
                return;
            }

            Adopt(download, version, package, manual);
            download = null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // タイムアウトは TaskCanceledException だけとは限らない。
            // 書き込みの待ちの最中に切れると、基底の OperationCanceledException のまま来る
            download?.Dispose();
            FailDownload(version, package, "取得できませんでした。あとで試し直します");
        }
    }

    /// <summary>検証を通った取得物を、インストールに使うものとして抱える。</summary>
    private void Adopt(UpdateDownload file, SemanticVersion version, UpdatePackage package, bool manual)
    {
        DropDownload();
        _download = new DownloadedUpdate(file, version, package);
        _store.CleanExcept(file.Id);

        _state = UpdateLedger.RecordDownloaded(_state);
        Save(string.Create(CultureInfo.InvariantCulture, $"{version} の準備ができました"));
        SetNotice();

        if (!manual && _state.Mode == UpdateMode.Automatic)
        {
            InstallReady?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// インストーラーを起動する。
    /// </summary>
    /// <param name="manual">利用者が押したか。</param>
    /// <returns>起動できたら null。止めたときは理由。</returns>
    /// <remarks>
    /// <para>
    /// 起動できたら、呼び出し側は<b>すぐに</b>自分を終えること。
    /// インストーラーは <c>/WAITPID</c> でこのプロセスの終了を待ってからファイルを置き換える。
    /// </para>
    /// <para>
    /// 開発用のビルドから起動しているときは入れない。
    /// <c>/DIR</c> に今の場所を渡すため、ビルドの出力先へインストールしてしまう。
    /// </para>
    /// </remarks>
    public string? TryInstall(bool manual)
    {
        // 版とSHA-256は取得物の側から取る。見つけた版の側から取ると、新しい版が出た直後に食い違う
        if (_download is not { } download)
        {
            return "取得済みの更新がありません";
        }

        var version = download.Version;
        var package = download.Package;

        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(installDirectory, "unins000.exe")))
        {
            return "インストールした場所から起動していないため、入れ替えられません";
        }

        if (!manual)
        {
            var gate = UpdateGate.ForInstall(_state, UpdateEnvironment.Read(0), DateTimeOffset.UtcNow);
            if (!gate.CanProceed)
            {
                if (gate.Outcome != GateOutcome.Hold)
                {
                    return "押されるのを待っています";
                }

                // 理由を残す。黙って戻ると「なぜ入らないのか」が分からない
                HoldInstall(gate.Reason);
                return LastMessage;
            }

            _autoInstallAttempted = true;
        }

        var logPath = Path.Combine(
            WidgetSettings.Directory,
            "logs",
            string.Create(CultureInfo.InvariantCulture, $"install-{version}.log"));
        if (!InstallCommand.CanWriteLog(logPath))
        {
            // /LOG= に書けない場所を渡すと、インストーラーがエラーで中止する
            logPath = null;
        }

        var arguments = InstallCommand.BuildArguments(installDirectory, Environment.ProcessId, logPath);

        // 狙いは起動より前に書く。書けなければ進まない。
        // 書けないまま起動すると、次の起動で成否を確定できない
        var previous = _state;
        var next = UpdateLedger.BeginInstall(UpdateLedger.AcknowledgeInterruption(_state), version.ToString(), package.Sha256);
        if (!UpdateStateStore.Save(next))
        {
            return "状態を保存できなかったため、インストールを中止しました";
        }

        _state = next;

        try
        {
            // インストール先を作業ディレクトリにしない。そこにハンドルが張られる
            Process.Start(new ProcessStartInfo(download.File.Path, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = download.File.Directory,
            });
            _noticePending = false;
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 起動できなかった。書いた狙いを戻さないと、次の起動で失敗として数えてしまう
            _state = previous;
            UpdateStateStore.Save(_state);
            return "インストーラーを起動できませんでした。セキュリティ製品が止めている可能性があります";
        }
    }

    /// <summary>
    /// いま、どう促すべきかを決める。
    /// </summary>
    /// <returns>促し方。</returns>
    /// <remarks>
    /// 割り込むのは9時から21時のあいだで、同じ日に2回以上は出さない。
    /// 5回を超えたら割り込まず、トレイと小窓にだけ残す。
    /// </remarks>
    public PromptChannel DecidePrompt() =>
        UpdatePrompt.Decide(
            _state,
            DateTimeOffset.UtcNow,
            _available?.Channel?.SeverityId ?? UpdateSeverity.Normal);

    /// <summary>トーストで促したことを書き入れる。</summary>
    /// <remarks>割り込んだ回数だけを数える。トレイに出しただけのものは数えない。</remarks>
    public void RecordPrompt()
    {
        _noticePending = false;
        _state = UpdatePrompt.RecordToast(_state, DateTimeOffset.UtcNow);
        UpdateStateStore.Save(_state);
    }

    /// <summary>診断の画面に出す1行。</summary>
    /// <returns>人が読める1行。</returns>
    public string Describe()
    {
        var checkedAt = _state.LastCheckedAt is { } at
            ? Core.Time.JstTime.ToLocal(at).ToString("M月d日 H:mm", CultureInfo.InvariantCulture)
            : "まだ";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"更新: いまの版 {Running} / {_state.Stage} / {LastMessage} / 最後に確認 {checkedAt} / 確認先 {_manifestUrl}");
    }

    /// <summary>
    /// 知らせを渡し、出されるまで持っておく。
    /// </summary>
    /// <remarks>
    /// 割り込むかは受け取った側が <see cref="DecidePrompt"/> で決める。
    /// 割り込んだら <see cref="RecordPrompt"/> が消す。割り込まなければ、tick のたびに渡し直す。
    /// </remarks>
    private void SetNotice()
    {
        _noticePending = true;
        RaiseNotice();
    }

    /// <summary>
    /// いまの状態から文面を組み立てて渡す。
    /// </summary>
    /// <remarks>
    /// 文面は持ち越さない。渡す直前に組み立てる。
    /// 持ち越すと、狙いが変わったあとに手放した版の「準備ができています」を出す。
    /// </remarks>
    private void RaiseNotice()
    {
        var message = _download is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{_download.Version} の準備ができています")
            : _available?.Version is { } version
                ? string.Create(CultureInfo.InvariantCulture, $"新しい版 {version} が出ています")
                : null;

        if (message is null)
        {
            // 知らせることが無くなった
            _noticePending = false;
            return;
        }

        Notice?.Invoke(this, message);
    }

    /// <summary>確かな答えを得た。途中の更新の確かめ直しを終える。</summary>
    private void MarkAnswered()
    {
        _answeredThisSession = true;
        _unansweredChecks = 0;
        _lastUnansweredAt = null;
    }

    /// <summary>答えを得られなかった。間を空けて確かめ直す。</summary>
    private void MarkUnanswered(DateTimeOffset at)
    {
        _unansweredChecks++;
        _lastUnansweredAt = at;
    }

    /// <summary>自動のインストールを見送ったことと、その理由を残す。</summary>
    private void HoldInstall(UpdateHoldReason reason)
    {
        var text = UpdateGate.Describe(reason);
        if (_state.Stage == UpdateStage.InstallHeld && string.Equals(text, LastMessage, StringComparison.Ordinal))
        {
            return;
        }

        _state = _state with { Stage = UpdateStage.InstallHeld };
        Save(text);
    }

    /// <summary>抱えていた取得物を手放す。ファイルは消さない。</summary>
    private void DropDownload()
    {
        _download?.File.Dispose();
        _download = null;
    }

    private void FailDownload(SemanticVersion version, UpdatePackage package, string message)
    {
        _state = UpdateLedger.RecordDownloadFailure(_state, version.ToString(), package.Sha256, DateTimeOffset.UtcNow);
        Save(message);
    }

    private void Finish(DateTimeOffset wall, TimeSpan monotonic, DateTimeOffset? accepted, UpdateStage stage, string message)
    {
        _state = UpdateLedger.RecordCheck(_state, wall, monotonic, accepted) with { Stage = stage };
        Save(message);
    }

    private void SetStage(UpdateStage stage, string? message = null)
    {
        _state = _state with { Stage = stage };
        if (message is not null)
        {
            LastMessage = message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save(string message)
    {
        LastMessage = message;
        UpdateStateStore.Save(_state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 大きさの上限を決めて読む。
    /// </summary>
    /// <remarks>
    /// マニフェストの取得先が大きな応答を返しても、飲み込まない。
    /// </remarks>
    private async Task<byte[]> GetLimitedAsync(string url, int limit, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();

        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(source, buffer, limit, cancellationToken).ConfigureAwait(true);
        return buffer.ToArray();
    }

    /// <summary>上限を超えたら止めて写す。</summary>
    private static async Task CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(true)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("想定より大きな応答が返りました。");
            }

            await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(true);
        }
    }

    private static string Describe(ManifestVerification result) => result.Verdict switch
    {
        UpdateVerdict.NotNewer => "最新の版です",
        UpdateVerdict.OsTooOld => "新しい版はこのWindowsに対応していません",
        UpdateVerdict.SignatureInvalid => "マニフェストの署名が通りませんでした。更新を見送ります",
        UpdateVerdict.Stale => "古いマニフェストが返りました。更新を見送ります",
        UpdateVerdict.UnknownSchema => "新しい形式のマニフェストです。リリースのページから手で入れてください",
        UpdateVerdict.PackageMissing => "この端末向けの配布物がありません",
        _ => "マニフェストを読めませんでした。更新を見送ります",
    };

    /// <summary>このプロセスの版を読む。</summary>
    /// <remarks>
    /// 読めなければ 0.0.0 とする。すべての版が新しく見え、更新を勧める側に倒れる。
    /// 逆に倒すと、壊れた版が永久に更新されない。
    /// </remarks>
    private static SemanticVersion ReadRunningVersion()
    {
        var text = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (SemanticVersion.TryParse(text, out var version))
        {
            return version;
        }

        _ = SemanticVersion.TryParse("0.0.0", out var fallback);
        return fallback;
    }

    private static string ProcessArch() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>
    /// 端末ごとに決まる位相。
    /// </summary>
    /// <remarks>
    /// 全員が同じ時刻に取りに行かないようにずらす。
    /// 保存せずに毎回同じ値を出せるよう、端末名と利用者名から作る。外へは出さない。
    /// </remarks>
    private static double DevicePhase()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.MachineName}|{Environment.UserName}"));
        return BitConverter.ToUInt32(hash, 0) / ((double)uint.MaxValue + 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tick.Stop();
        DropDownload();
        _http.Dispose();
    }

    /// <summary>
    /// 取得して検証を通したものと、それがどの配布物のものか。
    /// </summary>
    /// <param name="File">掴んでいるファイル。</param>
    /// <param name="Version">取得した版。</param>
    /// <param name="Package">取得した配布物。</param>
    /// <remarks>
    /// 版とSHA-256を取得物の側に持たせる。
    /// 見つけた版の側から引くと、新しい版が見つかった時点で、古い取得物に新しい版の名前が付く。
    /// </remarks>
    private sealed record DownloadedUpdate(UpdateDownload File, SemanticVersion Version, UpdatePackage Package)
    {
        /// <summary>指定の配布物のものか。</summary>
        public bool IsFor(SemanticVersion version, UpdatePackage package) =>
            Version == version && string.Equals(Package.Sha256, package.Sha256, StringComparison.Ordinal);
    }
}
