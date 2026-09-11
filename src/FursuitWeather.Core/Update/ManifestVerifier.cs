using System.Security.Cryptography;
using System.Text.Json;
using FursuitWeather.Core.Api;

namespace FursuitWeather.Core.Update;

/// <summary>マニフェストを調べた結果。</summary>
public enum UpdateVerdict
{
    /// <summary>署名が通らなかった。</summary>
    SignatureInvalid,

    /// <summary>署名は通ったが、内容を読めなかった。</summary>
    Malformed,

    /// <summary>知らないスキーマの版だった。解釈をやめる。</summary>
    UnknownSchema,

    /// <summary>使うチャンネルが入っていなかった。</summary>
    ChannelMissing,

    /// <summary>前回より古いマニフェストだった。巻き戻しの疑い。</summary>
    Stale,

    /// <summary>いまの版と同じか、より古かった。</summary>
    NotNewer,

    /// <summary>このWindowsでは動かない版だった。</summary>
    OsTooOld,

    /// <summary>このアーキテクチャ向けの配布物が無かった。</summary>
    PackageMissing,

    /// <summary>配布物の書き方がおかしい。取得先やハッシュが信用できない。</summary>
    PackageInvalid,

    /// <summary>更新がある。</summary>
    Available,
}

/// <summary>マニフェストを調べた結果。</summary>
/// <param name="Verdict">結論。</param>
/// <param name="Manifest">読めたマニフェスト。読めなければ null。</param>
/// <param name="Channel">使うチャンネル。無ければ null。</param>
/// <param name="Package">取得する配布物。無ければ null。</param>
/// <param name="Version">新しい版。読めなければ null。</param>
/// <param name="IsMandatory">いまの版が必須の下限を割っているか。</param>
/// <param name="IsUnsupported">いまの版がもう使えないか。</param>
public sealed record ManifestVerification(
    UpdateVerdict Verdict,
    UpdateManifest? Manifest,
    UpdateChannel? Channel,
    UpdatePackage? Package,
    SemanticVersion? Version,
    bool IsMandatory,
    bool IsUnsupported)
{
    /// <summary>更新が使える状態か。</summary>
    public bool HasUpdate => Verdict == UpdateVerdict.Available;

    /// <summary>結論だけの結果を作る。</summary>
    /// <param name="verdict">結論。</param>
    /// <returns>結果。</returns>
    public static ManifestVerification Rejected(UpdateVerdict verdict) =>
        new(verdict, null, null, null, null, false, false);
}

/// <summary>
/// 更新のマニフェストを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 信頼の起点はTLSでもAuthenticodeでもなく、自前のリリース署名鍵である。
/// マニフェストにSHA-256を載せるだけでは、マニフェストごと差し替えられたときに無力になる。
/// </para>
/// <para>
/// <b>検証の順序を変えてはいけない。</b>
/// 根拠は <c>docs/update.md</c> の「完全性の検証」にある。
/// </para>
/// <list type="number">
/// <item>署名</item>
/// <item>スキーマの版</item>
/// <item><c>generatedAt</c> が前回より新しいか</item>
/// <item>いまの版より新しいか</item>
/// <item>Windowsのビルド</item>
/// <item>配布物の書き方</item>
/// </list>
/// <para>
/// <b>署名を通す前に、JSONの中身で分岐しない。</b>
/// そのため、この型は最初にバイト列のまま署名を確かめ、通ってから読む。
/// </para>
/// </remarks>
public static class ManifestVerifier
{
    /// <summary>解釈できるスキーマの版。</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>取得先として認めるホスト。</summary>
    /// <remarks>
    /// マニフェストを乗っ取られたときに、任意の場所からexeを引かせないための境目である。
    /// インストーラーの引数も同じ理由でマニフェストに入れない。
    /// </remarks>
    public const string AllowedHost = "github.com";

    private static readonly JsonSerializerOptions Options = FursuitWeatherClient.JsonOptions;

    /// <summary>
    /// マニフェストを検証する。
    /// </summary>
    /// <param name="manifestBytes">取得した <c>update.json</c> のバイト列。</param>
    /// <param name="signature">デタッチ署名（DER）。</param>
    /// <param name="publicKeyPem">埋め込んである公開鍵。</param>
    /// <param name="currentVersion">いま動いている版。</param>
    /// <param name="lastGeneratedAt">前回に受け入れたマニフェストの生成時刻。無ければ null。</param>
    /// <param name="osBuild">WindowsのビルドID。</param>
    /// <param name="arch">このプロセスのアーキテクチャ。<c>x64</c> など。</param>
    /// <returns>検証の結果。</returns>
    public static ManifestVerification Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signature,
        string publicKeyPem,
        SemanticVersion currentVersion,
        DateTimeOffset? lastGeneratedAt,
        int osBuild,
        string arch)
    {
        ArgumentNullException.ThrowIfNull(publicKeyPem);
        ArgumentNullException.ThrowIfNull(arch);

        // ---- 1. 署名。ここを通るまで中身を見ない
        if (!HasValidSignature(manifestBytes, signature, publicKeyPem))
        {
            return ManifestVerification.Rejected(UpdateVerdict.SignatureInvalid);
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Options);
        }
        catch (JsonException)
        {
            return ManifestVerification.Rejected(UpdateVerdict.Malformed);
        }

        if (manifest is null)
        {
            return ManifestVerification.Rejected(UpdateVerdict.Malformed);
        }

        // ---- 2. スキーマの版
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            return ManifestVerification.Rejected(UpdateVerdict.UnknownSchema);
        }

        // ---- 3. 巻き戻しの検知
        // 前回より「古い」ものだけを弾く。同じ時刻は古くない。
        //
        // 新しい版が出るまで、確認のたびに同じマニフェストが返る。
        // 同じものまで弾くと、取得に1度失敗しただけで二度と取り直さなくなる。
        // 署名は偽造できないため、同じものを出し直されても中身は変わらず害が無い。
        // 巻き戻しとは、署名済みの古いマニフェストを出し直されることであり、それは下で止まる
        if (lastGeneratedAt is { } last && manifest.GeneratedAt < last)
        {
            return ManifestVerification.Rejected(UpdateVerdict.Stale);
        }

        if (!manifest.Channels.TryGetValue("stable", out var channel) || channel is null)
        {
            return ManifestVerification.Rejected(UpdateVerdict.ChannelMissing);
        }

        if (!SemanticVersion.TryParse(channel.Version, out var version))
        {
            return ManifestVerification.Rejected(UpdateVerdict.Malformed);
        }

        // 必須と停止の判定は、更新できるかとは別に見る。
        // 「更新は無いが、いまの版はもう使えない」が成り立つ
        var mandatory = IsBelow(currentVersion, channel.MandatoryBelow);
        var unsupported = IsBelow(currentVersion, channel.UnsupportedBelow);

        // ---- 4. いまの版より新しいか
        if (version <= currentVersion)
        {
            return new ManifestVerification(
                UpdateVerdict.NotNewer, manifest, channel, null, version, mandatory, unsupported);
        }

        // ---- 5. Windowsのビルド
        if (channel.Requirements.MinimumOsBuild > osBuild)
        {
            return new ManifestVerification(
                UpdateVerdict.OsTooOld, manifest, channel, null, version, mandatory, unsupported);
        }

        // ---- 6. 配布物
        var package = channel.Packages.FirstOrDefault(p => string.Equals(p.Arch, arch, StringComparison.Ordinal));
        if (package is null)
        {
            return new ManifestVerification(
                UpdateVerdict.PackageMissing, manifest, channel, null, version, mandatory, unsupported);
        }

        if (!IsUsable(package))
        {
            return new ManifestVerification(
                UpdateVerdict.PackageInvalid, manifest, channel, null, version, mandatory, unsupported);
        }

        return new ManifestVerification(
            UpdateVerdict.Available, manifest, channel, package, version, mandatory, unsupported);
    }

    /// <summary>
    /// 署名を確かめる。
    /// </summary>
    /// <param name="manifestBytes">署名の対象。</param>
    /// <param name="signature">デタッチ署名（DER）。</param>
    /// <param name="publicKeyPem">公開鍵。</param>
    /// <returns>通ったら true。</returns>
    /// <remarks>
    /// <para>
    /// 曲線はECDSA P-256。
    /// Ed25519は <c>.NET</c> のBCLに無く、信頼の起点をサードパーティ製のライブラリへ置くのは筋が悪い。
    /// </para>
    /// <para>
    /// 署名の形式はDER（<c>openssl dgst -sha256 -sign</c> が出すもの）にそろえる。
    /// 形式が食い違うと、正しい署名でも通らない。
    /// </para>
    /// <para>
    /// 鍵が読めないときは通さない。失敗は必ず閉じる側へ倒す。
    /// </para>
    /// </remarks>
    private static bool HasValidSignature(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signature,
        string publicKeyPem)
    {
        if (manifestBytes.Length == 0 || signature.Length == 0)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            return key.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>いまの版が、指定の版より古いか。</summary>
    /// <remarks>読めない値は「該当しない」として扱う。壊れた指定で機能を止めないためである。</remarks>
    private static bool IsBelow(SemanticVersion current, string? boundary) =>
        SemanticVersion.TryParse(boundary, out var parsed) && current < parsed;

    /// <summary>
    /// 配布物の書き方を確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取得先は <c>https</c> の <c>github.com</c> に限る。
    /// マニフェストを乗っ取られても、引く先を移せないようにするためである。
    /// </para>
    /// <para>
    /// SHA-256は64文字の16進でなければ、あとの照合が意味を持たない。
    /// 大きさが0以下のものも受け付けない。
    /// </para>
    /// </remarks>
    private static bool IsUsable(UpdatePackage package)
    {
        if (package.Size <= 0)
        {
            return false;
        }

        if (package.Sha256.Length != 64 || !package.Sha256.All(char.IsAsciiHexDigitLower))
        {
            return false;
        }

        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase);
    }
}
