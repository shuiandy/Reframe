namespace Reframe.Services;

/// <summary>
/// Parses process command-line arguments for startup behaviour flags.
///
/// The only flag today is the "start minimized to the tray" switch, used by the start-on-login
/// scheduled task so that a logon launch never pops the main window. A manual double-click of the
/// exe passes no such flag, so it shows the window as usual.
///
/// Kept as a tiny pure function (no <c>Environment</c> access, no Windows types) so it can be unit
/// tested in the Core.Tests project. The caller in <c>App.OnLaunched</c> feeds it
/// <c>Environment.GetCommandLineArgs()</c> — on unpackaged WinUI 3 the
/// <c>LaunchActivatedEventArgs</c> arguments are unreliable, so we read the real process args.
/// </summary>
public static class StartupOptions
{
    /// <summary>
    /// True when the args request a silent, minimized-to-tray start. Recognises <c>--minimized</c>
    /// and (for convenience) <c>/minimized</c>, case-insensitively. The first element of
    /// <c>Environment.GetCommandLineArgs()</c> is the exe path; it is simply ignored here because it
    /// won't match either token.
    /// </summary>
    public static bool IsMinimized(IEnumerable<string>? args)
    {
        if (args is null) return false;
        foreach (string? a in args)
        {
            if (string.IsNullOrEmpty(a)) continue;
            if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/minimized", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
