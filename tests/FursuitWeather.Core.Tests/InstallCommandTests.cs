using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>インストーラーへ渡す引数を見る。</summary>
public sealed class InstallCommandTests
{
    private const string InstallDir = @"C:\Users\223n\AppData\Local\Programs\FursuitWeather";

    [Fact]
    public void 落とすと無人の更新が止まる引数が入っている()
    {
        var args = InstallCommand.BuildArguments(InstallDir, 1234, null);

        // /SILENT と /VERYSILENT は起動時の「続行しますか」を抑止しない。
        // /SUPPRESSMSGBOXES でも抑止されない。これを落とすと無人で止まる
        Assert.Contains("/SP-", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 確認なしの再起動を止める引数が入っている()
    {
        var args = InstallCommand.BuildArguments(InstallDir, 1234, null);

        // /VERYSILENT は再起動が要るとき、確認なしで再起動する
        Assert.Contains("/NORESTART", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 再起動が要る成功を見分ける終了コードを渡す()
    {
        var args = InstallCommand.BuildArguments(InstallDir, 1234, null);

        // 既定では「成功して再起動が要る」も0が返り、区別できない
        Assert.Contains("/RESTARTEXITCODE=3010", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 静かに実行する引数がそろっている()
    {
        var args = InstallCommand.BuildArguments(InstallDir, 1234, null);

        Assert.Contains("/VERYSILENT", args, StringComparison.Ordinal);
        // /SILENT か /VERYSILENT との併用でのみ有効
        Assert.Contains("/SUPPRESSMSGBOXES", args, StringComparison.Ordinal);
        Assert.Contains("/NOCANCEL", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 自分のPIDと入れ先を渡す()
    {
        var args = InstallCommand.BuildArguments(InstallDir, 4321, null);

        Assert.Contains("/WAITPID=4321", args, StringComparison.Ordinal);
        Assert.Contains($"/DIR=\"{InstallDir}\"", args, StringComparison.Ordinal);
        Assert.Contains("/RELAUNCH=1", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 空白を含むパスを引用符で囲む()
    {
        var path = @"C:\Users\山田 太郎\AppData\Local\Programs\FursuitWeather";

        var args = InstallCommand.BuildArguments(path, 1, null);

        Assert.Contains($"/DIR=\"{path}\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void ログの場所を渡せる()
    {
        var log = @"C:\Users\223n\AppData\Local\FursuitWeather\logs\install.log";

        var args = InstallCommand.BuildArguments(InstallDir, 1, log);

        Assert.Contains($"/LOG=\"{log}\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void ログを書けないときは引数ごと外す()
    {
        // /LOG="ファイル名" は、作れないとインストーラーがエラーで中止する
        var args = InstallCommand.BuildArguments(InstallDir, 1, null);

        Assert.DoesNotContain("/LOG=", args, StringComparison.Ordinal);
    }

    [Fact]
    public void 空のログの場所も外す()
    {
        Assert.DoesNotContain("/LOG=", InstallCommand.BuildArguments(InstallDir, 1, "   "), StringComparison.Ordinal);
    }

    [Fact]
    public void 引用符を含む値は組み立てを壊すので弾く()
    {
        Assert.Throws<ArgumentException>(
            () => InstallCommand.BuildArguments(@"C:\a""b", 1, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PIDが無い値なら弾く(int processId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InstallCommand.BuildArguments(InstallDir, processId, null));
    }

    [Fact]
    public void 入れ先が空なら弾く()
    {
        Assert.ThrowsAny<ArgumentException>(() => InstallCommand.BuildArguments("  ", 1, null));
    }

    [Fact]
    public void 書ける場所なら真を返す()
    {
        var log = Path.Combine(Path.GetTempPath(), "fw-tests-" + Guid.NewGuid().ToString("N"), "install.log");

        Assert.True(InstallCommand.CanWriteLog(log));
        // 試したファイルは残さない
        Assert.False(File.Exists(log));

        Directory.Delete(Path.GetDirectoryName(log)!, recursive: true);
    }

    [Fact]
    public void 書けない場所なら偽を返す()
    {
        // 起動の前に必ず試す。書けない場所を渡すとインストーラーが中止する
        Assert.False(InstallCommand.CanWriteLog(string.Empty));
        Assert.False(InstallCommand.CanWriteLog("   "));
    }
}
