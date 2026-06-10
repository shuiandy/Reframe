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

    // ======================== 用户忽略名单 IsUserIgnored ========================

    private static readonly string[] SampleIgnores = { "notepad", "chrome", "discord" };

    [Theory(DisplayName = "用户忽略名单命中(进程名/带.exe/大小写/空白都容忍)")]
    [InlineData("notepad")]
    [InlineData("Notepad")]
    [InlineData("NOTEPAD.exe")]
    [InlineData("chrome.EXE")]
    [InlineData("  discord  ")]
    public void UserIgnored_Matches(string name)
        => Assert.True(WindowScanner.IsUserIgnored(name, SampleIgnores));

    [Theory(DisplayName = "用户忽略名单不命中(不在单里的进程)")]
    [InlineData("starrail")]
    [InlineData("yuanshen")]
    [InlineData("ZenlessZoneZero.exe")]
    public void UserIgnored_NoMatch(string name)
        => Assert.False(WindowScanner.IsUserIgnored(name, SampleIgnores));

    [Fact(DisplayName = "忽略名单本身带 .exe / 大小写 → 仍能命中(名单端也 StripExe)")]
    public void UserIgnored_ListEntryWithExe_Matches()
        => Assert.True(WindowScanner.IsUserIgnored("yuanshen", new[] { "YuanShen.exe" }));

    [Theory(DisplayName = "空名 / null名 / 空单 / null单 → 不命中(不抛)")]
    [InlineData("", new[] { "notepad" })]
    [InlineData(null, new[] { "notepad" })]
    public void UserIgnored_EmptyName_NoMatch(string? name, string[] ignores)
        => Assert.False(WindowScanner.IsUserIgnored(name, ignores));

    [Fact(DisplayName = "忽略名单为空 / null → 任何名都不命中")]
    public void UserIgnored_EmptyOrNullList_NoMatch()
    {
        Assert.False(WindowScanner.IsUserIgnored("notepad", System.Array.Empty<string>()));
        Assert.False(WindowScanner.IsUserIgnored("notepad", null));
    }

    [Fact(DisplayName = "忽略名单含空串/空白项 → 跳过,不误命中空名进程")]
    public void UserIgnored_BlankListEntries_Skipped()
        => Assert.False(WindowScanner.IsUserIgnored("notepad", new[] { "", "   " }));

    // ======================== 纯过滤判定 Classify ========================

    [Fact(DisplayName = "Classify:正常进程 + 足够大 + 未隐藏 + 不在名单 → None")]
    public void Classify_NormalCandidate_None()
        => Assert.Equal(FilterReason.None,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify:系统外壳黑名单 → SystemShell")]
    public void Classify_SystemShell()
        => Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("textinputhost", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify:用户忽略名单命中 → UserIgnored")]
    public void Classify_UserIgnored()
        => Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify:cloaked → Cloaked")]
    public void Classify_Cloaked()
        => Assert.Equal(FilterReason.Cloaked,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: true, SampleIgnores));

    [Theory(DisplayName = "Classify:任一边过小 → TooSmall")]
    [InlineData(40, 1080)]
    [InlineData(1920, 40)]
    [InlineData(10, 10)]
    public void Classify_TooSmall(int w, int h)
        => Assert.Equal(FilterReason.TooSmall,
            WindowScanner.Classify("starrail", w, h, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify:系统黑名单与用户忽略叠加 → 黑名单优先(SystemShell)")]
    public void Classify_BlacklistOverlapsUserIgnore_BlacklistWins()
    {
        // explorer 既在系统黑名单,又被显式加进用户忽略 —— 应判系统外壳(不可逆),不是 UserIgnored。
        var ignores = new[] { "explorer", "notepad" };
        Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("explorer", 1920, 1080, isCloaked: false, ignores));
    }

    [Fact(DisplayName = "Classify:用户忽略优先于 cloaked/过小(先判可逆原因,UI 显「已忽略」)")]
    public void Classify_UserIgnore_BeatsCloakedAndSmall()
    {
        Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 1920, 1080, isCloaked: true, SampleIgnores));
        Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 10, 10, isCloaked: false, SampleIgnores));
    }

    [Fact(DisplayName = "Classify:无用户名单(null)时退化为系统黑名单/尺寸/cloaked 三判")]
    public void Classify_NullIgnores_StillWorks()
    {
        Assert.Equal(FilterReason.None,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: false, null));
        Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("reframe", 1920, 1080, isCloaked: false, null));
    }
}
