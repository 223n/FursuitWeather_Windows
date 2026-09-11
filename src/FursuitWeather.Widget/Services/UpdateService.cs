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
    private UpdateDownload? _download;
    private bool _busy;
    private bool _disposed;

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
    public bool HasDownloadedUpdate => _download is not null && _available?.Version is not null;

    /// <summary>見つかった更新の版。無ければ null。</summary>
    public SemanticVersion? AvailableVersion => _available?.Version;

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
    /// 本体を起動し直すのはインストーラーの <c>[Run]</c> である。
    /// ここで狙った版と照らし、成功か中断かを決める。
    /// </remarks>
    public InstallOutcome ReconcileAtStartup()
    {
        var (next, outcome) = UpdateLedger.Reconcile(_state, Running, DateTimeOffset.UtcNow);
        if (outcome != InstallOutcome.None)
        {
            _state = next;
            UpdateStateStore.Save(_state);

            LastMessage = outcome == InstallOutcome.Succeeded
                ? string.Create(CultureInfo.InvariantCulture, $"{Running} に更新しました")
                : "前回のインストールが途中で終わりました。次は確認してから実行します";
        }

        // 置いたままのものを片付ける。次に使うのは新しく取ったものだけである
        _store.CleanExcept(null);
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

        var due = UpdateCheckSchedule.IsDue(
            _state,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            UpdateEnvironment.Read(0).Uptime,
            _phase);

        if (due)
        {
            await CheckAsync(manual: false).ConfigureAwait(true);
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
                Finish(wall, monotonic, accepted: null, UpdateStage.Idle, "公開されている安定版はまだありません");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                Finish(wall, monotonic, accepted: null, UpdateStage.Idle, "確認できませんでした。回線の状態を確かめてください");
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

            if (!result.HasUpdate)
            {
                Finish(wall, monotonic, result.Manifest?.GeneratedAt, UpdateStage.Idle, Describe(result));
                return;
            }

            _available = result;
            Finish(
                wall,
                monotonic,
                result.Manifest?.GeneratedAt,
                UpdateStage.UpdateAvailable,
                string.Create(CultureInfo.InvariantCulture, $"{result.Version} が出ています"));

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
    /// </remarks>
    private async Task DownloadCoreAsync(bool manual)
    {
        if (_available?.Package is not { } package || _available.Version is not { } version)
        {
            return;
        }

        if (!manual)
        {
            var gate = UpdateGate.ForDownload(_state, UpdateEnvironment.Read(package.Size), DateTimeOffset.UtcNow);
            if (!gate.CanProceed)
            {
                var stage = gate.Outcome == GateOutcome.Hold ? UpdateStage.DownloadHeld : UpdateStage.UpdateAvailable;
                SetStage(stage, gate.Outcome == GateOutcome.Hold
                    ? UpdateGate.Describe(gate.Reason)
                    : string.Create(CultureInfo.InvariantCulture, $"{version} が出ています。「今すぐ取得」で受け取れます"));
                Notice?.Invoke(this, string.Create(CultureInfo.InvariantCulture, $"新しい版 {version} が出ています"));
                return;
            }
        }

        SetStage(UpdateStage.Downloading, string.Create(CultureInfo.InvariantCulture, $"{version} を取得しています"));

        // 前に抱えていたものは手放してから掃除する。掴んだままだと消せない
        _download?.Dispose();
        _download = null;
        _store.CleanExcept(null);

        var fileName = Path.GetFileName(new Uri(package.Url).AbsolutePath);
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

            if (download.Length != package.Size ||
                !string.Equals(download.ComputeSha256(), package.Sha256, StringComparison.Ordinal))
            {
                download.Dispose();
                FailDownload(version, package, "取得したファイルがマニフェストと一致しません。破損か改ざんの可能性があります");
                return;
            }

            _download = download;
            download = null;

            _state = _state with { Stage = UpdateStage.Downloaded };
            Save(string.Create(CultureInfo.InvariantCulture, $"{version} の準備ができました"));
            Notice?.Invoke(this, string.Create(CultureInfo.InvariantCulture, $"{version} の準備ができました"));

            if (!manual && _state.Mode == UpdateMode.Automatic)
            {
                InstallReady?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            download?.Dispose();
            FailDownload(version, package, "取得できませんでした。あとで試し直します");
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
        if (_download is null || _available?.Version is not { } version || _available.Package is not { } package)
        {
            return "取得済みの更新がありません";
        }

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
                return gate.Outcome == GateOutcome.Hold ? UpdateGate.Describe(gate.Reason) : "押されるのを待っています";
            }
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
            Process.Start(new ProcessStartInfo(_download.Path, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = _download.Directory,
            });
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
        _download?.Dispose();
        _http.Dispose();
    }
}
