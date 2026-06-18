using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Reframe.Core;
using Reframe.Services;

namespace Reframe;

public partial class App : Application
{
    /// <summary>Forwards to ConfigService's current config (the reference is swapped after a hot reload; others read it, fetch-on-use).</summary>
    public static AppConfig Config => ConfigService.Instance.Config;

    public static Watcher Engine { get; private set; } = null!;

    /// <summary>The main window (non-null after OnLaunched). Available if a page needs to trigger directly; the main path for backdrop etc. goes through ConfigService.Changed.</summary>
    public static MainWindow? Main { get; private set; }

    /// <summary>The global hotkey service (non-null after OnLaunched). The Settings page queries the post-"Apply" registration state from it.</summary>
    public static HotkeyService? Hotkeys { get; private set; }

    /// <summary>The window-position persistence engine (non-null after OnLaunched).</summary>
    public static PersistenceEngine? Persistence { get; private set; }

    private MainWindow? _window;
    private TrayIcon? _tray;
    private HotkeyService? _hotkeys;
    private PersistenceEngine? _persistence;
    private DispatcherQueue? _ui;
    private bool _exiting;

    /// <summary>The current App instance (non-null after OnLaunched). Lets static entry points such as <see cref="RequestExit"/> forward to instance methods.</summary>
    private static App? _current;

    /// <summary>Informational version (csproj &lt;InformationalVersion&gt;, e.g. "1.2.0"), read from the
    /// entry assembly's attribute; falls back to the file version then "?" so the session marker never throws.</summary>
    private static string AppVersion
    {
        get
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(App).Assembly;
                var info = System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                return asm.GetName().Version?.ToString() ?? "?";
            }
            catch { return "?"; }
        }
    }

    /// <summary>
    /// Programmatically trigger a normal exit (equivalent to the tray "Exit"): restore all managed
    /// windows + clear the tray + Application.Exit. For scenarios like "restart immediately after a
    /// language change". Internally marshals back to the UI thread to run the existing <c>ExitApp</c>
    /// chain; idempotent.
    /// </summary>
    public static void RequestExit()
    {
        var app = _current;
        if (app is null) return;
        var ui = app._ui;
        if (ui is null || !ui.TryEnqueue(app.ExitApp)) app.ExitApp();
    }

    public App()
    {
        _current = this;

        // Display-language override: must be set before any XAML resource resolution (InitializeComponent
        // loads App.xaml). Unpackaged, MRT Core resolves resources by the "system display language" by
        // default; PrimaryLanguageOverride (the WinAppSDK Microsoft.Windows.Globalization one, not the UWP
        // namesake — the latter throws when unpackaged) overrides that choice.
        // Config.Language="system" (default) → leave unset (follow the system); "zh-CN"/"en-US" → force that language.
        // Set once, early at startup; switching x:Uid at runtime is unreliable, so a language change in
        // SettingsPage requires a restart (see that page).
        ApplyLanguageOverride();

        InitializeComponent();

        // Crash log: write unhandled exceptions to %LOCALAPPDATA%\Reframe\crash.log (shared sink in
        // Services.CrashLog). XAML/WinRT-layer exceptions (0xc000027b stowed exception) would otherwise
        // show only a module name in Event Viewer, with no stack.
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("XAML UnhandledException", e.Exception);
            // Don't set e.Handled = true: let the process crash as usual, but we've captured the stack.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write("UnobservedTaskException", e.Exception);

        // Manually-created background threads (the tray / hotkey message pumps, the WinEvent / DragSnap
        // hook pumps, the watcher's poll loop + debounce timers) don't surface through the three handlers
        // above — a raw-thread throw fast-fails the process before any managed handler logs it. Services
        // classes call CrashLog directly; Core classes can't reference Services (the Tests project links
        // Core sources standalone), so they expose a static OnThreadError callback we wire here. Done in
        // the ctor so it's set before OnLaunched starts any of those threads.
        Reframe.Core.WinEventHook.OnThreadError = CrashLog.Write;
        Reframe.Core.DragSnapService.OnThreadError = CrashLog.Write;
        Reframe.Core.Watcher.OnThreadError = CrashLog.Write;
        Reframe.Core.PersistenceEngine.OnThreadError = CrashLog.Write;
        Reframe.Core.DisplayChangeListener.OnThreadError = CrashLog.Write;

        // Clean-shutdown / vanish discriminator: a ProcessExit marker means the CLR ran a normal exit
        // path (tray Exit, restart, or host teardown). If the process disappears and crash.log shows
        // neither a crash entry nor this marker, it was killed externally or fast-failed in native code —
        // which narrows the investigation. ProcessExit handlers get a short, best-effort budget; keep it tiny.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashLog.Note("ProcessExit (clean shutdown)");

        // Native catch-all: register WER LocalDumps so even a native access violation that bypasses the
        // managed layer leaves a .dmp. Best-effort; failures (no registry permission) are swallowed.
        RegisterWerLocalDumps();
    }

    /// <summary>
    /// Set <c>ApplicationLanguages.PrimaryLanguageOverride</c> from Config.Language. Called at the very
    /// start of App construction (before any XAML loads). Uses <see cref="ConfigStore.TryLoad"/>, a pure
    /// disk read — it doesn't trigger the ConfigService singleton's file-watch/debounce side effects, and
    /// doesn't write the default when the file is missing (on first run, an unreadable config follows the
    /// system, which is correct). "system" or unreadable → no override (follow the system display
    /// language). Any exception is swallowed: a localization failure must not block startup.
    /// </summary>
    private static void ApplyLanguageOverride()
    {
        try
        {
            string? lang = ConfigStore.TryLoad()?.Language;
            if (string.IsNullOrWhiteSpace(lang) ||
                string.Equals(lang, "system", StringComparison.OrdinalIgnoreCase))
                return; // follow the system: no override

            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
        }
        catch { /* override failed: fall back to the system language, don't block startup */ }
    }

    /// <summary>
    /// Best-effort self-registration of WER LocalDumps for this exe, so a native crash that bypasses the
    /// managed exception handlers (e.g. an access violation in a P/Invoke'd DLL) still drops a full
    /// minidump. Writes <c>HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Reframe.exe</c>
    /// with DumpType=2 (full), DumpFolder=%LOCALAPPDATA%\Reframe\dumps, DumpCount=5. HKCU needs no elevation,
    /// but any failure is swallowed — a diagnostics nicety must never block startup. Idempotent (overwrites).
    /// </summary>
    private static void RegisterWerLocalDumps()
    {
        try
        {
            string exe = System.IO.Path.GetFileName(Environment.ProcessPath ?? "Reframe.exe");
            if (string.IsNullOrEmpty(exe)) exe = "Reframe.exe";

            string dumpFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe", "dumps");

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + exe);
            if (key is null) return;
            key.SetValue("DumpFolder", dumpFolder, Microsoft.Win32.RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 2, Microsoft.Win32.RegistryValueKind.DWord);   // 2 = full dump
            key.SetValue("DumpCount", 5, Microsoft.Win32.RegistryValueKind.DWord);  // keep the most recent 5
        }
        catch { /* no permission / policy-locked: skip silently */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: do this first. An existing instance is brought to the front, and this process Environment.Exits here.
        if (!SingleInstance.EnsureSingle()) return;

        // Session marker: one line that brackets each run in crash.log, so the log reads as a sequence of
        // sessions (and a later "ProcessExit" line confirms a clean close vs. a vanish). Written right after
        // the single-instance gate so a secondary instance that exits early doesn't emit a misleading line.
        bool startMinimized = StartupOptions.IsMinimized(Environment.GetCommandLineArgs());
        CrashLog.Note($"Started v{AppVersion} minimized={startMinimized}");

        // First access to Instance triggers Load. Engine always takes the latest config reference.
        _ = ConfigService.Instance;
        Engine = new Watcher(() => ConfigService.Instance.Config);
        Engine.Start();

        // Drag snap: drag a window while holding the modifier → zone overlay → drop into place. Manages its own thread/hooks internally.
        DragSnapService.Start(() => ConfigService.Instance.Config);

        // Config change (UI save / external config.json edit) → immediately rewrite the Unity resolution preset (the game is usually not running, so the write takes effect).
        ConfigService.Instance.Changed += () => Engine?.OnConfigChanged();

        // Window-position persistence: remember/restore ordinary window layout across display-topology changes
        // (resolution / monitor add-remove / streaming VDD return). Started after the engine so its
        // getEngineOwned/isEngineBusy callbacks see a live engine; it only polls the engine (no event
        // subscription), so ordering relative to the engine's first tick is not load-bearing.
        _persistence = new PersistenceEngine(
            getMonitors: MonitorService.GetMonitors,
            getEngineOwned: () => new HashSet<IntPtr>(Engine.GetTakenWindows().Select(w => w.Handle)),
            isEngineBusy: () => Engine.IsSystemMutationActive,
            isEnabled: () => ConfigService.Instance.Config.WindowPersistenceEnabled);
        Persistence = _persistence;
        _persistence.Log += m => Engine.LogExternal(m); // surface restore activity in the dashboard log
        _persistence.Start();

        _window = new MainWindow();
        Main = _window;
        _ui = _window.DispatcherQueue;

        // Clicking X doesn't exit: cancel the close, hide to the tray, and the engine keeps running. Exit is only via the tray menu.
        _window.AppWindow.Closing += OnAppWindowClosing;

        // Start-on-login (the scheduled task passes --minimized) → start silently to the tray: don't show
        // the main window. A manual launch (no flag) shows it as usual. Parsed from the real process args
        // because LaunchActivatedEventArgs.Arguments is unreliable on unpackaged WinUI 3.
        // Reliable silent path (verified empirically): Activate() to let the framework build/render the
        // content tree and wire up the presenter, then immediately AppWindow.Hide(). A window that is
        // never Activated can fail to render when shown later; activating first then hiding gives a normal
        // lifecycle and a later ShowMainWindow (tray "Open") reliably brings it up interactive. The brief
        // activate→hide happens within a single message-loop turn, so there's no visible window flash.
        // (startMinimized was already parsed above for the session marker.)
        _window.Activate();
        if (startMinimized)
            _window.AppWindow.Hide();

        // Central global hotkeys (with their own message-window thread): borderless/restore, send window to a zone. Auto re-register on config change.
        _hotkeys = new HotkeyService();
        Hotkeys = _hotkeys;
        _hotkeys.Start(_ui!, () => ConfigService.Instance.Config);

        // Tray stays resident. All callbacks marshal back to the UI thread.
        _tray = new TrayIcon
        {
            OnOpen = () => _ui!.TryEnqueue(ShowMainWindow),
            OnToggleEngine = on => _ui!.TryEnqueue(() => SetEngineEnabled(on)),
            OnExit = () => _ui!.TryEnqueue(ExitApp),
            EngineEnabledProvider = () => ConfigService.Instance.Config.EngineEnabled,
        };
        _tray.Start(tooltip: "Reframe");

        // Reconcile an existing start-on-login task's --minimized flag with the user's current preference
        // (Config.StartMinimizedOnLogin), so autostart picks up the configured behaviour without re-toggling
        // (e.g. an older task created before the flag existed, or after the user flips the option). Runs
        // schtasks, so do it off the UI thread; it's a no-op when autostart is disabled or the task already
        // matches, and never throws.
        bool startMinimizedPref = ConfigService.Instance.Config.StartMinimizedOnLogin;
        System.Threading.Tasks.Task.Run(() => StartupTaskService.MigrateIfNeeded(startMinimizedPref));
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_exiting) return;       // let it through on a real exit
        e.Cancel = true;            // intercept the close
        sender.Hide();              // hide to the tray
    }

    /// <summary>Tray left click / menu "Open": show and activate the main window.</summary>
    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        _window.Activate();
        WindowActivation.BringToFront(_window);
    }

    /// <summary>Toggle the engine-enabled config flag and write to disk. Watcher.SafeTick applies it immediately.</summary>
    private void SetEngineEnabled(bool on)
    {
        var cfg = ConfigService.Instance.Config;
        if (cfg.EngineEnabled == on) return;
        cfg.EngineEnabled = on;
        ConfigService.Instance.Save();
    }

    /// <summary>
    /// The real exit: restore all managed windows + remove the tray + exit. Triggered only by the tray
    /// "Exit". Must be entered on the UI thread.
    ///
    /// The exit chain no longer runs synchronously on the UI thread (the old implementation had
    /// Engine.Stop's Wait(2000)+RestoreAll, freezing for up to several seconds in the worst case):
    ///   1) Hide the main window first → visually "exited" immediately.
    ///   2) Run the stop chain (DragSnap/Hotkey/Engine/ConfigService) on a background thread (it may
    ///      block for several seconds without stalling the UI).
    ///   3) When done, marshal back to the UI thread for tray Dispose (to keep its thread affinity) +
    ///      Application.Exit (which must run on the UI thread).
    /// </summary>
    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        // Exit marker: a deliberate exit (tray "Exit", or RequestExit for a language-change restart). Paired
        // with the later AppDomain.ProcessExit "clean shutdown" line, this tells a clean teardown apart from a
        // crash or external kill when reading crash.log after the fact.
        CrashLog.Note("Exit via tray/RequestExit");

        // 1) Hide the window immediately for instant feedback (still on the UI thread here).
        try { _window?.AppWindow.Hide(); } catch { /* ignore */ }

        // 2) Run the stop chain in the background, to avoid Engine.Stop's Wait+RestoreAll blocking the UI thread.
        var ui = _ui;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { ConfigService.Instance.Shutdown(); } catch { /* stop the hot-reload watch/debounce, to avoid callbacks during exit */ }
            try { DragSnapService.Stop(); } catch { /* stop the snap hooks first, then tear down the engine */ }
            try { _persistence?.Stop(); } catch { /* stop the persistence worker/listener before the engine it polls */ }
            try { _hotkeys?.Stop(); } catch { /* unregister all hotkeys */ }
            try { Engine?.Stop(restoreWindows: true); } catch { /* best-effort restore */ }

            // 3) Both tray Dispose and Application.Exit go back to the UI thread (the tray's thread affinity, Exit's thread requirement).
            void Finish()
            {
                try { _tray?.Dispose(); } catch { /* ignore */ } // Dispose on the UI thread; doesn't self-join the tray thread
                try { Exit(); } catch { /* ignore */ }           // Application.Exit
            }
            if (ui is null || !ui.TryEnqueue(Finish)) Finish(); // if the queue is unavailable, fall back in place (best-effort exit)
        });
    }
}
