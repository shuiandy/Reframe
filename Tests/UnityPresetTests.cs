using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// UnityPreset 的值名前缀匹配纯函数(hash 后缀因游戏而异,前缀匹配是关键)。
/// 注册表 I/O 不强求单测;这里只测 Matches 的边界。
/// </summary>
public class UnityPresetTests
{
    [Fact(DisplayName = "Width 带 hash 后缀的真实值名命中宽度前缀")]
    public void Matches_WidthWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Width_h182942802", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "Height 带 hash 后缀命中高度前缀")]
    public void Matches_HeightWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Height_h2627697771", UnityPreset.HeightPrefix));
    }

    [Fact(DisplayName = "Is Fullscreen mode 带 hash 后缀命中全屏前缀")]
    public void Matches_FullscreenWithHashSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Is Fullscreen mode_h3981298716", UnityPreset.FullscreenPrefix));
    }

    [Fact(DisplayName = "Width 前缀不会误命中 Height 值名(前缀区分宽/高)")]
    public void Matches_WidthPrefix_DoesNotMatchHeight()
    {
        Assert.False(UnityPreset.Matches("Screenmanager Resolution Height_h123", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "Height 前缀不会误命中 Width 值名")]
    public void Matches_HeightPrefix_DoesNotMatchWidth()
    {
        Assert.False(UnityPreset.Matches("Screenmanager Resolution Width_h123", UnityPreset.HeightPrefix));
    }

    [Fact(DisplayName = "无后缀的精确值名也命中(前缀==全名)")]
    public void Matches_ExactNameNoSuffix()
    {
        Assert.True(UnityPreset.Matches("Screenmanager Resolution Width", UnityPreset.WidthPrefix));
    }

    [Fact(DisplayName = "无关值名(如 UnityGraphicsQuality)全部不命中")]
    public void Matches_UnrelatedName_NoMatch()
    {
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.WidthPrefix));
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.HeightPrefix));
        Assert.False(UnityPreset.Matches("UnityGraphicsQuality_h1234", UnityPreset.FullscreenPrefix));
    }

    [Fact(DisplayName = "大小写敏感:小写 width 不命中(Unity 值名固定大写 W)")]
    public void Matches_CaseSensitive()
    {
        Assert.False(UnityPreset.Matches("screenmanager resolution width_h1", UnityPreset.WidthPrefix));
    }
}
