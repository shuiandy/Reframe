using System.Text.Json;
using Reframe.Core;

namespace Reframe.Services;

/// <summary>Config read/write: %LOCALAPPDATA%\Reframe\config.json</summary>
public static class ConfigStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");

    public static string Path_ => Path.Combine(Dir, "config.json");

    /// <summary>
    /// A pure read attempt: if the file exists, read + deserialize, returning a non-null config on
    /// success; if the file is missing / the read fails / parsing fails / deserialization yields null,
    /// return <c>null</c> in all cases.
    /// <para><b>No side effects</b>: no quarantine, no default fallback, no disk write. For hot reload
    /// use — a file event triggered by an external editor's half-write will read partial JSON, in
    /// which case returning null lets the caller <b>keep the current in-memory config</b> and never
    /// swaps the good running config for the default and writes it (see <see cref="ConfigService"/>.Reload).</para>
    /// </summary>
    public static AppConfig? TryLoad()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            string json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// First-startup load (taken only on the one <see cref="ConfigService"/> initialization):
    /// use a valid config if read; on corrupt / missing file → quarantine for safekeeping, then fall
    /// back to the default and write it to disk.
    /// <para>Note the semantic difference from <see cref="TryLoad"/>: Load <b>renames + writes the
    /// default to disk</b> on corruption (correct at first startup: keep the bad file for safekeeping
    /// and give the user a clean, usable default); TryLoad <b>reads purely and touches no disk</b>
    /// (correct for hot reload: a half-written file must not trigger an overwrite).</para>
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                string json = File.ReadAllText(Path_);
                var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (cfg != null) return cfg;
                // Deserialization yielded null (empty file / literal "null"): treat as corrupt, leaving room to recover.
                QuarantineCorrupt();
            }
        }
        catch
        {
            // Corrupt JSON / read failure: rename the bad file for safekeeping first, then fall back to the default (so writing the default doesn't overwrite the recoverable original).
            QuarantineCorrupt();
        }

        var def = AppConfig.CreateDefault();
        try { Save(def); } catch { /* a failed first write is not fatal: this session uses the in-memory default */ }
        return def;
    }

    /// <summary>
    /// Rename a corrupt config.json to config.json.corrupt-yyyyMMddHHmmss (leaving room to recover);
    /// the caller then writes the default. If the rename itself fails (locked / permissions), let it
    /// go silently rather than blocking the fallback to default.
    /// </summary>
    private static void QuarantineCorrupt()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            string bak = Path_ + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(Path_, bak, overwrite: true);
        }
        catch { /* if the rename fails, let it go: worst case the next Save overwrites it */ }
    }

    /// <summary>
    /// Atomic disk write: write config.json.tmp first, then File.Move(tmp, path, overwrite:true), to
    /// avoid a half-written file being read mid-write. Failures propagate (the caller
    /// ConfigService.Save is responsible for surfacing them to the user).
    /// </summary>
    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);

        // The tmp name carries a GUID to prevent concurrent same-process writes from clobbering each other (and stale-tmp name collisions).
        string tmp = Path_ + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path_, overwrite: true); // atomic replace on the same volume
        }
        catch
        {
            // On failure, best-effort clean up the leftover tmp; the exception propagates as usual.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }
}
