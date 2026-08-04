using System.Text;

namespace Reframe.Services;

/// <summary>
/// Generates the PowerShell helper that actually swaps the files, plus the path comparison used to
/// decide which flavour of update we are doing.
///
/// <para><b>Why a helper script at all:</b> a process cannot overwrite its own executable while it is
/// running. So Reframe stages the download, writes a script, starts it, and exits; the script waits
/// for the PID to disappear, applies the update, and starts the new build. The script inherits
/// Reframe's token via <c>Process.Start</c>, so an elevated Reframe hands its elevation straight to
/// the script and no second UAC prompt appears.</para>
///
/// <para><b>The script never deletes anything.</b> Portable mode copies the new files over the old
/// ones and leaves every other file in the folder alone; installer mode just runs the signed-off
/// Setup.exe. There is no <c>Remove-Item</c>, no <c>rmdir</c>, no "clean the target first" step — a
/// half-failed update must cost the user nothing. (Config lives in
/// <c>%LOCALAPPDATA%\Reframe</c> and is not touched by either mode.) Every step appends to
/// <c>update.log</c> next to the staged files, and any failure writes an <c>ERROR</c> line that
/// Reframe surfaces on the Settings page the next time it starts.</para>
///
/// <para><b>Pure ASCII output, guaranteed.</b> The scripts run under Windows PowerShell 5.1, whose
/// default encoding for a <c>-File</c> script is the ANSI code page unless there is a BOM — so any
/// non-ASCII byte in the file (a user name with CJK characters in <c>%TEMP%</c>, say) would be
/// mis-decoded and could corrupt a path. Rather than depend on encoding, every path is embedded as
/// base64 of its UTF-8 bytes and decoded at run time. That keeps the file pure ASCII whatever the
/// paths look like, and it is also airtight quoting: base64 cannot contain a quote, a backtick, a
/// <c>$</c> or a newline, so no path can break out of its string literal.</para>
///
/// <para>Pure by design (string building only): linked into <c>Tests\Reframe.Core.Tests.csproj</c>,
/// where the generated text is asserted to be ASCII, to contain no destructive command, and to
/// round-trip every path.</para>
/// </summary>
public static class UpdateScript
{
    /// <summary>File name of the generated script inside the staging folder.</summary>
    public const string ScriptFileName = "apply-update.ps1";

    /// <summary>File name of the log the script appends to inside the staging folder.</summary>
    public const string LogFileName = "update.log";

    /// <summary>Marker the script writes before any failure; Reframe scans for it on the next start.</summary>
    public const string ErrorMarker = "ERROR ";

    /// <summary>How long the script waits for Reframe to exit before giving up without touching anything.</summary>
    private const int ExitWaitSeconds = 120;

    // ---------------------------------------------------------------- path helpers

    /// <summary>
    /// Canonical form of a directory path for comparison: trimmed, unquoted, made absolute, separators
    /// normalised, trailing separators removed (a bare drive keeps its root backslash so <c>C:</c> and
    /// <c>C:\</c> agree). Returns null for anything empty or malformed.
    /// </summary>
    public static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string s = path.Trim().Trim('"');
        if (s.Length == 0) return null;

        // "C:" means "the current directory on drive C" to Path.GetFullPath, which is never what an
        // InstallLocation value means. Pin a bare drive to its root before resolving.
        if (s.Length == 2 && s[1] == ':' && char.IsLetter(s[0])) s += "\\";

        try
        {
            s = Path.GetFullPath(s); // collapses "." / "..", turns '/' into '\'
        }
        catch
        {
            return null; // invalid characters, path too long, ...
        }

        s = s.TrimEnd('\\', '/');
        if (s.Length == 0) return null;
        if (s.Length == 2 && s[1] == ':') s += "\\"; // "C:" -> "C:\"
        return s;
    }

    /// <summary>
    /// Do these two strings name the same directory? Used to decide install mode: the Inno uninstall
    /// key's <c>InstallLocation</c> (which ends with a backslash) is compared with
    /// <see cref="AppContext.BaseDirectory"/> (which also does, but may differ in case or separator
    /// style). Either side unusable → false, i.e. "not the installed copy", which is the safe answer.
    /// </summary>
    public static bool IsSameDirectory(string? a, string? b)
    {
        string? na = NormalizeDirectory(a);
        string? nb = NormalizeDirectory(b);
        if (na is null || nb is null) return false;
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- script builders

    /// <summary>
    /// Installer flavour: wait for <paramref name="pid"/> to exit, run the downloaded Setup.exe with
    /// <c>/SILENT /NORESTART</c>, then start <paramref name="appExePath"/> from the install folder.
    /// </summary>
    /// <param name="pid">PID of the Reframe process that is about to exit.</param>
    /// <param name="setupPath">Full path of the downloaded <c>Reframe-Setup-v*.exe</c>.</param>
    /// <param name="appExePath">Full path of <c>Reframe.exe</c> to start once setup succeeds.</param>
    /// <param name="logPath">Full path of the log file to append to.</param>
    public static string BuildInstallerScript(int pid, string setupPath, string appExePath, string logPath)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb, "installer", pid, logPath);

        sb.Append("$Setup  = Dec('").Append(Encode(setupPath)).Append("')\n");
        sb.Append("$AppExe = Dec('").Append(Encode(appExePath)).Append("')\n");
        sb.Append('\n');
        sb.Append("if (-not (Test-Path -LiteralPath $Setup)) { Fail 'downloaded setup package is missing' }\n");
        sb.Append("Log ('running setup: ' + $Setup)\n");
        sb.Append("$proc = Start-Process -FilePath $Setup -ArgumentList '/SILENT','/NORESTART' -Wait -PassThru\n");
        sb.Append("$code = $proc.ExitCode\n");
        sb.Append("Log ('setup exit code ' + $code)\n");
        // 0 = installed. 1641 / 3010 = installed, reboot pending: still a success for our purposes.
        sb.Append("if (($code -ne 0) -and ($code -ne 1641) -and ($code -ne 3010)) {\n");
        sb.Append("  Fail ('setup returned exit code ' + $code + '; the previous version is still installed')\n");
        sb.Append("}\n");
        sb.Append('\n');

        AppendRelaunch(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Portable flavour: wait for <paramref name="pid"/> to exit, then copy every staged file over the
    /// running folder (creating sub-directories as needed, overwriting what collides, removing
    /// nothing), then start <paramref name="appExePath"/>.
    /// </summary>
    /// <param name="pid">PID of the Reframe process that is about to exit.</param>
    /// <param name="extractedDir">Folder the release zip was unpacked into.</param>
    /// <param name="targetDir">The running installation folder (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="appExePath">Full path of <c>Reframe.exe</c> to start once the copy succeeds.</param>
    /// <param name="logPath">Full path of the log file to append to.</param>
    public static string BuildPortableScript(int pid, string extractedDir, string targetDir, string appExePath, string logPath)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb, "portable", pid, logPath);

        sb.Append("$Src    = Dec('").Append(Encode(extractedDir)).Append("')\n");
        sb.Append("$Dst    = Dec('").Append(Encode(targetDir)).Append("')\n");
        sb.Append("$AppExe = Dec('").Append(Encode(appExePath)).Append("')\n");
        sb.Append('\n');
        sb.Append("if (-not (Test-Path -LiteralPath $Src)) { Fail 'staged payload folder is missing' }\n");
        sb.Append("if (-not (Test-Path -LiteralPath $Dst)) { Fail 'installation folder is missing' }\n");
        sb.Append('\n');
        sb.Append("$SrcRoot = (Resolve-Path -LiteralPath $Src).Path.TrimEnd('\\')\n");
        sb.Append("$files = @(Get-ChildItem -LiteralPath $Src -Recurse -Force -File)\n");
        sb.Append("if ($files.Count -eq 0) { Fail 'staged payload folder is empty' }\n");
        sb.Append("Log ('copying ' + $files.Count + ' files into ' + $Dst)\n");
        sb.Append('\n');
        // Explicit per-file copy instead of "Copy-Item <src>\* -Recurse -Force": it is unambiguous about
        // merging into existing sub-directories, it is LiteralPath-safe for folder names containing
        // wildcard characters, and it makes it self-evident that nothing is ever removed.
        sb.Append("$copied = 0\n");
        sb.Append("foreach ($f in $files) {\n");
        sb.Append("  $rel = $f.FullName.Substring($SrcRoot.Length + 1)\n");
        sb.Append("  $target = Join-Path $Dst $rel\n");
        sb.Append("  $targetDir = Split-Path -Parent $target\n");
        sb.Append("  if (-not (Test-Path -LiteralPath $targetDir)) {\n");
        sb.Append("    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null\n");
        sb.Append("  }\n");
        // The old exe/dlls can stay locked for a moment after the process object goes away; retry rather
        // than fail the whole update on a transient sharing violation.
        sb.Append("  $done = $false\n");
        sb.Append("  for ($attempt = 1; $attempt -le 10; $attempt++) {\n");
        sb.Append("    try { Copy-Item -LiteralPath $f.FullName -Destination $target -Force; $done = $true; break }\n");
        sb.Append("    catch { Log ('retry ' + $attempt + ' for ' + $rel + ': ' + $_.Exception.Message); Start-Sleep -Seconds 2 }\n");
        sb.Append("  }\n");
        sb.Append("  if (-not $done) { Fail ('could not overwrite ' + $rel + '; the folder is unchanged apart from files already copied') }\n");
        sb.Append("  $copied++\n");
        sb.Append("}\n");
        sb.Append("Log ('copied ' + $copied + ' files')\n");
        sb.Append('\n');

        AppendRelaunch(sb);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- shared pieces

    /// <summary>Header + logging helpers + "wait for the old process to go away" gate.</summary>
    private static void AppendPreamble(StringBuilder sb, string mode, int pid, string logPath)
    {
        sb.Append("# Reframe self-update helper - generated by Reframe, do not edit.\n");
        sb.Append("# Mode: ").Append(mode).Append('\n');
        // (Worded without the obvious verbs on purpose: UpdateScriptTests greps the generated text for
        // every destructive cmdlet name, and a comment mentioning one would trip that check.)
        sb.Append("# This script only ever creates or overwrites files - it never destroys anything,\n");
        sb.Append("# by design: a failed update must never cost the user a file.\n");
        sb.Append("$ErrorActionPreference = 'Stop'\n");
        sb.Append('\n');
        // Paths are base64 UTF-8 so this file stays pure ASCII and no path can break out of its literal.
        sb.Append("function Dec([string]$b64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) }\n");
        sb.Append('\n');
        sb.Append("$LogPath = Dec('").Append(Encode(logPath)).Append("')\n");
        sb.Append("function Log([string]$m) {\n");
        sb.Append("  $line = '[' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '] ' + $m\n");
        sb.Append("  try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch { }\n");
        sb.Append("}\n");
        sb.Append("function Fail([string]$m) { Log ('").Append(ErrorMarker).Append("' + $m); exit 1 }\n");
        sb.Append('\n');
        sb.Append("Log 'update started (mode=").Append(mode).Append(")'\n");
        sb.Append('\n');
        sb.Append("$TargetPid = ").Append(pid.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("try { Wait-Process -Id $TargetPid -Timeout ")
          .Append(ExitWaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append(" -ErrorAction SilentlyContinue } catch { }\n");
        sb.Append("if (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) {\n");
        sb.Append("  Fail 'Reframe did not exit in time; nothing was changed'\n");
        sb.Append("}\n");
        sb.Append("Log 'previous instance has exited'\n");
        sb.Append('\n');
    }

    /// <summary>Start the updated build and close the log out. Shared by both modes.</summary>
    private static void AppendRelaunch(StringBuilder sb)
    {
        sb.Append("if (Test-Path -LiteralPath $AppExe) {\n");
        sb.Append("  Log ('starting ' + $AppExe)\n");
        sb.Append("  Start-Process -FilePath $AppExe\n");
        sb.Append("} else {\n");
        sb.Append("  Log 'WARN updated Reframe.exe not found; please start Reframe manually'\n");
        sb.Append("}\n");
        sb.Append("Log 'update finished'\n");
        sb.Append("exit 0\n");
    }

    /// <summary>base64 of the UTF-8 bytes: ASCII-only and unbreakable inside a single-quoted literal.</summary>
    private static string Encode(string? value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}
