using System.Diagnostics;

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
    /// </summary>
    public static bool Enable(bool minimized)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // /F overwrites a task of the same name; /TR needs an extra layer of quotes when the path contains
        // spaces. When present, the trailing " --minimized" sits outside the path quotes so it's parsed as
        // a separate arg.
        string tail = minimized ? $" {MinimizedArg}" : "";
        string args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"{tail}\" /F";
        return Run(args) == 0;
    }

    /// <summary>Delete the scheduled task.</summary>
    public static bool Disable()
    {
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }

    /// <summary>
    /// Reconcile an existing start-on-login task's <c>--minimized</c> argument with the user's current
    /// preference (<paramref name="minimized"/>), so a user who already enabled autostart gets the
    /// behaviour their config asks for without having to re-toggle it. This is how an older task created
    /// before the flag existed picks up the silent default, and how flipping the option later takes
    /// effect on the next launch.
    ///
    /// Target state: the task exists AND its action's <c>--minimized</c> presence equals
    /// <paramref name="minimized"/>. We query the task XML and check whether the flag is present; if that
    /// disagrees with the desired state we rebuild via <see cref="Enable"/> (which adds or omits the flag
    /// accordingly). This covers both directions — adding the flag to a stale task and removing it when
    /// the user turned the option off.
    ///
    /// No-op when the task doesn't exist (nothing to migrate) or already matches. Best-effort and
    /// non-throwing; safe to call on every startup. Returns true when a rebuild was actually performed.
    /// Any failure is swallowed: a migration hiccup must never block startup, and the worst case is the
    /// pre-existing behaviour the user already had.
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

            // Already matches the desired state → leave it alone.
            if (hasFlag == minimized) return false;

            // Disagrees → rebuild with the current exe and the desired flag state.
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
