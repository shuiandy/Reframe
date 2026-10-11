using System.IO;

namespace Reframe.Services;

/// <summary>
/// On-disk trail of what the window-persistence engine did and why:
/// <c>%LOCALAPPDATA%\Reframe\persistence.log</c>. The dashboard shows the same lines, but only as an in-memory
/// ring buffer that dies with the process — and "a window jumped for no reason, some time yesterday" is exactly
/// the kind of report that can only be investigated after the fact. Mirrors the hotkey log's conventions
/// (append-only, local time stamps, a logging failure never propagates), plus a size cap: at
/// <see cref="MaxBytes"/> the file is rotated once to <c>persistence.log.1</c> so it can never grow without
/// bound on a machine whose display blinks all day.
/// </summary>
public static class PersistenceLog
{
    public const long MaxBytes = 1_000_000;

    private static readonly object _gate = new();

    public static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe", "persistence.log");

    /// <summary>Append one line. Callable from any thread; never throws.</summary>
    public static void Write(string line)
    {
        try
        {
            string path = Path_;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            lock (_gate)
            {
                RotateIfLarge(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never affect the engine */ }
    }

    private static void RotateIfLarge(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            File.Move(path, path + ".1", overwrite: true);
        }
        catch { /* best effort; the append below still works on the current file */ }
    }
}
