using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// WindowScanner.IsBlacklistedProcess 纯逻辑测试:系统外壳进程名黑名单。
/// cloaked / 尺寸过滤需 Win32,不在单测覆盖范围(那部分埋在 EnumerateCandidates 里)。
/// </summary>
public class WindowScannerTests
{
    [Theory(DisplayName = "黑名单进程(不含 .exe)→ 命中")]
    [InlineData("reframe")]
    [InlineData("textinputhost")]
    [InlineData("shellexperiencehost")]
    [InlineData("searchhost")]
    [InlineData("startmenuexperiencehost")]
    [InlineData("lockapp")]
    [InlineData("widgets")]
    [InlineData("systemsettings")]
    [InlineData("applicationframehost")]
    [InlineData("explorer")]
    public void Blacklisted_NoExe_Matches(string name)
        => Assert.True(WindowScanner.IsBlacklistedProcess(name));

    [Theory(DisplayName = "黑名单进程带 .exe / 大小写混合 → 仍命中")]
    [InlineData("Reframe.exe")]
    [InlineData("TextInputHost.EXE")]
    [InlineData("  SearchHost  ")]
    public void Blacklisted_WithExeOrCase_Matches(string name)
        => Assert.True(WindowScanner.IsBlacklistedProcess(name));

    [Theory(DisplayName = "普通应用/游戏进程 → 不命中")]
    [InlineData("starrail")]
    [InlineData("yuanshen")]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("ZenlessZoneZero.exe")]
    public void NormalApp_NoMatch(string name)
        => Assert.False(WindowScanner.IsBlacklistedProcess(name));

    [Theory(DisplayName = "空 / 空白 / null → 不命中(不抛)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrNull_NoMatch(string? name)
        => Assert.False(WindowScanner.IsBlacklistedProcess(name));
}
