using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Reframe.Services;

/// <summary>
/// Run at startup, implemented with a Windows scheduled task (schtasks).
/// The app is requireAdministrator, so an ordinary Run-key autostart would trigger UAC; a scheduled
/// task with /RL HIGHEST + /SC ONLOGON launches it silently at logon with the highest privileges, no UAC.
/// All calls run silently and don't surface exceptions, reporting success/failure as a bool.
/// </summary>
public static class StartupTaskService
{
    private const string TaskName = "Reframe";

    /// <summary>
    /// Argument handed to the exe when it is launched by the scheduled task: start silently, minimized to
    /// the tray (so a logon launch never pops the main window). A manual double-click passes no args and
    /// shows the window. Parsed by <see cref="StartupOptions.IsMinimized"/>.
    /// </summary>
    private const string MinimizedArg = "--minimized";

    /// <summary>Whether the scheduled task already exists.</summary>
    public static bool IsEnabled()
    {
        // /Query returns 0 on a hit, non-zero if it doesn't exist.
        return Run($"/Query /TN \"{TaskName}\"") == 0;
    }

    /// <summary>
    /// Create/overwrite the scheduled task, pointing at the current exe. When <paramref name="minimized"/>
    /// is true the action carries <c>--minimized</c> so a logon launch starts silently to the tray; when
    /// false the flag is omitted and the logon launch shows the main window. <c>/F</c> overwrites any
    /// existing task of the same name, so toggling start-on-login off→on (or flipping the minimized
    /// option) always rebuilds the action with the current arguments — this is also how an older task
    /// created before the <c>--minimized</c> flag existed gets migrated.
    ///
    /// Implemented with <c>schtasks /Create /XML</c> (rather than the flag form) because only the XML
    /// definition lets us set <c>ExecutionTimeLimit=PT0S</c> (unlimited). The command-line flag form has
    /// no way to disable the limit, so a task created that way inherits Task Scheduler's default 3-day
    /// (<c>PT72H</c>) limit and the running process is terminated after three days of uptime — the
    /// "silently exits after a few days" bug. The XML also disables the on-battery restrictions.
    /// </summary>
    public static bool Enable(bool minimized)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            string userId = WindowsIdentity.GetCurrent().Name;
            string xml = BuildTaskXml(exe, userId, minimized);

            // schtasks /XML requires the file be Unicode (UTF-16); UTF-8 is rejected.
            string tmp = Path.Combine(Path.GetTempPath(), $"Reframe_task_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                // /F overwrites any existing task of the same name.
                return Run($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Build a Task Scheduler 1.2 XML definition for the start-on-login task. Pure function (no
    /// environment/registry access) so it can be unit tested. Key settings:
    /// <list type="bullet">
    /// <item><c>ExecutionTimeLimit=PT0S</c> — unlimited; this is the whole point (see <see cref="Enable"/>).</item>
    /// <item>battery flags false — don't refuse to start or stop when running on battery.</item>
    /// <item>a <c>LogonTrigger</c> for <paramref name="userId"/> — equivalent to the old <c>/SC ONLOGON</c>.</item>
    /// <item>a Principal with <c>HighestAvailable</c> + <c>InteractiveToken</c> — equivalent to <c>/RL HIGHEST</c>,
    /// launching silently at logon with the highest privileges, no UAC.</item>
    /// </list>
    /// <paramref name="exePath"/> and <paramref name="userId"/> are XML-escaped, so paths containing
    /// <c>&amp; &lt; &gt; " '</c> are safe. The declaration is UTF-16 because the file is written as Unicode.
    /// </summary>
    public static string BuildTaskXml(string exePath, string userId, bool minimized)
    {
        string exe = SecurityElement.Escape(exePath) ?? string.Empty;
        string user = SecurityElement.Escape(userId) ?? string.Empty;
        string argsElement = minimized ? $"\n      <Arguments>{MinimizedArg}</Arguments>" : "";

        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Description>Reframe start-on-login</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <LogonTrigger>\n" +
            "      <Enabled>true</Enabled>\n" +
            $"      <UserId>{user}</UserId>\n" +
            "    </LogonTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            $"      <UserId>{user}</UserId>\n" +
            "      <LogonType>InteractiveToken</LogonType>\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\n" +
            "    <StartWhenAvailable>false</StartWhenAvailable>\n" +
            "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n" +
            "    <IdleSettings>\n" +
            "      <StopOnIdleEnd>false</StopOnIdleEnd>\n" +
            "      <RestartOnIdle>false</RestartOnIdle>\n" +
            "    </IdleSettings>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <Hidden>false</Hidden>\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
            "    <WakeToRun>false</WakeToRun>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <Priority>7</Priority>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            $"      <Command>{exe}</Command>{argsElement}\n" +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>\n";
    }

    /// <summary>Delete the scheduled task.</summary>
    public static bool Disable()
    {
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }

    /// <summary>
    /// Reconcile an existing start-on-login task with the user's current preference and, crucially, heal
    /// a stale execution time limit. Two things are checked against the task XML:
    /// <list type="number">
    /// <item>the action's <c>--minimized</c> flag presence matches <paramref name="minimized"/> — so a
    /// user who already enabled autostart gets the behaviour their config asks for without re-toggling
    /// (an older task created before the flag existed picks up the silent default, and flipping the
    /// option later takes effect on the next launch);</item>
    /// <item>the settings carry <c>ExecutionTimeLimit=PT0S</c> (unlimited) — so a task created by the old
    /// flag-based code, which inherited Task Scheduler's default 3-day (<c>PT72H</c>) limit and was
    /// therefore terminated after three days of uptime, is silently rebuilt with no limit.</item>
    /// </list>
    /// If either check fails we rebuild via <see cref="Enable"/> (which writes the corrected XML). We
    /// only leave the task alone when both are already satisfied.
    ///
    /// No-op when the task doesn't exist (nothing to migrate). Best-effort and non-throwing; safe to call
    /// on every startup. Returns true when a rebuild was actually performed. Any failure is swallowed: a
    /// migration hiccup must never block startup, and the worst case is the pre-existing behaviour.
    /// </summary>
    public static bool MigrateIfNeeded(bool minimized)
    {
        try
        {
            // /XML dumps the task definition; /Query alone (exit 0) only tells us it exists.
            string xml = RunCapture($"/Query /TN \"{TaskName}\" /XML", out int code);
            if (code != 0) return false; // no such task → nothing to migrate

            // Does the current action carry the flag? (case-insensitive; XML is exe path + args text).
            bool hasFlag = xml.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase);
            bool flagMatches = hasFlag == minimized;

            // Is the execution time limit already unlimited? schtasks /Query /XML emits exactly
            // "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" for an unlimited task; a stale (old
            // flag-created) task has PT72H, which fails this check and triggers a rebuild.
            bool unlimited = xml.Contains("<ExecutionTimeLimit>PT0S", StringComparison.OrdinalIgnoreCase);

            // Both already correct → leave it alone.
            if (flagMatches && unlimited) return false;

            // Disagrees on either axis → rebuild with the current exe, the desired flag, and no limit.
            return Enable(minimized);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Run schtasks silently and return its exit code; -1 if it failed to start.</summary>
    private static int Run(string arguments)
    {
        RunCapture(arguments, out int code);
        return code;
    }

    /// <summary>
    /// Run schtasks silently, capture stdout, and report the exit code via <paramref name="exitCode"/>
    /// (-1 if the process failed to start). Used by <see cref="MigrateIfNeeded"/> to inspect the task XML.
    /// </summary>
    private static string RunCapture(string arguments, out int exitCode)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) { exitCode = -1; return string.Empty; }
            // Read stdout before WaitForExit to avoid a deadlock if the pipe buffer fills.
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            exitCode = p.ExitCode;
            return stdout;
        }
        catch
        {
            exitCode = -1;
            return string.Empty;
        }
    }
}
