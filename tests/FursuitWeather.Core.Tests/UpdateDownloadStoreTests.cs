using System.Security.Cryptography;
using System.Text;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>取得した配布物の抱え方を見る。</summary>
public sealed class UpdateDownloadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "fw-tests-" + Guid.NewGuid().ToString("N"));

    private UpdateDownloadStore Store() => new(_root);

    /// <summary>中身を書き込んだ置き場所を作る。</summary>
    private UpdateDownload Written(string content, string fileName = "setup.exe")
    {
        var download = Store().Create(fileName);
        using (var stream = download.OpenForWriting())
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return download;
    }

    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void 置き場所の名前は毎回変わる()
    {
        // 固定のパスを使うと、検証と実行のあいだを狙われる
        var store = Store();

        var a = store.Create("setup.exe");
        var b = store.Create("setup.exe");

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Directory, b.Directory);
    }

    [Fact]
    public void 同じ場所へ二度は書けない()
    {
        // 上書きを許すと、狙って置かれたファイルへ書き足す形になりうる
        var download = Written("なかみ");

        Assert.Throws<IOException>(() => download.OpenForWriting());
    }

    [Fact]
    public void 書いている間は誰にも開かせない()
    {
        var download = Store().Create("setup.exe");

        using var writing = download.OpenForWriting();

        Assert.ThrowsAny<IOException>(
            () => new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    [Fact]
    public void 掴んだハンドル越しにハッシュを取る()
    {
        using var download = Written("FursuitWeather");
        download.Hold();

        Assert.Equal(Sha256Of("FursuitWeather"), download.ComputeSha256());
    }

    [Fact]
    public void ハッシュを取っても位置が残らない()
    {
        // 続けて読む側が先頭から読めるようにしておく
        using var download = Written("FursuitWeather");
        var handle = download.Hold();

        download.ComputeSha256();

        Assert.Equal(0, handle.Position);
        Assert.Equal(Sha256Of("FursuitWeather"), download.ComputeSha256());
    }

    [Fact]
    public void 掴む前にハッシュを取ろうとしたら止める()
    {
        using var download = Written("なかみ");

        Assert.Throws<InvalidOperationException>(download.ComputeSha256);
        Assert.Throws<InvalidOperationException>(() => download.Length);
    }

    [Fact]
    public void 掴んでいる間は書き換えられない()
    {
        // 検証してから実行するまでの隙を狭めるのが目的である
        using var download = Written("なかみ");
        download.Hold();

        Assert.ThrowsAny<IOException>(
            () => new FileStream(download.Path, FileMode.Open, FileAccess.Write, FileShare.None));
    }

    [Fact]
    public void 掴んでいても読むことはできる()
    {
        // プロセスの起動は読み取りで開く。塞ぐと起動できない
        using var download = Written("なかみ");
        download.Hold();

        using var reader = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(reader.Length > 0);
    }

    [Fact]
    public void 大きさを掴んだハンドルから取れる()
    {
        using var download = Written("12345");
        download.Hold();

        Assert.Equal(5, download.Length);
    }

    [Theory]
    [InlineData("../setup.exe")]
    [InlineData("..\\setup.exe")]
    [InlineData("dir/setup.exe")]
    [InlineData("dir\\setup.exe")]
    [InlineData("C:setup.exe")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("setup	.exe")]
    public void ディレクトリを含む名前は受け付けない(string fileName)
    {
        // マニフェストのURLから取った名前をそのまま使うと、狙った場所へ書けてしまう。
        //
        // Path.GetFileName に頼ると、区切り文字の扱いが環境で変わる。
        // Linux では円記号が区切りではないため、上から2つ目がそのまま通る。
        // 実際に Linux のランナーでこのテストが落ちた
        Assert.ThrowsAny<ArgumentException>(() => Store().Create(fileName));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("FursuitWeather-0.4.0-x64-setup.exe")]
    public void ふつうの名前は受け付ける(string fileName)
    {
        var download = Store().Create(fileName);

        Assert.EndsWith(fileName, download.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void 残すもの以外を消す()
    {
        var store = Store();
        var keep = Written("のこす");
        var drop = Written("けす");

        store.CleanExcept(keep.Id);

        Assert.True(Directory.Exists(keep.Directory));
        Assert.False(Directory.Exists(drop.Directory));
    }

    [Fact]
    public void 掴まれているものは消せなくても止まらない()
    {
        var store = Store();
        using var held = Written("つかむ");
        held.Hold();

        // 例外を投げずに戻ること。次の機会に消せばよい
        store.CleanExcept(null);
    }

    [Fact]
    public void 根が無くても掃除で落ちない()
    {
        new UpdateDownloadStore(Path.Combine(_root, "まだ無い")).CleanExcept(null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 掴まれたままのものが残ることがある。テストの後始末なので止めない
        }
    }
}
