using Reframe.Core;

namespace Reframe.Services;

/// <summary>
/// The in-process single source of truth for config: wraps ConfigStore and adds hot reload.
/// Watches config.json for external changes (manual edits / sync outside the UI) → debounce →
/// re-Load → raise Changed. File events caused by our own Save are ignored.
/// Contract: the Config reference is swapped wholesale after a hot reload, so consumers must
/// "fetch on use, don't cache".
/// </summary>
public sealed class ConfigService
{
    private static readonly Lazy<ConfigService> _instance = new(() => new ConfigService());
    public static ConfigService Instance => _instance.Value;

    private volatile AppConfig _config;
    /// <summary>The current config. The reference swap is atomic; after a hot reload this points at a new object.</summary>
    public AppConfig Config => _config;

    /// <summary>Raised after a Save or an external file change. Any thread; the UI marshals via its own DispatcherQueue.</summary>
    public event Action? Changed;

    private readonly FileSystemWatcher _fsw;
    private readonly object _gate = new();

    // Self-write suppression: record the write time just before Save; file events near that time are treated as self-induced.
    private DateTime _selfWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromMilliseconds(800);

    // Debounce: bursts of external writes (an editor's save often fires Changed/Created several times) are coalesced into one reload.
    private const int DebounceMs = 300;
    private Timer? _debounce;
    private bool _shutdown;

    // Whether the most recent Save failed (a write exception). Queryable by the UI / diagnostics; a
    // failure is also surfaced to the user through the Watcher log channel.
    private volatile string? _lastSaveError;
    /// <summary>The reason the most recent Save failed; cleared on success. null = the last write was fine.</summary>
    public string? LastSaveError => _lastSaveError;

    // When a hot reload reads a half-written/corrupt file, report "temporarily unreadable, keeping
    // the current config" to the dashboard log only once, to avoid spamming on an editor's repeated
    // saves. Once a reload succeeds (the file recovers), reset to allow reporting again. Read/written only under _gate.
    private bool _reloadUnreadableLogged;

    private ConfigService()
    {
        _config = ConfigStore.Load(); // load on first access (Load writes the default to disk when the file is missing)

        Directory.CreateDirectory(ConfigStore.Dir);
        _fsw = new FileSystemWatcher(ConfigStore.Dir, System.IO.Path.GetFileName(ConfigStore.Path_))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _fsw.Changed += OnFileEvent;
        _fsw.Created += OnFileEvent;
        _fsw.Renamed += OnFileEvent;
    }

    /// <summary>
    /// Write to disk + raise Changed. The whole "read _config + serialize + write" runs under _gate,
    /// mutually exclusive with Reload's reference swap, eliminating the window where "the UI edits the
    /// old object while Save writes the new one" or "the reference is swapped by a hot reload mid-
    /// serialization". A write failure is not thrown (to avoid crashing the UI call site); instead it
    /// records LastSaveError and surfaces it to the user through the Watcher log.
    /// </summary>
    public void Save()
    {
        bool ok = true;
        string? err = null;
        lock (_gate)
        {
            _selfWriteUtc = DateTime.UtcNow; // mark first, to suppress the file event that follows
            try
            {
                ConfigStore.Save(_config); // serialize under the lock: mutually exclusive with Reload's reference swap
                _lastSaveError = null;
            }
            catch (Exception ex)
            {
                ok = false;
                err = ex.Message;
                _lastSaveError = ex.Message;
            }
        }

        if (!ok)
        {
            // Make the failure visible: via the engine log channel (shown on the dashboard); fall back to Debug if the engine isn't ready.
            try { App.Engine?.LogExternal($"Failed to save configuration: {err}. Changes may not have been written to disk."); }
            catch { System.Diagnostics.Debug.WriteLine($"ConfigService.Save failed: {err}"); }
        }

        Changed?.Invoke(); // in-memory _config is already up to date, so still notify consumers (even if the disk write failed, the in-session value is valid)
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_shutdown) return;
            // Something we just wrote: ignore (file events can lag, so allow a time window).
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;

            // Debounce: reset the timer; only reload for real after 300ms of quiet.
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Hot reload (fired when the debounce elapses). Semantics are <b>deliberately different</b> from
    /// the first-startup ConfigStore.Load: it uses <see cref="ConfigStore.TryLoad"/>, a pure read —
    /// <list type="bullet">
    /// <item>Read a valid config: atomically swap _config (under _gate, mutually exclusive with Save's serialization), raise Changed.</item>
    /// <item>Read null (an external editor wrote half a file / the file is corrupt / it's momentarily
    /// locked): <b>leave the old _config untouched and do not raise Changed</b>, logging one
    /// dashboard line only on first occurrence (anti-spam). Once the file recovers, the next file
    /// event naturally reloads it.</item>
    /// </list>
    /// The old implementation used ConfigStore.Load, which on a parse failure quarantines + writes the
    /// default to disk — meaning an "external half-write" overwrote the good running config with the
    /// default. This fix removes that path.
    /// </summary>
    private void Reload()
    {
        bool replaced = false;       // whether the config was actually swapped this time (decides whether to raise Changed)
        bool needLogUnreadable = false;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;

            if (_shutdown) return;
            // Re-check the self-write marker as the debounce window ends (a Save that happened during the debounce).
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;

            var fresh = ConfigStore.TryLoad();
            if (fresh is null)
            {
                // Read/parse failure: leave the current in-memory config untouched, don't raise Changed. Schedule a log only on first occurrence.
                if (!_reloadUnreadableLogged)
                {
                    _reloadUnreadableLogged = true;
                    needLogUnreadable = true;
                }
                System.Diagnostics.Debug.WriteLine("ConfigService.Reload: config.json temporarily unreadable, keeping the current config");
            }
            else
            {
                _config = fresh;                 // atomic reference swap — under _gate, mutually exclusive with Save's serialization
                _reloadUnreadableLogged = false; // once recovered, allow reporting again next time
                replaced = true;
            }
        }

        // Emit the log / notification outside the lock, to avoid calling external callbacks under _gate.
        if (needLogUnreadable)
        {
            try { App.Engine?.LogExternal("Configuration file temporarily unreadable (an editor may be writing it); keeping the current config."); }
            catch { /* engine not ready: already wrote Debug, let it go */ }
        }
        if (replaced)
            Changed?.Invoke();
    }

    /// <summary>
    /// Orderly shutdown: stop the file watcher and the debounce timer, after which Reload/Changed no
    /// longer fire. Called by the exit chain (App.ExitApp) to avoid late file-event callbacks during
    /// teardown. Idempotent.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            try { _fsw.EnableRaisingEvents = false; } catch { /* ignore */ }
            try { _fsw.Dispose(); } catch { /* ignore */ }
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
