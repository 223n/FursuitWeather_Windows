using System.Globalization;
using System.Security.Cryptography;

namespace FursuitWeather.Core.Update;

/// <summary>
/// 取得した配布物を、実行するまで抱えておく。
/// </summary>
/// <remarks>
/// <para>
/// <b>検証してから実行するまでのあいだ、ファイルを開いたまま持つ。</b>
/// 同じ利用者の権限で動く別のプロセスによる差し替えは、Windowsのセキュリティ境界の外側にある。
/// 目標は解決ではなく、隙を狭めることである。
/// </para>
/// <para>
/// 読み取りだけを許す。書き込みと削除は拒む。
/// プロセスの起動は読み取りと実行で開くため、この共有の仕方で両立する。
/// </para>
/// </remarks>
public sealed class UpdateDownload : IDisposable
{
    private FileStream? _handle;

    /// <summary>この取得を識別する値。ディレクトリの名前になる。</summary>
    public string Id { get; }

    /// <summary>置いてあるディレクトリ。</summary>
    public string Directory { get; }

    /// <summary>ファイルの場所。</summary>
    public string Path { get; }

    internal UpdateDownload(string id, string directory, string path)
    {
        Id = id;
        Directory = directory;
        Path = path;
    }

    /// <summary>書き込み用に開く。</summary>
    /// <returns>書き込み用のストリーム。</returns>
    /// <remarks>
    /// <para>
    /// <c>FileMode.CreateNew</c> で作る。既にあれば失敗させる。
    /// 上書きを許すと、狙って置かれたファイルへ書き足す形になりうる。
    /// </para>
    /// <para>
    /// 書いているあいだは誰にも開かせない。
    /// </para>
    /// </remarks>
    public FileStream OpenForWriting() =>
        new(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>
    /// 検証と実行のあいだ、ファイルを掴んでおく。
    /// </summary>
    /// <returns>掴んだストリーム。</returns>
    /// <remarks>
    /// <para>
    /// 書き終えて閉じてから、読み取り用に開き直す。
    /// managedのAPIでは書き込みの権限を落とせないため、この一瞬だけ隙がある。
    /// ディレクトリの名前を毎回変えているのは、その隙を狙いにくくするためである。
    /// </para>
    /// <para>
    /// <b>掴んだままハッシュを取り、掴んだまま起動する。</b>
    /// 途中でパスから開き直すと、掴んでいる意味が消える。
    /// </para>
    /// </remarks>
    public FileStream Hold()
    {
        _handle?.Dispose();
        _handle = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return _handle;
    }

    /// <summary>
    /// 掴んでいるハンドル越しにSHA-256を取る。
    /// </summary>
    /// <returns>小文字の16進で64文字。</returns>
    /// <exception cref="InvalidOperationException">まだ掴んでいないとき。</exception>
    /// <remarks>パスから開き直さない。開き直すと、掴んだものと違うファイルを測りうる。</remarks>
    public string ComputeSha256()
    {
        if (_handle is null)
        {
            throw new InvalidOperationException("先に Hold() でファイルを掴んでください。");
        }

        _handle.Position = 0;
        var hash = SHA256.HashData(_handle);
        _handle.Position = 0;

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>掴んでいるファイルの大きさ。</summary>
    /// <returns>バイト数。</returns>
    /// <exception cref="InvalidOperationException">まだ掴んでいないとき。</exception>
    public long Length =>
        _handle?.Length ?? throw new InvalidOperationException("先に Hold() でファイルを掴んでください。");

    /// <inheritdoc />
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

/// <summary>
/// 取得した配布物の置き場所を用意する。
/// </summary>
/// <remarks>
/// <para>
/// <c>%TEMP%</c> は使わない。ストレージセンサーに消される可能性がある。
/// </para>
/// <para>
/// 毎回新しい名前のディレクトリを作る。固定のパスを使わない。
/// 置き場所が読めると、検証と実行のあいだを狙われる。
/// </para>
/// </remarks>
public sealed class UpdateDownloadStore
{
    private readonly string _root;

    /// <summary>置き場所の根を決めて作る。</summary>
    /// <param name="root">根のディレクトリ。</param>
    public UpdateDownloadStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <summary>根のディレクトリ。</summary>
    public string Root => _root;

    /// <summary>
    /// 新しい置き場所を作る。
    /// </summary>
    /// <param name="fileName">置くファイルの名前。</param>
    /// <returns>置き場所。</returns>
    /// <remarks>
    /// ディレクトリの名前は毎回変える。
    /// 既に同じ名前があれば作り直す。
    /// </remarks>
    public UpdateDownload Create(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // パスを組み立てる材料に、外から来た名前をそのまま使わない。
        // マニフェストのURLから取った名前が ..\ を含むと、狙った場所へ書けてしまう
        var safeName = System.IO.Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != fileName)
        {
            throw new ArgumentException("ファイル名にディレクトリを含められません。", nameof(fileName));
        }

        string id;
        string directory;
        do
        {
            id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            directory = System.IO.Path.Combine(_root, id);
        }
        while (System.IO.Directory.Exists(directory));

        System.IO.Directory.CreateDirectory(directory);

        return new UpdateDownload(id, directory, System.IO.Path.Combine(directory, safeName));
    }

    /// <summary>
    /// 指定したもの以外の置き場所を消す。
    /// </summary>
    /// <param name="keepId">残すもの。すべて消すなら null。</param>
    /// <remarks>
    /// 取得のたびに古いものが積まれる。200MB近い実体のため、放っておくと膨らむ。
    /// 消せないものがあっても止めない。掴まれている最中かもしれない。
    /// </remarks>
    public void CleanExcept(string? keepId)
    {
        if (!System.IO.Directory.Exists(_root))
        {
            return;
        }

        foreach (var directory in System.IO.Directory.GetDirectories(_root))
        {
            var name = System.IO.Path.GetFileName(directory);
            if (string.Equals(name, keepId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 掴まれている最中かもしれない。次の機会に消す
            }
        }
    }
}
