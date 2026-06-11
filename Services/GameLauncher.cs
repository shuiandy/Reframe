using System.Diagnostics;
using Reframe.Core;

namespace Reframe.Services;

/// <summary>
/// Mini game library: launch a game from its profile with one click.
///
/// Ordered closure (preset first, then launch): if the profile enables a startup resolution preset
/// (<see cref="UnityResolutionPreset"/>) and the target process is **not currently running**, call
/// <see cref="UnityPreset.Write"/> first to write the target resolution into the Screenmanager
/// registry, then launch the game — so the game reads the target resolution at startup (for Unity
/// games such as Genshin whose render resolution is pinned in the registry). If the process is
/// already running, the preset is not written (the game writes the current value back on exit, so a
/// mid-run write is pointless), and the game is not launched again.
///
/// Launch target: <see cref="Profile.LaunchCommand"/> if non-empty, otherwise
/// <see cref="Profile.ExePath"/>; if both are empty → failure. Started via ShellExecute: a local exe
/// gets its containing directory as the working directory; a URI / launcher command (e.g.
/// hoyoplay://) is handed to the shell with no working directory.
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// Launch the game for a profile. Returns true on success; on failure returns false and sets
    /// <paramref name="error"/> to a localized human-readable reason (no launch method / file not
    /// found / already running / launch exception). Call on the UI thread (the caller shows the
    /// error in a dialog).
    /// </summary>
    public static bool Launch(Profile p, out string? error)
    {
        error = null;

        // Launch target: prefer LaunchCommand, otherwise ExePath; both empty → no launch method configured.
        string? target = FirstNonEmpty(p.LaunchCommand, p.ExePath);
        if (target is null)
        {
            error = Loc.T("Services/LaunchNoMethod");
            return false;
        }

        // Already running (only reliably determinable for process matches) → don't launch again.
        if (p.MatchKind == MatchKind.Process && IsProcessRunning(p.MatchValue))
        {
            error = Loc.T("Services/LaunchAlreadyRunningFormat", NameOf(p));
            return false;
        }

        // Security allow-list (this process is requireAdministrator; never ShellExecute an arbitrary string):
        //   allow (1) an existing .exe file path; allow (2) an allow-listed protocol prefix (steam:// etc.); reject the rest.
        bool isExe = IsExistingExe(target);
        bool isAllowedUri = !isExe && IsAllowedProtocol(target);

        if (!isExe && !isAllowedUri)
        {
            // Neither an existing exe nor an allow-listed protocol: likely a mistyped path, or a disallowed command.
            error = LooksLikeUri(target)
                ? Loc.T("Services/LaunchUnsupportedCommand")
                : Loc.T("Services/LaunchFileNotFoundFormat", target);
            return false;
        }

        bool isUri = isAllowedUri;

        // Ordered closure: write the resolution preset first (only when the process isn't running), then launch.
        // Note: reaching here means the "already running" check above missed (process not running), so write directly.
        var preset = p.ResolutionPreset;
        if (preset is { Enabled: true } && !string.IsNullOrWhiteSpace(preset.RegistryPath))
        {
            try { UnityPreset.Write(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed); }
            catch { /* a failed preset write doesn't block launch: worst case the game renders at its own remembered resolution */ }
        }

        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            // A local exe gets its containing directory as the working directory (some games rely on CWD to find assets); a URI does not.
            if (!isUri)
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = Loc.T("Services/LaunchFailedFormat", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Allow-list of permitted launch-protocol prefixes (under an admin process, only known game
    /// launcher / web protocols are allowed). A match is handed to the shell as a URI; protocols not
    /// on the list (file://, shell:, cmd pipes, etc.) are all rejected.
    /// </summary>
    private static readonly string[] AllowedSchemes =
    {
        "steam://",        // Steam
        "hoyoplay://",     // miHoYo HoYoPlay
        "com.epicgames.launcher://", // Epic
        "uplay://", "ubisoft://",    // Ubisoft Connect
        "origin://", "ea://", "link2ea://", // EA / Origin
        "battlenet://", "blizzard://",      // Battle.net
        "goggalaxy://",    // GOG Galaxy
        "http://", "https://", // web launch page
    };

    /// <summary>Whether target is an existing .exe file (allow rule 1).</summary>
    private static bool IsExistingExe(string target)
    {
        try
        {
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target);
        }
        catch { return false; } // illegal path characters, etc.
    }

    /// <summary>Whether target starts with an allow-listed protocol prefix (allow rule 2, case-insensitive).</summary>
    private static bool IsAllowedProtocol(string target)
    {
        foreach (var s in AllowedSchemes)
            if (target.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>For error wording only: whether it looks like a protocol URI (contains "://"), to
    /// distinguish "mistyped path" from "protocol not allowed".</summary>
    private static bool LooksLikeUri(string target)
        => target.Contains("://", StringComparison.Ordinal);

    /// <summary>Whether a process with this name is running (ignoring .exe and case). Matches Watcher's check.</summary>
    private static bool IsProcessRunning(string matchValue)
    {
        if (string.IsNullOrWhiteSpace(matchValue)) return false;
        string name = matchValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? matchValue[..^4]
            : matchValue;
        try
        {
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var pr in procs) pr.Dispose(); }
        }
        catch { return false; }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    private static string NameOf(Profile p)
        => string.IsNullOrWhiteSpace(p.Name) ? Loc.T("Services/LaunchUnnamedProfile") : p.Name;
}
