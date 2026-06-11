using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// WindowScanner.IsBlacklistedProcess pure-logic tests: the system-shell process-name blacklist.
/// cloaked / size filtering needs Win32 and is out of unit-test scope (that part lives in EnumerateCandidates).
/// </summary>
public class WindowScannerTests
{
    [Theory(DisplayName = "Blacklisted process (without .exe) → match")]
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

    [Theory(DisplayName = "Blacklisted process with .exe / mixed case → still matches")]
    [InlineData("Reframe.exe")]
    [InlineData("TextInputHost.EXE")]
    [InlineData("  SearchHost  ")]
    public void Blacklisted_WithExeOrCase_Matches(string name)
        => Assert.True(WindowScanner.IsBlacklistedProcess(name));

    [Theory(DisplayName = "Normal app/game process → no match")]
    [InlineData("starrail")]
    [InlineData("yuanshen")]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("ZenlessZoneZero.exe")]
    public void NormalApp_NoMatch(string name)
        => Assert.False(WindowScanner.IsBlacklistedProcess(name));

    [Theory(DisplayName = "Empty / whitespace / null → no match (no throw)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrNull_NoMatch(string? name)
        => Assert.False(WindowScanner.IsBlacklistedProcess(name));

    // ======================== User-ignore list IsUserIgnored ========================

    private static readonly string[] SampleIgnores = { "notepad", "chrome", "discord" };

    [Theory(DisplayName = "User-ignore list matches (process name / with .exe / case / whitespace all tolerated)")]
    [InlineData("notepad")]
    [InlineData("Notepad")]
    [InlineData("NOTEPAD.exe")]
    [InlineData("chrome.EXE")]
    [InlineData("  discord  ")]
    public void UserIgnored_Matches(string name)
        => Assert.True(WindowScanner.IsUserIgnored(name, SampleIgnores));

    [Theory(DisplayName = "User-ignore list doesn't match (process not in the list)")]
    [InlineData("starrail")]
    [InlineData("yuanshen")]
    [InlineData("ZenlessZoneZero.exe")]
    public void UserIgnored_NoMatch(string name)
        => Assert.False(WindowScanner.IsUserIgnored(name, SampleIgnores));

    [Fact(DisplayName = "List entry itself has .exe / case → still matches (the list side is StripExe'd too)")]
    public void UserIgnored_ListEntryWithExe_Matches()
        => Assert.True(WindowScanner.IsUserIgnored("yuanshen", new[] { "YuanShen.exe" }));

    [Theory(DisplayName = "Empty name / null name / empty list / null list → no match (no throw)")]
    [InlineData("", new[] { "notepad" })]
    [InlineData(null, new[] { "notepad" })]
    public void UserIgnored_EmptyName_NoMatch(string? name, string[] ignores)
        => Assert.False(WindowScanner.IsUserIgnored(name, ignores));

    [Fact(DisplayName = "Empty / null ignore list → no name matches")]
    public void UserIgnored_EmptyOrNullList_NoMatch()
    {
        Assert.False(WindowScanner.IsUserIgnored("notepad", System.Array.Empty<string>()));
        Assert.False(WindowScanner.IsUserIgnored("notepad", null));
    }

    [Fact(DisplayName = "Ignore list with empty/whitespace entries → skipped, doesn't falsely match an empty-named process")]
    public void UserIgnored_BlankListEntries_Skipped()
        => Assert.False(WindowScanner.IsUserIgnored("notepad", new[] { "", "   " }));

    // ======================== Pure filter verdict Classify ========================

    [Fact(DisplayName = "Classify: normal process + big enough + not hidden + not in list → None")]
    public void Classify_NormalCandidate_None()
        => Assert.Equal(FilterReason.None,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify: system-shell blacklist → SystemShell")]
    public void Classify_SystemShell()
        => Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("textinputhost", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify: user-ignore list matches → UserIgnored")]
    public void Classify_UserIgnored()
        => Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 1920, 1080, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify: cloaked → Cloaked")]
    public void Classify_Cloaked()
        => Assert.Equal(FilterReason.Cloaked,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: true, SampleIgnores));

    [Theory(DisplayName = "Classify: either side too small → TooSmall")]
    [InlineData(40, 1080)]
    [InlineData(1920, 40)]
    [InlineData(10, 10)]
    public void Classify_TooSmall(int w, int h)
        => Assert.Equal(FilterReason.TooSmall,
            WindowScanner.Classify("starrail", w, h, isCloaked: false, SampleIgnores));

    [Fact(DisplayName = "Classify: system blacklist and user-ignore overlap → blacklist wins (SystemShell)")]
    public void Classify_BlacklistOverlapsUserIgnore_BlacklistWins()
    {
        // explorer is both in the system blacklist and explicitly added to user-ignore — should be judged system-shell (irreversible), not UserIgnored.
        var ignores = new[] { "explorer", "notepad" };
        Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("explorer", 1920, 1080, isCloaked: false, ignores));
    }

    [Fact(DisplayName = "Classify: user-ignore beats cloaked/too-small (the reversible reason is judged first, UI shows \"Ignored\")")]
    public void Classify_UserIgnore_BeatsCloakedAndSmall()
    {
        Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 1920, 1080, isCloaked: true, SampleIgnores));
        Assert.Equal(FilterReason.UserIgnored,
            WindowScanner.Classify("notepad", 10, 10, isCloaked: false, SampleIgnores));
    }

    [Fact(DisplayName = "Classify: with no user list (null) falls back to the system-blacklist/size/cloaked trio")]
    public void Classify_NullIgnores_StillWorks()
    {
        Assert.Equal(FilterReason.None,
            WindowScanner.Classify("starrail", 1920, 1080, isCloaked: false, null));
        Assert.Equal(FilterReason.SystemShell,
            WindowScanner.Classify("reframe", 1920, 1080, isCloaked: false, null));
    }
}
