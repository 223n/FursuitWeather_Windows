using System.Globalization;

namespace FursuitWeather.Core.Update;

/// <summary>
/// インストーラーへ渡す引数を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// <b>引数はマニフェストに入れない。</b>
/// 入れると、マニフェストを乗っ取られたときに
/// 「任意のexeを任意の引数で実行させる」経路になる。
/// ここに直接書く。
/// </para>
/// <para>
/// 個々の引数の理由は <c>docs/update.md</c> の「使う引数」にある。
/// 落とすと無人の更新が止まるものが混ざっているため、勝手に減らさないこと。
/// </para>
/// </remarks>
public static class InstallCommand
{
    /// <summary>「成功したが再起動が要る」を表す終了コード。</summary>
    /// <remarks>
    /// 既定ではこれも0が返り、区別できない。
    /// </remarks>
    public const int RestartExitCode = 3010;

    /// <summary>
    /// 引数を組み立てる。
    /// </summary>
    /// <param name="installDirectory">いま入っている場所。ここへ上書きする。</param>
    /// <param name="processId">終わるのを待たせる自分のPID。</param>
    /// <param name="logPath">ログの場所。書けないときは null を渡す。</param>
    /// <returns>コマンドラインへ渡す文字列。</returns>
    /// <remarks>
    /// <para>
    /// <c>/SP-</c> を落とすと無人の更新が止まる。
    /// <c>/SILENT</c> と <c>/VERYSILENT</c> は起動時の「続行しますか」を抑止せず、
    /// <c>/SUPPRESSMSGBOXES</c> でも抑止されない。
    /// </para>
    /// <para>
    /// <c>/NORESTART</c> は必須である。
    /// <c>/VERYSILENT</c> は再起動が要るとき、確認なしで再起動する。
    /// </para>
    /// <para>
    /// <b><c>/LOG="ファイル名"</c> は、ファイルを作れないとインストーラーが中止する</b>と
    /// 公式に明記されている。起動の前に書き込みを試し、駄目なら null を渡すこと。
    /// </para>
    /// </remarks>
    public static string BuildArguments(string installDirectory, int processId, string? logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        var parts = new List<string>
        {
            // 起動時の「続行しますか」を抑止する。落とすと無人の更新が止まる
            "/SP-",
            "/VERYSILENT",
            // /SILENT か /VERYSILENT との併用でのみ有効
            "/SUPPRESSMSGBOXES",
            // /VERYSILENT は既定で、再起動が要るときに確認なしで再起動する
            "/NORESTART",
            string.Create(CultureInfo.InvariantCulture, $"/RESTARTEXITCODE={RestartExitCode}"),
            "/NOCANCEL",
        };

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            parts.Add(Quote("/LOG=", logPath));
        }

        parts.Add(Quote("/DIR=", installDirectory));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"/WAITPID={processId}"));
        parts.Add("/RELAUNCH=1");

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 値を引用符で囲む。
    /// </summary>
    /// <remarks>
    /// 利用者の名前を含むパスには空白が入りうる。
    /// 引用符の中の引用符は通さない。組み立てを壊す値を弾く。
    /// </remarks>
    private static string Quote(string prefix, string value)
    {
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException($"引用符を含む値は渡せません: {value}", nameof(value));
        }

        return $"{prefix}\"{value}\"";
    }

    /// <summary>
    /// ログを書ける場所かを試す。
    /// </summary>
    /// <param name="logPath">試す場所。</param>
    /// <returns>書けたら true。</returns>
    /// <remarks>
    /// 起動の前に必ず試す。
    /// 書けない場所を <c>/LOG=</c> に渡すと、インストーラーがエラーで中止する。
    /// </remarks>
    public static bool CanWriteLog(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var probe = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                probe.WriteByte(0);
            }

            File.Delete(logPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
