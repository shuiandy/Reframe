using System;
using System.Collections.Concurrent;
using MrtResourceLoader = Microsoft.Windows.ApplicationModel.Resources.ResourceLoader;

namespace Reframe.Services;

/// <summary>
/// Localization helper: a thin wrapper over MRT Core's <c>ResourceLoader</c> for fetching
/// localized strings from code-behind. XAML should prefer <c>x:Uid="/File/Key"</c>; use this
/// only for strings built at runtime (status lines, dialogs, composite-format messages).
///
/// <para>Key format is <c>"File/Key"</c> where <c>File</c> is the .resw base name (e.g.
/// <c>SettingsPage</c>, <c>Common</c>) under <c>Strings\&lt;lang&gt;\</c>, and <c>Key</c> is the
/// resource <c>name</c> in that file. Example: <c>Loc.T("SettingsPage/HotkeySaved")</c>.</para>
///
/// <para>Per-file loaders are created via the two-arg constructor
/// <c>ResourceLoader(GetDefaultResourceFilePath(), file)</c> — the single-arg overload takes a
/// .pri <i>path</i>, not a map name, so it must not be used here. Loaders are cached per file.</para>
///
/// <para><b>Language:</b> resolution follows MRT's runtime context. In unpackaged apps that is the
/// system display language, overridable via
/// <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c> (set once at
/// App startup before any XAML loads — see App.xaml.cs). This helper does not set the override.</para>
///
/// <para><b>Core red line:</b> this type lives in <c>Services</c>, never <c>Core</c>. Core stays
/// MRT-free (its engine log strings are plain English literals). See docs\dev\I18N.md.</para>
/// </summary>
public static class Loc
{
    // One ResourceLoader per .resw file (keyed by file base name). ResourceLoader is agile/thread-safe
    // for GetString; cache to avoid re-resolving the map on every call.
    private static readonly ConcurrentDictionary<string, MrtResourceLoader> _loaders = new();

    private static MrtResourceLoader LoaderFor(string file) =>
        _loaders.GetOrAdd(file, static f =>
            new MrtResourceLoader(MrtResourceLoader.GetDefaultResourceFilePath(), f));

    /// <summary>
    /// Look up a localized string by <c>"File/Key"</c>. On any failure (missing resource, no PRI,
    /// resource system unavailable) returns the input id unchanged so the UI degrades to a visible
    /// marker rather than throwing.
    /// </summary>
    public static string T(string fileSlashKey)
    {
        if (string.IsNullOrEmpty(fileSlashKey)) return fileSlashKey;

        int slash = fileSlashKey.IndexOf('/');
        if (slash <= 0 || slash >= fileSlashKey.Length - 1)
            return fileSlashKey; // not in "File/Key" shape; nothing we can resolve

        string file = fileSlashKey.Substring(0, slash);
        string key = fileSlashKey.Substring(slash + 1);

        try
        {
            string value = LoaderFor(file).GetString(key);
            // MRT returns "" for an unknown key; surface the id instead of a blank control.
            return string.IsNullOrEmpty(value) ? fileSlashKey : value;
        }
        catch
        {
            return fileSlashKey;
        }
    }

    /// <summary>
    /// Same as <see cref="T(string)"/> but runs the resolved value through
    /// <see cref="string.Format(IFormatProvider, string, object[])"/> with <paramref name="args"/>.
    /// The .resw value should use composite-format placeholders (<c>{0}</c>, <c>{1}</c>, …).
    /// If formatting fails (bad placeholder count), the unformatted resource value is returned.
    /// </summary>
    public static string T(string fileSlashKey, params object[] args)
    {
        string format = T(fileSlashKey);
        if (args is null || args.Length == 0) return format;
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
