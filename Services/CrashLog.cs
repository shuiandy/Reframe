namespace Reframe.Services;

/// <summary>
/// Shared crash/diagnostic sink: appends timestamped entries to
/// <c>%LOCALAPPDATA%\Reframe\crash.log</c>. The format matches the original
/// <c>App.LogCrash</c> (a <c>=== &lt;timestamp&gt; [source] ===</c> header followed by the
/// exception text), so existing log readers keep working.
///
/// <para>This exists because the process has several manually-created background threads
/// (the tray / hotkey message pumps, the WinEvent / DragSnap hook pumps, the watcher's poll
/// loop and debounce timers). An unhandled exception thrown on a raw <see cref="System.Threading.Thread"/>
/// or a thread-pool timer callback does <b>not</b> reach <c>App.UnhandledException</c> /
/// <c>AppDomain.UnhandledException</c> in a way that runs our managed logging before the CLR
/// fast-fails the process — so without wrapping those procs the process can vanish leaving no
/// crash.log entry and no WER event. Every such proc now funnels through here.</para>
///
/// <para>Layering: lives in Services and has no UI dependency, so Services classes (TrayIcon /
/// HotkeyService) and App call it directly. Core classes must not reference Services, so they
/// instead expose an injectable <c>OnThreadError</c> callback that App wires to
/// <see cref="Write"/> at startup — keeping the Core-has-zero-UI/Services-dependency red line
/// intact (Tests link Core source files and must still compile standalone).</para>
/// </summary>
public static class CrashLog
{
    private static readonly object _gate = new();

    /// <summary>Record an exception (or a bare marker if <paramref name="ex"/> is null) under
    /// <paramref name="source"/>. Never throws — a logging failure must not cascade.</summary>
    public static void Write(string source, Exception? ex)
        => Append(source, ex?.ToString() ?? "(null exception)");

    /// <summary>Record a plain diagnostic note (no exception): lifecycle markers such as
    /// startup/exit, so a later read can tell "known clean exit" from "vanished without a
    /// trace". Never throws.</summary>
    public static void Note(string msg) => Append("Note", msg);

    private static void Append(string source, string body)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");
            System.IO.Directory.CreateDirectory(dir);
            string text = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ==={Environment.NewLine}" +
                          body + Environment.NewLine + Environment.NewLine;
            // Serialize across our own threads; cross-process contention falls back to the catch.
            lock (_gate)
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), text);
        }
        catch { /* a logging failure must not throw again */ }
    }
}
