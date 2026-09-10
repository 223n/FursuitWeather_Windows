using FursuitWeather.Core.Update;

namespace FursuitWeather.Core.Tests;

/// <summary>版の比較を見る。</summary>
public sealed class SemanticVersionTests
{
    private static SemanticVersion V(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version), $"{text} を読めませんでした。");
        return version;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("0.3.0-rc.2", 0, 3, 0, "rc.2")]
    [InlineData("10.20.30", 10, 20, 30, "")]
    public void 版を読める(string text, int major, int minor, int patch, string preRelease)
    {
        var version = V(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Fact]
    public void ビルドメタデータは比較に使わない()
    {
        // Directory.Build.props が InformationalVersion に付ける形である。
        // 実際に 0.3.0-rc.1+3ab1107... という値がファイルのプロパティに出ている
        var withMetadata = V("0.3.0-rc.1+3ab1107863921264ed753a9247c88a284fd43775");
        var without = V("0.3.0-rc.1");

        Assert.Equal(0, withMetadata.CompareTo(without));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("-1.2.3")]
    public void 読めない値は偽を返す(string text)
    {
        // 更新の判断がこの比較に乗る。読めない値を通すと巻き戻しの防止が崩れる
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void 読めない値でnullを渡しても落ちない()
    {
        Assert.False(SemanticVersion.TryParse(null, out _));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("0.3.0-rc.2", "0.3.0")]
    [InlineData("0.3.0", "0.4.0")]
    public void 小さい順に並ぶ(string smaller, string larger)
    {
        Assert.True(V(smaller) < V(larger), $"{smaller} < {larger} になりませんでした。");
        Assert.True(V(larger) > V(smaller));
        Assert.True(V(smaller) <= V(larger));
        Assert.True(V(larger) >= V(smaller));
    }

    [Fact]
    public void 数の識別子は桁数ではなく値で比べる()
    {
        // 文字列のまま比べると rc.10 が rc.2 より小さくなる
        Assert.True(V("1.0.0-rc.2") < V("1.0.0-rc.10"));
    }

    [Fact]
    public void 正式版はプレリリースより新しい()
    {
        // これを取り違えると、正式版を出したのに更新が届かない
        Assert.True(V("0.3.0") > V("0.3.0-rc.2"));
    }

    [Fact]
    public void 同じ版は等しい()
    {
        Assert.Equal(0, V("1.2.3").CompareTo(V("1.2.3")));
        Assert.Equal(0, V("1.2.3-rc.1").CompareTo(V("1.2.3-rc.1")));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.3.0-rc.2")]
    public void 文字にして読み直しても変わらない(string text)
    {
        Assert.Equal(text, V(text).ToString());
    }
}
