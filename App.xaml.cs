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

    private MainWindow? _window;
    private TrayIcon? _tray;
    private HotkeyService? _hotkeys;
    private DispatcherQueue? _ui;
    private bool _exiting;

    /// <summary>The current App instance (non-null after OnLaunched). Lets static entry points such as <see cref="RequestExit"/> forward to instance methods.</summary>
    private static App? _current;

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

        // Crash log: write unhandled exceptions to %LOCALAPPDATA%\Reframe\crash.log.
        // XAML/WinRT-layer exceptions (0xc000027b stowed exception) would otherwise show only a module name in Event Viewer, with no stack.
        UnhandledException += (_, e) =>
        {
            LogCrash("XAML UnhandledException", e.Exception);
            // Don't set e.Handled = true: let the process crash as usual, but we've captured the stack.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            LogCrash("UnobservedTaskException", e.Exception);
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

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");
            System.IO.Directory.CreateDirectory(dir);
            string text = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ==={Environment.NewLine}" +
                          (ex?.ToString() ?? "(null exception)") + Environment.NewLine + Environment.NewLine;
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), text);
        }
        catch { /* a logging failure must not throw again */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: do this first. An existing instance is brought to the front, and this process Environment.Exits here.
        if (!SingleInstance.EnsureSingle()) return;

        // First access to Instance triggers Load. Engine always takes the latest config reference.
        _ = ConfigService.Instance;
        Engine = new Watcher(() => ConfigService.Instance.Config);
        Engine.Start();

        // Drag snap: drag a window while holding the modifier → zone overlay → drop into place. Manages its own thread/hooks internally.
        DragSnapService.Start(() => ConfigService.Instance.Config);

        // Config change (UI save / external config.json edit) → immediately rewrite the Unity resolution preset (the game is usually not running, so the write takes effect).
        ConfigService.Instance.Changed += () => Engine?.OnConfigChanged();

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
        bool startMinimized = StartupOptions.IsMinimized(Environment.GetCommandLineArgs());
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

        // 1) Hide the window immediately for instant feedback (still on the UI thread here).
        try { _window?.AppWindow.Hide(); } catch { /* ignore */ }

        // 2) Run the stop chain in the background, to avoid Engine.Stop's Wait+RestoreAll blocking the UI thread.
        var ui = _ui;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { ConfigService.Instance.Shutdown(); } catch { /* stop the hot-reload watch/debounce, to avoid callbacks during exit */ }
            try { DragSnapService.Stop(); } catch { /* stop the snap hooks first, then tear down the engine */ }
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
