using System.Text;
using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// UpdateScript: the generated PowerShell that swaps the files after Reframe exits, and the path
/// comparison that decides which flavour of update to do.
///
/// <para>Three properties are load-bearing and asserted for both modes:</para>
/// <list type="number">
/// <item><b>Pure ASCII.</b> Windows PowerShell decodes a BOM-less <c>-File</c> script as the ANSI code
/// page, so a non-ASCII byte (a CJK user name in <c>%TEMP%</c>, say) would be mis-decoded into a
/// corrupted path. Paths are embedded as base64 UTF-8 precisely so this holds for any input.</item>
/// <item><b>No destructive command.</b> The script must only ever create or overwrite. A failed update
/// must not cost the user a single file.</item>
/// <item><b>Paths round-trip exactly.</b> Including paths with spaces, quotes, <c>$</c>, brackets and
/// non-ASCII characters — base64 is also what makes the quoting airtight.</item>
/// </list>
/// </summary>
public class UpdateScriptTests
{
    private const int Pid = 4242;
    private const string Setup = @"C:\Users\andy\AppData\Local\Temp\Reframe-update\Reframe-Setup-v1.3.1-win-x64.exe";
    private const string Extracted = @"C:\Users\andy\AppData\Local\Temp\Reframe-update\extracted";
    private const string Target = @"C:\Program Files\Reframe";
    private const string AppExe = @"C:\Program Files\Reframe\Reframe.exe";
    private const string Log = @"C:\Users\andy\AppData\Local\Temp\Reframe-update\update.log";

    private static string Installer() => UpdateScript.BuildInstallerScript(Pid, Setup, AppExe, Log);
    private static string Portable() => UpdateScript.BuildPortableScript(Pid, Extracted, Target, AppExe, Log);

    public static TheoryData<string> BothModes() => new() { Installer(), Portable() };

    /// <summary>Pull every base64 literal back out of a generated script and decode it.</summary>
    private static List<string> DecodedPaths(string script)
    {
        var result = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(script, @"Dec\('([A-Za-z0-9+/=]*)'\)"))
        {
            result.Add(Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)));
        }
        return result;
    }

    // ------------------------------------------------------------------ ASCII

    [Theory(DisplayName = "Generated script is pure ASCII (both modes)")]
    [MemberData(nameof(BothModes))]
    public void ScriptIsAscii(string script)
    {
        foreach (char c in script)
            Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the generated script");
    }

    [Fact(DisplayName = "Still pure ASCII when the paths themselves are not")]
    public void ScriptIsAscii_EvenWithNonAsciiPaths()
    {
        string script = UpdateScript.BuildPortableScript(
            Pid,
            @"C:\Users\用户\AppData\Local\Temp\Reframe-update\extracted",
            @"D:\程序\Reframe (便携版)",
            @"D:\程序\Reframe (便携版)\Reframe.exe",
            @"C:\Users\用户\AppData\Local\Temp\Reframe-update\update.log");

        foreach (char c in script)
            Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} leaked into the generated script");

        var paths = DecodedPaths(script);
        Assert.Contains(@"D:\程序\Reframe (便携版)", paths);
        Assert.Contains(@"D:\程序\Reframe (便携版)\Reframe.exe", paths);
    }

    // ------------------------------------------------------------------ no deletion

    [Theory(DisplayName = "No destructive command anywhere in the script (both modes)")]
    [MemberData(nameof(BothModes))]
    public void NoDestructiveCommands(string script)
    {
        foreach (string forbidden in new[]
                 {
                     "Remove-Item", "Remove-ItemProperty", "Clear-Content", "Clear-Item",
                     "rmdir", "rd /", "del ", "erase", "Delete", "Format-Volume", "Set-Content",
                 })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "Portable mode copies file by file and never mirrors/prunes the target")]
    public void Portable_CopiesOnly()
    {
        string script = Portable();
        Assert.Contains("Copy-Item -LiteralPath $f.FullName -Destination $target -Force", script);
        // A "make the target identical to the source" idiom would be able to remove user files.
        Assert.DoesNotContain("robocopy", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/MIR", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/PURGE", script, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ paths and escaping

    [Fact(DisplayName = "Installer mode round-trips setup / exe / log paths exactly")]
    public void Installer_PathsRoundTrip()
    {
        var paths = DecodedPaths(Installer());
        Assert.Contains(Setup, paths);
        Assert.Contains(AppExe, paths);
        Assert.Contains(Log, paths);
    }

    [Fact(DisplayName = "Portable mode round-trips source / target / exe / log paths exactly")]
    public void Portable_PathsRoundTrip()
    {
        var paths = DecodedPaths(Portable());
        Assert.Contains(Extracted, paths);
        Assert.Contains(Target, paths);
        Assert.Contains(AppExe, paths);
        Assert.Contains(Log, paths);
    }

    [Fact(DisplayName = "Paths with quotes, $ and brackets cannot break out of their literal")]
    public void HostilePaths_CannotEscapeTheLiteral()
    {
        string nasty = @"C:\temp\it's $(Get-Process) [x] `bad`\dir";
        string script = UpdateScript.BuildPortableScript(Pid, nasty, Target, AppExe, Log);

        // The dangerous text appears nowhere verbatim - it only exists inside a base64 blob.
        Assert.DoesNotContain("$(Get-Process)", script);
        Assert.DoesNotContain("it's", script);
        Assert.Contains(nasty, DecodedPaths(script));
    }

    // ------------------------------------------------------------------ mode differences

    [Fact(DisplayName = "Installer mode runs Setup.exe silently and checks its exit code")]
    public void Installer_RunsSetupSilently()
    {
        string script = Installer();
        Assert.Contains("Mode: installer", script);
        Assert.Contains("-ArgumentList '/SILENT','/NORESTART'", script);
        Assert.Contains("-Wait -PassThru", script);
        Assert.Contains("$code = $proc.ExitCode", script);
        // A non-zero (non-reboot) exit must abort rather than pretend success.
        Assert.Contains("Fail ('setup returned exit code '", script);
        // Installer mode never touches files itself; that is Setup.exe's job.
        Assert.DoesNotContain("Copy-Item", script);
    }

    [Fact(DisplayName = "Portable mode copies the payload over the install folder and runs no installer")]
    public void Portable_CopiesPayload()
    {
        string script = Portable();
        Assert.Contains("Mode: portable", script);
        Assert.Contains("Get-ChildItem -LiteralPath $Src -Recurse -Force -File", script);
        Assert.Contains("New-Item -ItemType Directory -Path $targetDir -Force", script);
        Assert.DoesNotContain("/SILENT", script);
        Assert.DoesNotContain("Start-Process -FilePath $Setup", script);
    }

    [Theory(DisplayName = "Both modes wait for the old PID, log, and relaunch the app")]
    [MemberData(nameof(BothModes))]
    public void SharedSkeleton(string script)
    {
        Assert.Contains("$TargetPid = 4242", script);
        Assert.Contains("Wait-Process -Id $TargetPid", script);
        // If the old process is still alive we abort without touching anything.
        Assert.Contains("Fail 'Reframe did not exit in time; nothing was changed'", script);
        Assert.Contains("Start-Process -FilePath $AppExe", script);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        // Failures are greppable: this is what Reframe scans update.log for on the next start.
        Assert.Contains("Log ('" + UpdateScript.ErrorMarker + "' + $m); exit 1", script);
    }

    // ------------------------------------------------------------------ install-path comparison

    [Theory(DisplayName = "IsSameDirectory: trailing separator, case and separator style do not matter")]
    [InlineData(@"C:\Program Files\Reframe", @"C:\Program Files\Reframe\", true)]
    [InlineData(@"C:\Program Files\Reframe\", @"C:\Program Files\Reframe", true)]
    [InlineData(@"c:\program files\reframe\", @"C:\Program Files\Reframe\", true)]
    [InlineData(@"C:/Program Files/Reframe/", @"C:\Program Files\Reframe", true)]
    [InlineData(@"  C:\Program Files\Reframe\  ", @"C:\Program Files\Reframe", true)]
    [InlineData("\"C:\\Program Files\\Reframe\\\"", @"C:\Program Files\Reframe", true)]
    [InlineData(@"C:\Program Files\Reframe\.", @"C:\Program Files\Reframe", true)]
    [InlineData(@"C:\Program Files\Reframe\sub\..", @"C:\Program Files\Reframe", true)]
    [InlineData(@"C:\", @"C:", true)]
    public void IsSameDirectory_Equivalent(string a, string b, bool expected)
        => Assert.Equal(expected, UpdateScript.IsSameDirectory(a, b));

    [Theory(DisplayName = "IsSameDirectory: different folders (and unusable input) are not the same")]
    [InlineData(@"C:\Program Files\Reframe", @"C:\Program Files\Reframe2")]
    [InlineData(@"C:\Program Files\Reframe", @"C:\Program Files\Reframe\bin")]
    [InlineData(@"C:\Program Files\Reframe", @"D:\Program Files\Reframe")]
    [InlineData(@"C:\Program Files\Reframe", @"C:\Users\andy\Downloads\Reframe")]
    [InlineData(@"C:\Program Files\Reframe", null)]
    [InlineData(null, @"C:\Program Files\Reframe")]
    [InlineData("", @"C:\Program Files\Reframe")]
    [InlineData("   ", @"C:\Program Files\Reframe")]
    [InlineData(null, null)]
    public void IsSameDirectory_Different(string? a, string? b)
        => Assert.False(UpdateScript.IsSameDirectory(a, b));

    [Fact(DisplayName = "NormalizeDirectory returns a canonical, separator-trimmed absolute path")]
    public void NormalizeDirectory_Canonical()
    {
        Assert.Equal(@"C:\Program Files\Reframe", UpdateScript.NormalizeDirectory(@"C:\Program Files\Reframe\"));
        Assert.Equal(@"C:\Program Files\Reframe", UpdateScript.NormalizeDirectory(@"C:/Program Files/Reframe//"));
        Assert.Equal(@"C:\", UpdateScript.NormalizeDirectory(@"C:\"));
        Assert.Null(UpdateScript.NormalizeDirectory(null));
        Assert.Null(UpdateScript.NormalizeDirectory("   "));
    }
}
