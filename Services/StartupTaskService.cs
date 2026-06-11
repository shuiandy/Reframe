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

    /// <summary>Whether the scheduled task already exists.</summary>
    public static bool IsEnabled()
    {
        // /Query returns 0 on a hit, non-zero if it doesn't exist.
        return Run($"/Query /TN \"{TaskName}\"") == 0;
    }

    /// <summary>Create/overwrite the scheduled task, pointing at the current exe.</summary>
    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // /F overwrites a task of the same name; /TR needs an extra layer of quotes when the path contains spaces.
        string args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\" /F";
        return Run(args) == 0;
    }

    /// <summary>Delete the scheduled task.</summary>
    public static bool Disable()
    {
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }

    /// <summary>Run schtasks silently and return its exit code; -1 if it failed to start.</summary>
    private static int Run(string arguments)
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
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
