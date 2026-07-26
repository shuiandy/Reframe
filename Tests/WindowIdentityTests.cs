using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Identity extraction: the normalization that lets a window captured in one session be recognized in the
/// next. Pure string work — no Win32 involved.
/// </summary>
public class WindowIdentityTests
{
    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome", "chrome")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("Chrome.EXE", "chrome")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData("/usr/bin/Weird.Exe", "weird")]
    [InlineData("  notepad.exe  ", "notepad")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Process_name_normalizes_path_extension_and_case(string? raw, string expected)
        => Assert.Equal(expected, WindowIdentity.NormalizeProcess(raw));

    [Theory]
    [InlineData("Chrome_WidgetWin_1", "chrome_widgetwin_1")]
    [InlineData("  Notepad  ", "notepad")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Class_name_normalizes_case_and_whitespace(string? raw, string expected)
        => Assert.Equal(expected, WindowIdentity.NormalizeClass(raw));

    [Fact]
    public void Identities_are_value_equal_after_normalization()
    {
        var a = WindowIdentity.Create(@"C:\x\Chrome.exe", "Chrome_WidgetWin_1");
        var b = WindowIdentity.Create("chrome", "chrome_widgetwin_1");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Title_is_not_part_of_the_identity()
    {
        // Two Chrome windows showing different tabs are the same *identity*; only the ordinal separates them.
        // (The caption is recorded on the record for diagnostics, never on the identity — a browser caption
        // changes on every tab switch and would make matching drift.)
        var a = WindowIdentity.Create("chrome", "chrome_widgetwin_1");
        var b = WindowIdentity.Create("chrome", "chrome_widgetwin_1");
        Assert.Equal(a, b);

        // The type simply has nowhere to put a caption: its only components are process + class.
        Assert.Equal("chrome!chrome_widgetwin_1", a.ToString());
    }

    [Fact]
    public void Different_class_or_process_yields_a_different_identity()
    {
        var browser = WindowIdentity.Create("chrome", "chrome_widgetwin_1");
        Assert.NotEqual(browser, WindowIdentity.Create("chrome", "chrome_widgetwin_2"));
        Assert.NotEqual(browser, WindowIdentity.Create("msedge", "chrome_widgetwin_1"));
    }

    [Fact]
    public void Unknown_identity_is_empty_and_not_matchable()
    {
        Assert.True(WindowIdentity.Unknown.IsUnknown);
        Assert.False(WindowIdentity.Unknown.IsMatchable);
        Assert.Equal(WindowIdentity.Unknown, WindowIdentity.Create(null, null));
    }

    [Fact]
    public void A_half_known_identity_is_not_matchable()
    {
        // A failed pid→name lookup leaves the process empty: better to skip the claim this round than to let
        // two unrelated apps that share a generic class name adopt each other's geometry.
        Assert.False(WindowIdentity.Create("", "notepad").IsMatchable);
        Assert.False(WindowIdentity.Create("notepad", "").IsMatchable);
        Assert.True(WindowIdentity.Create("notepad", "notepad").IsMatchable);
    }
}
