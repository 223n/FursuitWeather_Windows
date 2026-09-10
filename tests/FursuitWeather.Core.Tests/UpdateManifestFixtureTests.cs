using System.Security.Cryptography;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>
/// CIが組み立てる形のマニフェストを、クライアントが受け取れるかを見る。
/// </summary>
/// <remarks>
/// <para>
/// 置いてあるのは <c>.github/workflows/release-publish.yml</c> が
/// <c>jq</c> で組み立てるのと同じ形である。
/// </para>
/// <para>
/// <b>組み立てる側と読む側は別々に書かれている。</b>
/// 片方だけを変えると、配ったあとに「更新が来ない」という形で現れる。
/// ここで形を止めておく。
/// </para>
/// </remarks>
public sealed class UpdateManifestFixtureTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] Manifest() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "update-manifest.json"));

    private ManifestVerification Verify(SemanticVersion current, int osBuild = 26100, string arch = "x64")
    {
        var bytes = Manifest();
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return ManifestVerifier.Verify(
            bytes, signature, _key.ExportSubjectPublicKeyInfoPem(), current, null, osBuild, arch);
    }

    private static SemanticVersion V(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }

    [Fact]
    public void CIが作る形をそのまま受け取れる()
    {
        var result = Verify(V("0.3.0"));

        Assert.Equal(UpdateVerdict.Available, result.Verdict);
        Assert.Equal("0.4.0", result.Version?.ToString());
    }

    [Fact]
    public void 取得先と大きさとハッシュを取り出せる()
    {
        var result = Verify(V("0.3.0"));

        Assert.NotNull(result.Package);
        Assert.Equal("x64", result.Package.Arch);
        Assert.Equal(182452224, result.Package.Size);
        Assert.Equal(64, result.Package.Sha256.Length);
        Assert.StartsWith(
            "https://github.com/223n/FursuitWeather_Windows/releases/download/",
            result.Package.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 版を含む恒久のURLになっている()
    {
        // GitHub Releases の署名付きURLは約1時間で失効するため焼き込めない
        var result = Verify(V("0.3.0"));

        Assert.Contains("/download/v0.4.0/", result.Package!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void 鍵の識別子が埋め込んだものと合っている()
    {
        var result = Verify(V("0.3.0"));

        Assert.Equal(ReleaseKey.KeyId, result.Manifest?.KeyId);
    }

    [Fact]
    public void 必須でも使用停止でもない()
    {
        var result = Verify(V("0.3.0"));

        Assert.False(result.IsMandatory);
        Assert.False(result.IsUnsupported);
    }

    [Fact]
    public void 下限のWindowsが22H2である()
    {
        // 22621 が Windows 11 22H2
        Assert.Equal(22621, Verify(V("0.3.0")).Channel?.Requirements.MinimumOsBuild);

        // それより古い端末へは配らない
        Assert.Equal(UpdateVerdict.OsTooOld, Verify(V("0.3.0"), osBuild: 19045).Verdict);
    }

    [Fact]
    public void 同じ版へは更新しない()
    {
        Assert.Equal(UpdateVerdict.NotNewer, Verify(V("0.4.0")).Verdict);
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
