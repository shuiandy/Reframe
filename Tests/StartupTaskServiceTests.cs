using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// StartupTaskService.BuildTaskXml: the Task Scheduler 1.2 XML that fixes the "silently exits after a
/// few days" bug. The critical fact is the unlimited execution time limit (PT0S); the rest guards the
/// minimized-flag wiring, the on-battery behaviour, the privilege level, the trigger, and XML escaping.
/// </summary>
public class StartupTaskServiceTests
{
    private const string Exe = @"C:\Program Files\Reframe\Reframe.exe";
    private const string User = @"DESKTOP-ABC\Andy";

    [Fact(DisplayName = "Execution time limit is unlimited (PT0S) - the core fix")]
    public void UnlimitedExecutionTimeLimit()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: false);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        // Never the Task Scheduler default that caused the 3-day termination.
        Assert.DoesNotContain("PT72H", xml);
    }

    [Fact(DisplayName = "minimized=true -> carries <Arguments>--minimized</Arguments>")]
    public void Minimized_HasArguments()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: true);
        Assert.Contains("<Arguments>--minimized</Arguments>", xml);
    }

    [Fact(DisplayName = "minimized=false -> no Arguments element and no --minimized")]
    public void NotMinimized_NoArguments()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: false);
        Assert.DoesNotContain("<Arguments>", xml);
        Assert.DoesNotContain("--minimized", xml);
    }

    [Fact(DisplayName = "Both battery flags are false (don't refuse/stop on battery)")]
    public void BatteryFlagsFalse()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: false);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
        // And definitely not the (schema-default) true.
        Assert.DoesNotContain("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml);
        Assert.DoesNotContain("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>", xml);
    }

    [Fact(DisplayName = "Runs elevated (HighestAvailable) via a logon trigger")]
    public void ElevatedLogonTrigger()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: false);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonTrigger>", xml);
    }

    [Fact(DisplayName = "exePath with & is XML-escaped to &amp;")]
    public void AmpersandInPathIsEscaped()
    {
        string xml = StartupTaskService.BuildTaskXml(@"C:\a&b\Reframe.exe", User, minimized: false);
        Assert.Contains(@"<Command>C:\a&amp;b\Reframe.exe</Command>", xml);
        // The raw, unescaped ampersand must never appear inside the command.
        Assert.DoesNotContain(@"C:\a&b\Reframe.exe", xml);
    }

    [Fact(DisplayName = "userId with special chars is XML-escaped")]
    public void SpecialCharsInUserAreEscaped()
    {
        // A userId containing < > & " ' must all be escaped; here we check the ampersand and angle bracket.
        string xml = StartupTaskService.BuildTaskXml(Exe, @"DOM\a<b&c", minimized: false);
        Assert.Contains("a&lt;b&amp;c", xml);
        Assert.DoesNotContain("a<b&c", xml);
    }

    [Fact(DisplayName = "Declares the UTF-16 encoding schtasks /XML requires")]
    public void DeclaresUtf16()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: false);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
    }

    // ------------------------------------------------------------------ TaskCommandIs (installed copy claims the task)

    [Fact(DisplayName = "TaskCommandIs: a task built for this exe matches it")]
    public void TaskCommandIs_RoundTrip()
    {
        string xml = StartupTaskService.BuildTaskXml(Exe, User, minimized: true);
        Assert.True(StartupTaskService.TaskCommandIs(xml, Exe));
        Assert.True(StartupTaskService.TaskCommandIs(xml, @"c:\program files\reframe\REFRAME.EXE"));
    }

    [Fact(DisplayName = "TaskCommandIs: a task pointing at a dev build does not match the installed exe")]
    public void TaskCommandIs_OtherExe()
    {
        string xml = StartupTaskService.BuildTaskXml(@"C:\Users\andy\ClaudeOutputs-local\build\Reframe\Reframe\bin\x64\Debug\Reframe.exe", User, minimized: true);
        Assert.False(StartupTaskService.TaskCommandIs(xml, Exe));
    }

    [Fact(DisplayName = "TaskCommandIs: escaped and quoted commands are compared as paths")]
    public void TaskCommandIs_EscapedAndQuoted()
    {
        string escaped = StartupTaskService.BuildTaskXml(@"C:\a&b\Reframe.exe", User, minimized: false);
        Assert.True(StartupTaskService.TaskCommandIs(escaped, @"C:\a&b\Reframe.exe"));

        // The old flag-based schtasks /TR form stored the path with quotes.
        string quoted = StartupTaskService.BuildTaskXml(Exe, User, minimized: false)
            .Replace($"<Command>{Exe}</Command>", $"<Command>\"{Exe}\"</Command>");
        Assert.Contains("<Command>\"", quoted);
        Assert.True(StartupTaskService.TaskCommandIs(quoted, Exe));
    }

    [Theory(DisplayName = "TaskCommandIs: unreadable input never matches")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Task><Actions><Exec></Exec></Actions></Task>")]
    public void TaskCommandIs_Unreadable(string? xml)
        => Assert.False(StartupTaskService.TaskCommandIs(xml, Exe));
}
