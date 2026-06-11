using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// StartupOptions.IsMinimized: recognises the "start minimized to tray" switch in the process
/// command line (the start-on-login scheduled task passes it; a manual launch does not).
/// </summary>
public class StartupOptionsTests
{
    [Fact(DisplayName = "--minimized present -> true")]
    public void DashDashMinimized_True()
    {
        Assert.True(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe", "--minimized" }));
    }

    [Fact(DisplayName = "/minimized present -> true")]
    public void SlashMinimized_True()
    {
        Assert.True(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe", "/minimized" }));
    }

    [Theory(DisplayName = "Case-insensitive match")]
    [InlineData("--MINIMIZED")]
    [InlineData("--Minimized")]
    [InlineData("/Minimized")]
    [InlineData("/MINIMIZED")]
    public void CaseInsensitive_True(string flag)
    {
        Assert.True(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe", flag }));
    }

    [Fact(DisplayName = "Exe path only (manual double-click) -> false")]
    public void ExeOnly_False()
    {
        Assert.False(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe" }));
    }

    [Fact(DisplayName = "Unrelated args -> false")]
    public void UnrelatedArgs_False()
    {
        Assert.False(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe", "--something", "/else", "minimized" }));
    }

    [Fact(DisplayName = "Flag is found regardless of position")]
    public void AnyPosition_True()
    {
        Assert.True(StartupOptions.IsMinimized(new[] { @"C:\app\Reframe.exe", "--foo", "--minimized", "--bar" }));
    }

    [Fact(DisplayName = "Empty / null inputs -> false")]
    public void EmptyOrNull_False()
    {
        Assert.False(StartupOptions.IsMinimized(System.Array.Empty<string>()));
        Assert.False(StartupOptions.IsMinimized(null));
        Assert.False(StartupOptions.IsMinimized(new string?[] { null, "", "  " }!));
    }

    [Fact(DisplayName = "Substring / bare word does not match")]
    public void NoSubstringMatch_False()
    {
        // Guard against accidentally matching on Contains: only the exact token counts.
        Assert.False(StartupOptions.IsMinimized(new[] { "--minimized-extra" }));
        Assert.False(StartupOptions.IsMinimized(new[] { "minimized" }));
    }
}
