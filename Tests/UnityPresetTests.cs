using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// UnityPreset's value-name prefix-matching pure function (the hash suffix varies per game, so prefix
/// matching is key). Registry I/O isn't required to be unit-tested; here we only test the boundaries of Matches.
/// </summary>
public class UnityPresetTests
{
    [Fact(DisplayName = "A real Width value name with a hash suffix matches the width prefix")]
    public void Matches_WidthWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Width_h182942802", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "Height with a hash suffix matches the height prefix")]
    public void Matches_HeightWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Height_h2627697771", UnityPreset.HeightPrefix));
    }

    [Fact(DisplayName = "Is Fullscreen mode with a hash suffix matches the fullscreen prefix")]
    public void Matches_FullscreenWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Is Fullscreen mode_h3981298716", UnityPreset.FullscreenPrefix));
    }

    [Fact(DisplayName = "Width prefix doesn't accidentally match a Height value name (the prefix distinguishes width/height)")]
    public void Matches_WidthPrefix_DoesNotMatchHeight()
    {
        Assert.False(UnityPreset.Matches("Screenmanager Resolution Height_h123", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "Height prefix doesn't accidentally match a Width value name")]
    public void Matches_HeightPrefix_DoesNotMatchWidth()
    {
        Assert.False(UnityPreset.Matches("Screenmanager Resolution Width_h123", UnityPreset.HeightPrefix));
    }

    [Fact(DisplayName = "An exact value name with no suffix also matches (prefix == full name)")]
    public void Matches_ExactNameNoSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Width", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "Unrelated value names (e.g. UnityGraphicsQuality) all fail to match")]
    public void Matches_UnrelatedName_NoMatch()
    {
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.WidthPrefix));
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.HeightPrefix));
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.FullscreenPrefix));
    }

    [Fact(DisplayName = "Case-sensitive: lowercase width doesn't match (Unity value names use a fixed uppercase W)")]
    public void Matches_CaseSensitive()
    {
        Assert.False(UnityPreset.Matches("screenmanager resolution width_h1", UnityPreset.WidthPrefix));
    }
}
