using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// MatchEngine.Matches 纯逻辑测试:进程名 / 标题包含 / 正则 / 启用与空值守卫。
/// 不调任何 Win32(WindowInfo 只是数据快照,Handle 不参与匹配)。
/// </summary>
public class MatchEngineTests
{
    private static WindowInfo Win(string title = "", string proc = "")
        => new() { Title = title, ProcessName = proc };

    private static Profile Prof(MatchKind kind, string value, bool enabled = true)
        => new() { MatchKind = kind, MatchValue = value, Enabled = enabled };

    // ---- 进程名匹配 ----

    [Fact(DisplayName = "进程名:配置带 .exe,窗口进程名不带 .exe → 命中")]
    public void Process_ConfigHasExe_WindowWithout_Matches()
    {
        // WindowScanner 约定 ProcessName 不含 .exe、小写
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "进程名:配置不带 .exe 也命中")]
    public void Process_ConfigWithoutExe_Matches()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "进程名:大小写不敏感")]
    public void Process_CaseInsensitive_Matches()
    {
        var w = Win(proc: "yuanshen");
        var p = Prof(MatchKind.Process, "YUANSHEN.EXE");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "进程名:不同进程 → 不命中")]
    public void Process_Different_NoMatch()
    {
        var w = Win(proc: "notepad");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "进程名:仅 .exe 后缀差异不影响,但子串不算命中")]
    public void Process_SubstringNotEqual_NoMatch()
    {
        // 进程名是精确相等(StripExe 后 Equals),不是 Contains
        var w = Win(proc: "starrail2");
        var p = Prof(MatchKind.Process, "StarRail.exe");
        Assert.False(MatchEngine.Matches(w, p));
    }

    // ---- 标题包含 ----

    [Fact(DisplayName = "标题包含:子串命中,大小写不敏感")]
    public void Title_Contains_CaseInsensitive()
    {
        var w = Win(title: "崩坏：星穹铁道");
        var p = Prof(MatchKind.Title, "星穹");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "标题包含:英文大小写不敏感命中")]
    public void Title_Contains_AsciiCaseInsensitive()
    {
        var w = Win(title: "Genshin Impact");
        var p = Prof(MatchKind.Title, "genshin");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "标题包含:不含子串 → 不命中")]
    public void Title_NotContains_NoMatch()
    {
        var w = Win(title: "Genshin Impact");
        var p = Prof(MatchKind.Title, "StarRail");
        Assert.False(MatchEngine.Matches(w, p));
    }

    // ---- 正则 ----

    [Fact(DisplayName = "正则:合法表达式命中(忽略大小写)")]
    public void Regex_Valid_Matches()
    {
        var w = Win(title: "原神 3.5");
        var p = Prof(MatchKind.TitleRegex, @"原神\s+\d+\.\d+");
        Assert.True(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "正则:合法表达式不命中")]
    public void Regex_Valid_NoMatch()
    {
        var w = Win(title: "Notepad");
        var p = Prof(MatchKind.TitleRegex, @"^Genshin$");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "正则:非法表达式不抛异常,返回 false")]
    public void Regex_Invalid_DoesNotThrow_ReturnsFalse()
    {
        var w = Win(title: "anything");
        var p = Prof(MatchKind.TitleRegex, "([unclosed");
        // 不应抛异常
        var result = Record.Exception(() => MatchEngine.Matches(w, p));
        Assert.Null(result);
        Assert.False(MatchEngine.Matches(w, p));
    }

    // ---- Enabled / 空值守卫 ----

    [Fact(DisplayName = "Enabled=false:即使匹配也不命中")]
    public void Disabled_NeverMatches()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "StarRail.exe", enabled: false);
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "MatchValue 为空字符串 → 不命中")]
    public void EmptyMatchValue_NoMatch()
    {
        var w = Win(proc: "starrail");
        var p = Prof(MatchKind.Process, "");
        Assert.False(MatchEngine.Matches(w, p));
    }

    [Fact(DisplayName = "MatchValue 为空白 → 不命中")]
    public void WhitespaceMatchValue_NoMatch()
    {
        var w = Win(title: "anything");
        var p = Prof(MatchKind.Title, "   ");
        Assert.False(MatchEngine.Matches(w, p));
    }
}
