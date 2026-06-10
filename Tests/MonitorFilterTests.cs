using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>MonitorFilter.Matches:0 = 任意维度通配,非零 = 精确匹配。</summary>
public class MonitorFilterTests
{
    [Fact(DisplayName = "0×0:任意分辨率都命中")]
    public void AnyByAny_MatchesEverything()
    {
        var f = new MonitorFilter { Width = 0, Height = 0 };
        Assert.True(f.Matches(7680, 2160));
        Assert.True(f.Matches(1920, 1080));
        Assert.True(f.Matches(1, 1));
    }

    [Fact(DisplayName = "精确分辨率:相等才命中")]
    public void ExactMatch()
    {
        var f = new MonitorFilter { Width = 7680, Height = 2160 };
        Assert.True(f.Matches(7680, 2160));
        Assert.False(f.Matches(7680, 2161));
        Assert.False(f.Matches(3840, 2160));
        Assert.False(f.Matches(1920, 1080));
    }

    [Fact(DisplayName = "仅宽度约束:高度任意")]
    public void WidthOnly()
    {
        var f = new MonitorFilter { Width = 7680, Height = 0 };
        Assert.True(f.Matches(7680, 2160));
        Assert.True(f.Matches(7680, 1080));
        Assert.False(f.Matches(3840, 2160));
    }

    [Fact(DisplayName = "仅高度约束:宽度任意")]
    public void HeightOnly()
    {
        var f = new MonitorFilter { Width = 0, Height = 1080 };
        Assert.True(f.Matches(1920, 1080));
        Assert.True(f.Matches(2560, 1080));
        Assert.False(f.Matches(1920, 2160));
    }
}
