using FursuitWeather.Core.Display;
using FursuitWeather.Core.Models;

namespace FursuitWeather.Core.Tests;

/// <summary>配色と記号の引き方と、もしものときの条件を見る。</summary>
public sealed class DisplayToneTests
{
    [Theory]
    [InlineData("coldDanger", true)]
    [InlineData("coldWarning", true)]
    [InlineData("coldCaution", true)]
    [InlineData("optimal", false)]
    [InlineData("warning", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 配色に使う低温は名前がcoldで始まるものだけ(string? level, bool expected)
    {
        // optimal を低温に塗らない。本体のWebとMac版と同じ
        Assert.Equal(expected, DisplayTone.IsColdLevel(level));
    }

    [Theory]
    [InlineData("warning", 2, LevelTone.Grade2)]
    [InlineData("danger", 4, LevelTone.Grade4)]
    [InlineData("coldDanger", 4, LevelTone.Cold)]
    [InlineData("optimal", 0, LevelTone.Grade0)]
    [InlineData("warning", 9, LevelTone.Grade4)]
    [InlineData("warning", -1, LevelTone.Grade0)]
    public void 配色の区分を引く(string level, int grade, LevelTone expected)
    {
        Assert.Equal(expected, DisplayTone.From(level, grade));
    }

    [Theory]
    [InlineData("optimal", 0, "◎")]
    [InlineData("caution", 1, "○")]
    [InlineData("warning", 2, "△")]
    [InlineData("severeWarning", 3, "✕")]
    [InlineData("danger", 4, "✕")]
    public void 記号を引く(string level, int grade, string expected)
    {
        Assert.Equal(expected, DisplayTone.Symbol(level, grade));
    }

    [Theory]
    [InlineData("coldCaution", 1, "❄○")]
    [InlineData("coldWarning", 2, "❄△")]
    [InlineData("coldDanger", 4, "❄✕")]
    public void 低温でもgradeの記号を落とさない(string level, int grade, string expected)
    {
        // 落とすと、低温の注意と警戒と危険が同じ記号になる。本体のバッジも両方を並べている
        Assert.Equal(expected, DisplayTone.Symbol(level, grade));
    }

    [Fact]
    public void gradeの記号は範囲の外を丸める()
    {
        Assert.Equal("◎", DisplayTone.GradeSymbol(-3));
        Assert.Equal("✕", DisplayTone.GradeSymbol(12));
    }

    // ---- もしものとき

    private static HourForecast Hour(string level, int grade) => new()
    {
        Outdoor = new ActivityAssessment { Level = level, Grade = grade },
    };

    [Fact]
    public void もしものときの手順は5つ()
    {
        Assert.Equal(5, EmergencySteps.Items.Count);
        Assert.Equal("意識を確認", EmergencySteps.Items[0].Title);
        Assert.Contains("119番", EmergencySteps.Items[0].Detail, StringComparison.Ordinal);
        Assert.EndsWith("/emergency", EmergencySteps.MoreInfo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("severeWarning", 3, true)]
    [InlineData("danger", 4, true)]
    [InlineData("warning", 2, false)]
    [InlineData("coldDanger", 4, false)]
    public void もしものときはgrade3以上で低温でないときに加える(string level, int grade, bool expected)
    {
        // 低温の grade 4 に熱中症の手順を出すのは誤誘導になる
        Assert.Equal(expected, EmergencySteps.ShouldShow(Hour(level, grade)));
    }

    [Fact]
    public void 判定が無ければもしものときを加えない()
    {
        Assert.False(EmergencySteps.ShouldShow(null));
    }
}
