using System.Diagnostics;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// MatchEngine.Matches pure-logic tests: process name / title contains / regex / enabled and null guards.
/// No Win32 calls (WindowInfo is just a data snapshot; Handle plays no part in matching).
/// </summary>
public class MatchEngineTests
{
    private static WindowInfo Win(string title = "", string proc = "")
        => new() { Title = title, ProcessName = proc };

    private static Profile Prof(MatchKind kind, string value, bool enabled = true)
        => new() { MatchKind = kind, MatchValue = value, Enabled = enabled };

    // ---- Process-name matching ----

    [Fact(DisplayName = "Process name: config has .exe, window process name doesn't → match")]
    public void Process_ConfigHasExe_WindowWithout_Matches()
    {
        // WindowScanner's convention: ProcessName excludes .exe and is lowercase
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Process name: config without .exe also matches")]
    public void Process_ConfigWithoutExe_Matches()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Process name: case-insensitive")]
    public void Process_CaseInsensitive_Matches()
    {
        var w = Win(proc: "yuanshen");
        var p = Prof(MatchKind.Process, "YUANSHEN.EXE");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Process name: different process → no match")]
    public void Process_Different_NoMatch()
    {
        var w = Win(proc: "notepad");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Process name: only the .exe suffix differs is fine, but a substring doesn't count as a match")]
    public void Process_SubstringNotEqual_NoMatch()
    {
        // Process name is exact equality (Equals after StripExe), not Contains
        var w = Win(proc: "starrail2");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.False(MatchEngine.Matches(w, p));
    }

    // ---- Title contains ----

    [Fact(DisplayName = "Title contains: substring matches, case-insensitive")]
    public void Title_Contains_CaseInsensitive()
    {
        var w = Win(title: "崩坏：星穹铁道");
        var p = Prof(MatchKind.Title, "星穹");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Title contains: ASCII case-insensitive match")]
    public void Title_Contains_AsciiCaseInsensitive()
    {
        var w = Win(title: "Genshin Impact");
        var p = Prof(MatchKind.Title, "genshin");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Title contains: substring absent → no match")]
    public void Title_NotContains_NoMatch()
    {
        var w = Win(title: "Genshin Impact");
        var p = Prof(MatchKind.Title, "StarRail");
        Assert.False(MatchEngine.Matches(w, p));
    }

    // ---- Regex ----

    [Fact(DisplayName = "Regex: valid expression matches (case-insensitive)")]
    public void Regex_Valid_Matches()
    {
        var w = Win(title: "原神 3.5");
        var p = Prof(MatchKind.TitleRegex, @"原神\s+\d+\.\d+");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Regex: valid expression doesn't match")]
    public void Regex_Valid_NoMatch()
    {
        var w = Win(title: "Notepad");
        var p = Prof(MatchKind.TitleRegex, @"^Genshin$");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Regex: invalid expression doesn't throw, returns false")]
    public void Regex_Invalid_DoesNotThrow_ReturnsFalse()
    {
        var w = Win(title: "anything");
        var p = Prof(MatchKind.TitleRegex, "([unclosed");
        // Should not throw
        var result = Record.Exception(() => MatchEngine.Matches(w, p));
        Assert.Null(result);
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "Regex: catastrophic backtracking is caught by the 100ms timeout, returns false quickly without hanging")]
    public void Regex_CatastrophicBacktracking_TimesOut_ReturnsFalseFast()
    {
        // (a+)+$ against "a long run of a's + a non-a tail" is classic catastrophic backtracking: without a
        // timeout it would blow up exponentially and hang the scan tick. MatchEngine sets a 100ms matchTimeout
        // on user regexes and treats a timeout as no match.
        var input = new string('a', 40) + "!"; // 40 a's are enough to run forever without a timeout; the '!' tail makes $ fail and triggers backtracking
        var w = Win(title: input);
        var p = Prof(MatchKind.TitleRegex, "(a+)+$");

        var sw = Stopwatch.StartNew();
        bool matched = MatchEngine.Matches(w, p);
        sw.Stop();

        Assert.False(matched); // Timeout treated as no match
        // Shouldn't hang: 100ms timeout + first-compile overhead, with ample margin assert < 2s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Regex matching should be caught by the timeout and return quickly; actual {sw.ElapsedMilliseconds}ms");
    }

    // ---- Enabled / null guards ----

    [Fact(DisplayName = "Enabled=false: never matches even if it would otherwise")]
    public void Disabled_NeverMatches()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail.exe", enabled: false);
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "MatchValue is empty string → no match")]
    public void EmptyMatchValue_NoMatch()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "MatchValue is whitespace → no match")]
    public void WhitespaceMatchValue_NoMatch()
    {
        var w = Win(title: "anything");
        var p = Prof(MatchKind.Title, "   ");
        Assert.False(MatchEngine.Matches(w, p));
    }
}
