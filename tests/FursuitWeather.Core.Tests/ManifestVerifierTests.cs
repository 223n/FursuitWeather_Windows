using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>更新のマニフェストの検証を見る。</summary>
/// <remarks>
/// 鍵はテストの中で作る。本物の私有鍵はリポジトリにも手元にも置かない。
/// </remarks>
public sealed class ManifestVerifierTests : IDisposable
{
    private const int OsBuild = 26100;
    private const string Arch = "x64";

    private static readonly SemanticVersion Current = Parse("0.3.0");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKeyPem;

    public ManifestVerifierTests()
    {
        _publicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
    }

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }

    /// <summary>署名まで済んだマニフェストを作る。</summary>
    private (byte[] Bytes, byte[] Signature) Sign(object manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (bytes, signature);
    }

    /// <summary>ふつうのマニフェストを作る。</summary>
    private static object Manifest(
        string version = "0.4.0",
        int schemaVersion = 1,
        string generatedAt = "2026-09-10T04:00:00Z",
        int minimumOsBuild = 22621,
        string arch = Arch,
        string url = "https://github.com/223n/FursuitWeather_Windows/releases/download/v0.4.0/setup.exe",
        long size = 182452224,
        string? sha256 = null,
        string? mandatoryBelow = null,
        string? unsupportedBelow = null,
        string severity = "normal") => new
        {
            schemaVersion,
            generatedAt,
            keyId = "fw-2026-01",
            channels = new
            {
                stable = new
                {
                    version,
                    releasedAt = "2026-09-10",
                    notesUrl = "https://github.com/223n/FursuitWeather_Windows/releases/tag/v0.4.0",
                    severity,
                    mandatoryBelow,
                    unsupportedBelow,
                    requirements = new { minimumOsBuild },
                    packages = new[]
                    {
                        new
                        {
                            arch,
                            url,
                            size,
                            sha256 = sha256 ?? new string('a', 64),
                        },
                    },
                },
            },
        };

    private ManifestVerification Run(
        object manifest,
        DateTimeOffset? lastGeneratedAt = null,
        SemanticVersion? current = null,
        int osBuild = OsBuild)
    {
        var (bytes, signature) = Sign(manifest);
        return ManifestVerifier.Verify(
            bytes, signature, _publicKeyPem, current ?? Current, lastGeneratedAt, osBuild, Arch);
    }

    [Fact]
    public void 正しいマニフェストは更新ありになる()
    {
        var result = Run(Manifest());

        Assert.Equal(UpdateVerdict.Available, result.Verdict);
        Assert.True(result.HasUpdate);
        Assert.Equal("0.4.0", result.Version?.ToString());
        Assert.NotNull(result.Package);
        Assert.Equal(Arch, result.Package.Arch);
    }

    [Fact]
    public void 署名が違えば中身を読まない()
    {
        // 信頼の起点は署名だけである。ここが緩むと、あとの検査はすべて無意味になる
        var (bytes, _) = Sign(Manifest());
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrong = other.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var result = ManifestVerifier.Verify(bytes, wrong, _publicKeyPem, Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.SignatureInvalid, result.Verdict);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void 中身を1バイト変えると通らない()
    {
        var (bytes, signature) = Sign(Manifest());
        bytes[^2]++;

        var result = ManifestVerifier.Verify(bytes, signature, _publicKeyPem, Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.SignatureInvalid, result.Verdict);
    }

    [Fact]
    public void 署名が空なら通らない()
    {
        var (bytes, _) = Sign(Manifest());

        var result = ManifestVerifier.Verify(bytes, [], _publicKeyPem, Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.SignatureInvalid, result.Verdict);
    }

    [Fact]
    public void 公開鍵が壊れていれば通らない()
    {
        // 失敗は必ず閉じる側へ倒す
        var (bytes, signature) = Sign(Manifest());

        var result = ManifestVerifier.Verify(bytes, signature, "これは鍵ではない", Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.SignatureInvalid, result.Verdict);
    }

    [Fact]
    public void 知らないスキーマなら解釈をやめる()
    {
        var result = Run(Manifest(schemaVersion: 2));

        Assert.Equal(UpdateVerdict.UnknownSchema, result.Verdict);
    }

    [Fact]
    public void 前回より古いマニフェストは無視する()
    {
        // 巻き戻しの検知。古い版を新しいと言い張る差し替えを弾く
        var last = DateTimeOffset.Parse("2026-09-10T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var result = Run(Manifest(generatedAt: "2026-09-10T04:00:00Z"), lastGeneratedAt: last);

        Assert.Equal(UpdateVerdict.Stale, result.Verdict);
    }

    [Fact]
    public void 前回と同じ時刻のマニフェストは受け入れる()
    {
        // 新しい版が出るまで、確認のたびに同じマニフェストが返る。
        // これを弾くと、取得に1度失敗しただけで二度と取り直さなくなる
        var same = DateTimeOffset.Parse("2026-09-10T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var result = Run(Manifest(generatedAt: "2026-09-10T04:00:00Z"), lastGeneratedAt: same);

        Assert.Equal(UpdateVerdict.Available, result.Verdict);
    }

    [Fact]
    public void 取得に失敗したあとの確認でも同じ版を取り直せる()
    {
        // 検証器と記録の組み合わせで起きる筋書きをそのまま通す。
        // 1回目で受け入れた時刻を覚え、取得が失敗し、2回目で同じマニフェストが返る
        var (bytes, signature) = Sign(Manifest());
        var first = ManifestVerifier.Verify(bytes, signature, _publicKeyPem, Current, null, OsBuild, Arch);
        Assert.Equal(UpdateVerdict.Available, first.Verdict);

        var state = UpdateLedger.RecordCheck(
            new UpdateState(), DateTimeOffset.UtcNow, TimeSpan.Zero, first.Manifest!.GeneratedAt);

        var second = ManifestVerifier.Verify(
            bytes, signature, _publicKeyPem, Current, state.LastManifestGeneratedAt, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.Available, second.Verdict);
    }

    [Fact]
    public void 前回より1秒でも古いマニフェストは弾く()
    {
        // 巻き戻しとは、署名済みの古いマニフェストを出し直されること
        var last = DateTimeOffset.Parse("2026-09-10T04:00:01Z", System.Globalization.CultureInfo.InvariantCulture);

        var result = Run(Manifest(generatedAt: "2026-09-10T04:00:00Z"), lastGeneratedAt: last);

        Assert.Equal(UpdateVerdict.Stale, result.Verdict);
    }

    [Fact]
    public void いまの版と同じなら更新しない()
    {
        var result = Run(Manifest(version: "0.3.0"));

        Assert.Equal(UpdateVerdict.NotNewer, result.Verdict);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public void 古い版へは戻さない()
    {
        // 署名が通っていても、古い版を配られたら降りない
        var result = Run(Manifest(version: "0.2.0"));

        Assert.Equal(UpdateVerdict.NotNewer, result.Verdict);
    }

    [Fact]
    public void プレリリースは正式版より新しくならない()
    {
        var result = Run(Manifest(version: "0.3.0-rc.9"));

        Assert.Equal(UpdateVerdict.NotNewer, result.Verdict);
    }

    [Fact]
    public void Windowsが古ければ配らない()
    {
        var result = Run(Manifest(minimumOsBuild: 99999));

        Assert.Equal(UpdateVerdict.OsTooOld, result.Verdict);
    }

    [Fact]
    public void 別のアーキテクチャしか無ければ配らない()
    {
        var result = Run(Manifest(arch: "arm64"));

        Assert.Equal(UpdateVerdict.PackageMissing, result.Verdict);
    }

    [Theory]
    [InlineData("http://github.com/223n/x/releases/download/v0.4.0/setup.exe")]
    [InlineData("https://example.com/setup.exe")]
    [InlineData("https://github.com.example.com/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("これはURLではない")]
    public void 取得先が想定の場所でなければ配らない(string url)
    {
        // マニフェストを乗っ取られても、引く先を移せないようにする境目である
        var result = Run(Manifest(url: url));

        Assert.Equal(UpdateVerdict.PackageInvalid, result.Verdict);
    }

    [Theory]
    [InlineData("https://github.com/223n/x/releases/download/v0.4.0/")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/223n/x/releases/download/v0.4.0/..")]
    public void 置くときの名前を取り出せなければ配らない(string url)
    {
        // 通すと取得の段で例外になり、確認のたびにアプリが落ちる
        var result = Run(Manifest(url: url));

        Assert.Equal(UpdateVerdict.PackageInvalid, result.Verdict);
    }

    [Fact]
    public void 置くときの名前はURLの最後の区切りから取る()
    {
        var package = new UpdatePackage
        {
            Url = "https://github.com/223n/FursuitWeather_Windows/releases/download/v0.4.0/FursuitWeather-0.4.0-x64-setup.exe",
        };

        Assert.Equal("FursuitWeather-0.4.0-x64-setup.exe", ManifestVerifier.PackageFileName(package));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ハッシュの書き方がおかしければ配らない(string sha256)
    {
        // 64文字の小文字の16進でなければ、あとの照合が意味を持たない
        var result = Run(Manifest(sha256: sha256));

        Assert.Equal(UpdateVerdict.PackageInvalid, result.Verdict);
    }

    [Fact]
    public void 大きさが無ければ配らない()
    {
        var result = Run(Manifest(size: 0));

        Assert.Equal(UpdateVerdict.PackageInvalid, result.Verdict);
    }

    [Fact]
    public void 必須の下限を割っていることを伝える()
    {
        var result = Run(Manifest(mandatoryBelow: "0.4.0"));

        Assert.True(result.IsMandatory);
        Assert.Equal(UpdateVerdict.Available, result.Verdict);
    }

    [Fact]
    public void 使用を止める指定を更新が無いときでも伝える()
    {
        // 「更新は無いが、いまの版はもう使えない」が成り立つ
        var result = Run(Manifest(version: "0.3.0", unsupportedBelow: "0.4.0"));

        Assert.Equal(UpdateVerdict.NotNewer, result.Verdict);
        Assert.True(result.IsUnsupported);
    }

    [Fact]
    public void 下限の書き方がおかしければ該当しないとみなす()
    {
        // 壊れた指定で機能を止めない
        var result = Run(Manifest(mandatoryBelow: "こわれた値", unsupportedBelow: "9"));

        Assert.False(result.IsMandatory);
        Assert.False(result.IsUnsupported);
    }

    [Fact]
    public void 重さを読み取れる()
    {
        var result = Run(Manifest(severity: "security"));

        Assert.Equal(UpdateSeverity.Security, result.Channel?.SeverityId);
    }

    [Fact]
    public void 知らない重さは不明として扱う()
    {
        var result = Run(Manifest(severity: "こわれた値"));

        Assert.Equal(UpdateSeverity.Unknown, result.Channel?.SeverityId);
        // 重さが読めなくても更新自体は止めない
        Assert.Equal(UpdateVerdict.Available, result.Verdict);
    }

    [Fact]
    public void 使うチャンネルが無ければ配らない()
    {
        var (bytes, signature) = Sign(new
        {
            schemaVersion = 1,
            generatedAt = "2026-09-10T04:00:00Z",
            keyId = "fw-2026-01",
            channels = new { beta = new { version = "0.4.0" } },
        });

        var result = ManifestVerifier.Verify(bytes, signature, _publicKeyPem, Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.ChannelMissing, result.Verdict);
    }

    [Fact]
    public void 読めないJSONは弾く()
    {
        var bytes = Encoding.UTF8.GetBytes("{ これはJSONではない");
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var result = ManifestVerifier.Verify(bytes, signature, _publicKeyPem, Current, null, OsBuild, Arch);

        Assert.Equal(UpdateVerdict.Malformed, result.Verdict);
    }

    [Fact]
    public void 版の書き方がおかしければ弾く()
    {
        var result = Run(Manifest(version: "こわれた値"));

        Assert.Equal(UpdateVerdict.Malformed, result.Verdict);
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
