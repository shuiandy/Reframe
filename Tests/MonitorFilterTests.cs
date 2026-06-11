using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>MonitorFilter.Matches: 0 = wildcard on that dimension, non-zero = exact match.</summary>
public class MonitorFilterTests
{
    [Fact(DisplayName = "0×0: matches any resolution")]
    public void AnyByAny_MatchesEverything()
    {
        var f = new MonitorFilter { Width = 0, Height = 0 };
        Assert.True(f.Matches(7680, 2160));
        Assert.True(f.Matches(1920, 1080));
        Assert.True(f.Matches(1, 1));
    }

    [Fact(DisplayName = "Exact resolution: matches only when equal")]
    public void ExactMatch()
    {
        var f = new MonitorFilter { Width = 7680, Height = 2160 };
        Assert.True(f.Matches(7680, 2160));
        Assert.False(f.Matches(7680, 2161));
        Assert.False(f.Matches(3840, 2160));
        Assert.False(f.Matches(1920, 1080));
    }

    [Fact(DisplayName = "Width constraint only: any height")]
    public void WidthOnly()
    {
        var f = new MonitorFilter { Width = 7680, Height = 0 };
        Assert.True(f.Matches(7680, 2160));
        Assert.True(f.Matches(7680, 1080));
        Assert.False(f.Matches(3840, 2160));
    }

    [Fact(DisplayName = "Height constraint only: any width")]
    public void HeightOnly()
    {
        var f = new MonitorFilter { Width = 0, Height = 1080 };
        Assert.True(f.Matches(1920, 1080));
        Assert.True(f.Matches(2560, 1080));
        Assert.False(f.Matches(1920, 2160));
    }
}
