using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FursuitWeather.Core.Update;

/// <summary>
/// 版の比較。
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Version</c> はプレリリースの識別子を持てない。
/// このリポジトリは <c>0.3.0-rc.2</c> のような版を実際に出しているため、使えない。
/// </para>
/// <para>
/// 比べ方は semver.org の11項に従う。
/// 識別子を <c>.</c> で分け、数字どうしは数値で、英字を含むものは文字列で比べる。
/// 数字と英字が並んだときは数字が小さい。
/// 中身が同じなら、正式版がプレリリースより大きい。
/// </para>
/// <para>
/// <b>更新の判断はこの比較に乗る。</b>
/// 巻き戻しを防ぐのが目的のため、曖昧な比較をしてはいけない。
/// </para>
/// </remarks>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    /// <summary>メジャー。</summary>
    public int Major { get; }

    /// <summary>マイナー。</summary>
    public int Minor { get; }

    /// <summary>パッチ。</summary>
    public int Patch { get; }

    /// <summary>プレリリースの識別子。正式版のときは空。</summary>
    public string PreRelease { get; }

    private SemanticVersion(int major, int minor, int patch, string preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    /// <summary>プレリリースかどうか。</summary>
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>
    /// 版の文字列を読む。
    /// </summary>
    /// <param name="value">読む文字列。先頭の <c>v</c> は落とす。</param>
    /// <param name="version">読めた版。</param>
    /// <returns>読めたら true。</returns>
    /// <remarks>
    /// ビルドメタデータ（<c>+</c> のあと）は落とす。
    /// semver は比較に使わないと定めており、実際 <c>0.3.0-rc.1+abc</c> のような値が付く。
    /// </remarks>
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // ビルドメタデータは比較に使わない
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var preRelease = string.Empty;
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];

            // 空の識別子（1.0.0- や 1.0.0-a..b）は通さない
            if (preRelease.Length == 0 || preRelease.Split('.').Any(string.IsNullOrEmpty))
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseNumber(parts[0], out var major) ||
            !TryParseNumber(parts[1], out var minor) ||
            !TryParseNumber(parts[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>数字を読む。先頭の0は通さない。</summary>
    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;

        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// プレリリースの識別子を比べる。
    /// </summary>
    /// <remarks>
    /// 中身が同じなら、プレリリースを持たないほうが大きい。
    /// <c>1.0.0</c> は <c>1.0.0-rc.1</c> より新しい。
    /// </remarks>
    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        // 正式版のほうが大きい
        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            // 識別子の数が少ないほうが小さい
            if (i >= a.Length)
            {
                return -1;
            }

            if (i >= b.Length)
            {
                return 1;
            }

            var compared = CompareIdentifier(a[i], b[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return 0;
    }

    /// <summary>識別子を1つ比べる。</summary>
    /// <remarks>数字どうしは数値で、それ以外は文字列で比べる。数字は英字より小さい。</remarks>
    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            // 桁数が違えば大きいほうが大きい。同じなら文字列の比較でよい
            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    /// <summary>左が右より小さいか。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>小さければ true。</returns>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>左が右より大きいか。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>大きければ true。</returns>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>左が右以下か。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>以下なら true。</returns>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>左が右以上か。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>以上なら true。</returns>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    [SuppressMessage(
        "Globalization",
        "CA1305:IFormatProvider を指定します",
        Justification = "数値は不変文化圏で組み立てており、書式化の文化圏に依存しない")]
    public override string ToString() =>
        IsPreRelease
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
