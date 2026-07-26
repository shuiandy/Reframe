using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reframe.Core;

/// <summary>
/// One remembered window as written to disk. Plain get/set properties (not a positional record) so
/// System.Text.Json source generation has a trivial job and a future field addition stays backward compatible
/// — an old file simply leaves the new property at its default.
///
/// <para><b>No handle is persisted.</b> A HWND is meaningful only inside the session that observed it; see
/// <see cref="LayoutMemory.ImportFromDisk"/>.</para>
/// </summary>
public sealed class PersistedWindow
{
    // Identity (see WindowIdentity — the title is stored but is NOT part of the identity).
    public string Process { get; set; } = "";
    public string Class { get; set; } = "";
    public int Ordinal { get; set; }
    public string Title { get; set; } = "";

    // Geometry: screen rect (SetWindowPos path) + rcNormalPosition (SetWindowPlacement path) + show state.
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int NormLeft { get; set; }
    public int NormTop { get; set; }
    public int NormRight { get; set; }
    public int NormBottom { get; set; }
    public int ShowCmd { get; set; }

    /// <summary>Last time a capture refreshed this record; drives aging on load/save.</summary>
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>All remembered windows for one DisplayKey.</summary>
public sealed class PersistedLayout
{
    public string DisplayKey { get; set; } = "";
    public List<PersistedWindow> Windows { get; set; } = new();
}

/// <summary>The whole file. <see cref="Version"/> exists so a future shape change can migrate instead of guessing.</summary>
public sealed class LayoutFile
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<PersistedLayout> Layouts { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LayoutFile))]
public partial class LayoutJsonContext : JsonSerializerContext { }

/// <summary>
/// Disk persistence for the window layout memory: <c>%LOCALAPPDATA%\Reframe\window-layouts.json</c>.
/// Deliberately a sibling of, not part of, <c>config.json</c> — the write rates are orders of magnitude apart
/// and a layout write must never risk the user's settings file.
///
/// <para>Lives in Core (not Services) because <see cref="PersistenceEngine"/> owns it and Core must not
/// reference Services; only BCL types (System.IO / System.Text.Json) are used, so the Tests project can link
/// and exercise it. It mirrors <c>Services.ConfigStore</c>'s conventions on purpose: source-generated JSON,
/// atomic tmp→Move writes, and a read path that degrades to "empty" instead of throwing.</para>
/// </summary>
public static class LayoutStore
{
    /// <summary>Same directory as config.json (derived identically).</summary>
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");

    public static string Path_ => Path.Combine(Dir, "window-layouts.json");

    /// <summary>
    /// Read the layout file. <b>Never throws and never blocks startup</b>: a missing file, a half-written file
    /// (we could be racing another process's write), corrupt JSON, a permission failure, or a file from a
    /// future schema version all degrade to an empty <see cref="LayoutFile"/> — worst case the user loses
    /// layout memory, which the next capture round rebuilds. Unlike ConfigStore there is no quarantine copy:
    /// this data is disposable and re-derived continuously.
    /// </summary>
    public static LayoutFile Load(string? path = null)
    {
        try
        {
            string p = path ?? Path_;
            if (!File.Exists(p)) return new LayoutFile();
            string json = File.ReadAllText(p);
            var file = JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutFile);
            if (file == null) return new LayoutFile();
            // Unknown (newer) schema: don't try to interpret it, and don't delete it either — this build just
            // starts from empty and will overwrite on its next save.
            if (file.Version > LayoutFile.CurrentVersion) return new LayoutFile();
            file.Layouts ??= new List<PersistedLayout>();
            return file;
        }
        catch
        {
            return new LayoutFile();
        }
    }

    /// <summary>
    /// Atomic write (tmp → <c>File.Move(overwrite)</c>, same volume) so a reader — or a crash mid-write —
    /// never sees a truncated file. The tmp name carries a GUID so concurrent writers can't clobber each
    /// other's temp file. Returns false on any failure instead of throwing: losing a layout save must never
    /// take down the persistence worker.
    /// </summary>
    public static bool Save(LayoutFile file, string? path = null)
    {
        string p = path ?? Path_;
        string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(file, LayoutJsonContext.Default.LayoutFile);
            File.WriteAllText(tmp, json);
            File.Move(tmp, p, overwrite: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }
}
