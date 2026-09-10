namespace FursuitWeather.Core.Update;

/// <summary>
/// 更新のマニフェストを検証する公開鍵。
/// </summary>
/// <remarks>
/// <para>
/// <b>これが信頼の起点である。</b>
/// TLSでもAuthenticodeでもなく、この鍵で署名されているかだけを見る。
/// マニフェストにSHA-256を載せるだけでは、マニフェストごと差し替えられたときに無力になる。
/// </para>
/// <para>
/// 私有鍵はGitHubのEnvironment secret（<c>release</c> 環境の
/// <c>RELEASE_SIGNING_KEY</c>）にあり、承認を通さないと使えない。
/// リポジトリにも開発機にも置かない。
/// </para>
/// <para>
/// <b>鍵を入れ替えるときは <see cref="KeyId"/> も上げる。</b>
/// マニフェストの <c>keyId</c> と照らして、どちらの鍵で署名されたかを見分けられるようにする。
/// 入れ替えの過渡期には、古い鍵も受け入れる期間が要る。
/// </para>
/// </remarks>
public static class ReleaseKey
{
    /// <summary>いま使っている鍵の識別子。</summary>
    public const string KeyId = "fw-2026-01";

    /// <summary>
    /// 公開鍵（ECDSA P-256、SPKIのPEM）。
    /// </summary>
    /// <remarks>
    /// 公開鍵なので、リポジトリへ置いて構わない。
    /// 対になる私有鍵が漏れないかぎり、ここを見ても署名は作れない。
    /// </remarks>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE04SRfJ47a8dZF14WvyXb10xxEayM
        A0F+w4FyB8BqIv7LXMDn8JxvRUlQ598JzL4CjH3PrNY4oEioDuWjlo13dg==
        -----END PUBLIC KEY-----
        """;
}
