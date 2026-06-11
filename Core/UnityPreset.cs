using Microsoft.Win32;

namespace Reframe.Core;

/// <summary>
/// Writes Unity startup-resolution presets to the registry (see the DESIGN research conclusions / the fix
/// for Genshin stretching).
///
/// Unity (e.g. Genshin on Unity 2017.4) pins the render-buffer resolution in three DWORDs under
/// <c>HKCU\Software\miHoYo\原神</c>:
/// <list type="bullet">
///   <item><c>Screenmanager Resolution Width_h&lt;hash&gt;</c></item>
///   <item><c>Screenmanager Resolution Height_h&lt;hash&gt;</c></item>
///   <item><c>Screenmanager Is Fullscreen mode_h&lt;hash&gt;</c></item>
/// </list>
/// The hash suffix varies per game, so value names are matched by **prefix** (may hit several; write them
/// all). The game reads the registry to render; an external resize just scales (stretches) the whole frame,
/// and the game writes the current values back on exit — so writing only makes sense while the game is **not
/// running**. This class only writes Unity's standard keys and never creates missing values, so it's safe.
/// </summary>
public static class UnityPreset
{
    /// <summary>The value-name prefixes for the target DWORDs.</summary>
    public const string WidthPrefix = "Screenmanager Resolution Width";
    public const string HeightPrefix = "Screenmanager Resolution Height";
    public const string FullscreenPrefix = "Screenmanager Is Fullscreen mode";

    /// <summary>
    /// Write the target resolution to the registry (pure I/O). Returns whether at least one value was actually
    /// written. If the key, or all three value classes, don't exist → don't create them, return false (the
    /// game isn't installed or the path is wrong; don't pollute the registry).
    /// </summary>
    /// <param name="registryPath">A path relative to HKCU, e.g. <c>Software\miHoYo\原神</c>.</param>
    public static bool Write(string registryPath, int width, int height, bool windowed)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return false;

        // Don't create the key: open an existing key for writing; if it doesn't exist, returns null.
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
        if (key is null) return false;

        var names = key.GetValueNames();
        int fullscreenValue = windowed ? 0 : 1;

        bool wrote = false;
        foreach (var name in names)
        {
            if (Matches(name, WidthPrefix))
            {
                key.SetValue(name, width, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (Matches(name, HeightPrefix))
            {
                key.SetValue(name, height, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (Matches(name, FullscreenPrefix))
            {
                key.SetValue(name, fullscreenValue, RegistryValueKind.DWord);
                wrote = true;
            }
        }

        return wrote;
    }

    /// <summary>
    /// Read the registry's current values and decide whether they already equal the target (every matched
    /// width/height/fullscreen value agrees). Returns true if no write is needed; if the key is missing or a
    /// value is absent, returns false (leaving Write to decide whether it can write). Used as a cheap
    /// comparison during tick correction, to avoid redundant writes.
    /// </summary>
    public static bool AlreadyMatches(string registryPath, int width, int height, bool windowed)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return false;

        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        if (key is null) return false;

        int fullscreenValue = windowed ? 0 : 1;
        bool sawWidth = false, sawHeight = false, sawFullscreen = false;

        foreach (var name in key.GetValueNames())
        {
            if (Matches(name, WidthPrefix))
            {
                sawWidth = true;
                if (ReadDword(key, name) != width) return false;
            }
            else if (Matches(name, HeightPrefix))
            {
                sawHeight = true;
                if (ReadDword(key, name) != height) return false;
            }
            else if (Matches(name, FullscreenPrefix))
            {
                sawFullscreen = true;
                if (ReadDword(key, name) != fullscreenValue) return false;
            }
        }

        // All three classes must be seen and all agree to count as "already matching". If any class is missing → let Write fill it in.
        return sawWidth && sawHeight && sawFullscreen;
    }

    /// <summary>Whether the value name starts with the given prefix (the hash suffix is unknown, so prefix matching is key). Pure function, unit-test target.</summary>
    public static bool Matches(string valueName, string prefix)
        => valueName.StartsWith(prefix, StringComparison.Ordinal);

    private static int? ReadDword(RegistryKey key, string name)
    {
        var v = key.GetValue(name);
        return v is int i ? i : (int?)null;
    }
}
