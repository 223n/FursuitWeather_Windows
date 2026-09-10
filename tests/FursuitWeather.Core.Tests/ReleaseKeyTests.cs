using System.Security.Cryptography;
using System.Text;
using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>埋め込んだ公開鍵を見る。</summary>
/// <remarks>
/// 貼り付けを誤ると、更新が丸ごと届かなくなる。
/// しかも壊れ方が「署名が通らない」なので、原因が鍵にあると気付きにくい。
/// </remarks>
public sealed class ReleaseKeyTests
{
    [Fact]
    public void 埋め込んだ鍵を読み込める()
    {
        using var key = ECDsa.Create();

        // 改行や余白が崩れていればここで落ちる
        key.ImportFromPem(ReleaseKey.PublicKeyPem);

        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void 曲線がP256である()
    {
        // 署名する側は openssl の prime256v1 で作る。食い違うと検証が通らない
        using var key = ECDsa.Create();
        key.ImportFromPem(ReleaseKey.PublicKeyPem);

        var parameters = key.ExportParameters(includePrivateParameters: false);

        Assert.Equal(
            ECCurve.NamedCurves.nistP256.Oid.Value,
            parameters.Curve.Oid.Value);
    }

    [Fact]
    public void 私有鍵が混ざっていない()
    {
        // 貼り間違いでこちらを入れると、リポジトリへ私有鍵が入る
        Assert.DoesNotContain("PRIVATE", ReleaseKey.PublicKeyPem, StringComparison.Ordinal);
        Assert.Contains("BEGIN PUBLIC KEY", ReleaseKey.PublicKeyPem, StringComparison.Ordinal);
    }

    [Fact]
    public void 鍵の識別子がある()
    {
        // 入れ替えの余地。最初から持たないと後から足せない
        Assert.False(string.IsNullOrWhiteSpace(ReleaseKey.KeyId));
    }

    [Fact]
    public void この鍵では他人の署名を通さない()
    {
        // 埋め込んだ鍵が実際に検証へ効いていることを、経路ごと確かめる
        var payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = stranger.SignData(
            payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(SemanticVersion.TryParse("0.0.1", out var current));
        var result = ManifestVerifier.Verify(
            payload, signature, ReleaseKey.PublicKeyPem, current, null, 26100, "x64");

        Assert.Equal(UpdateVerdict.SignatureInvalid, result.Verdict);
    }
}
