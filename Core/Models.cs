using System.Text.Json.Serialization;

namespace Reframe.Core;

/// <summary>How a window is matched: process name / window title (contains) / title regex.</summary>
public enum MatchKind { Process, Title, TitleRegex }

/// <summary>Borderless implementation. GpuScaling is reserved for M4 (WGC capture + overlay rendering).</summary>
public enum BorderlessMethod { Win32, GpuScaling }

/// <summary>Main-window backdrop material. Mica/MicaAlt = mica and its variant, Acrylic = desktop acrylic (frosted glass).</summary>
public enum BackdropKind { Mica, MicaAlt, Acrylic }

/// <summary>Application theme. System = follow the OS setting.</summary>
public enum AppTheme { System, Light, Dark }

/// <summary>What to do to a window's geometry once a rule matches.</summary>
public enum PlacementKind
{
    None,        // Strip the border only; leave geometry alone
    Fullscreen,  // Fill the current monitor
    Zone,        // Apply a specific zone of a specific layout
    CustomRect   // Absolute rect (relative to the current monitor's top-left, physical pixels)
}

/// <summary>A physical-pixel rect (relative to a monitor's top-left).</summary>
public sealed class RectPx
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>Per-edge fine-tuning of the final rect (mirrors Borderless Gaming's window offsets).</summary>
public sealed class Offsets
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

/// <summary>Identify a monitor by resolution. 0 = any. Intuitive and stable across devices (\\.\DISPLAYn drifts).</summary>
public sealed class MonitorFilter
{
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Matches(int w, int h)
        => (Width == 0 || Width == w) && (Height == 0 || Height == h);
}

/// <summary>A zone within a layout. Coordinates are 0..1 ratios, relative to "the monitor the window is currently on".</summary>
public sealed class Zone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;
}

/// <summary>A named layout = a set of zones. A first-class citizen: reused by multiple profiles, edit once and all follow.</summary>
public sealed class Layout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名布局";

    /// <summary>Reference resolution used by the editor, only for ratio↔pixel display conversion; not used in runtime resolution.</summary>
    public int RefWidth { get; set; } = 7680;
    public int RefHeight { get; set; } = 2160;

    public List<Zone> Zones { get; set; } = new();
}

/// <summary>Placement rule: when the window's monitor matches the Monitor filter, perform Kind.</summary>
public sealed class PlacementRule
{
    public MonitorFilter Monitor { get; set; } = new();
    public PlacementKind Kind { get; set; } = PlacementKind.Fullscreen;

    // Kind == Zone
    public string? LayoutId { get; set; }
    public string? ZoneId { get; set; }

    // Kind == CustomRect
    public RectPx? CustomRect { get; set; }

    /// <summary>true = base on the work area (rcWork, avoiding the taskbar); false = the whole monitor (rcMonitor).</summary>
    public bool UseWorkArea { get; set; }

    /// <summary>
    /// Move only: place the window's top-left at the target rect's top-left, keeping the window's current
    /// size unchanged (no resize). For Unity games with "render resolution pinned in the registry" (see
    /// <see cref="UnityResolutionPreset"/>): a resize would just scale (stretch) the whole frame, so here we
    /// only move it. When it conflicts with KeepAspectRatio, MoveOnly wins.
    /// </summary>
    public bool MoveOnly { get; set; }
}

/// <summary>Unity game startup-resolution preset: before launch, write the Screenmanager registry values to the target (for games like Genshin that "pin render resolution in the registry").</summary>
public sealed class UnityResolutionPreset
{
    public bool Enabled { get; set; }
    public string RegistryPath { get; set; } = "";  // e.g. Software\miHoYo\原神 (path relative to HKCU)
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Windowed { get; set; } = true;       // Is Fullscreen mode = 0
}

/// <summary>The complete configuration for one game/app.</summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名";
    public bool Enabled { get; set; } = true;

    public MatchKind MatchKind { get; set; } = MatchKind.Process;
    public string MatchValue { get; set; } = "";

    /// <summary>
    /// Full path to the executable (nullable). Mainly used as an icon source: anti-cheat-protected games
    /// (Zenless Zone Zero / Genshin etc.) expose no readable MainModule, so once this path is set IconCache
    /// can extract the icon straight from the exe. May also be used to launch in the future.
    /// </summary>
    public string? ExePath { get; set; }

    public bool Borderless { get; set; } = true;
    public BorderlessMethod Method { get; set; } = BorderlessMethod.Win32;

    /// <summary>How long to wait after detecting a window before acting on it (windows get rebuilt during a game's startup).</summary>
    public int DelayMs { get; set; } = 1000;

    public Offsets Offsets { get; set; } = new();

    /// <summary>Top-down, the first rule whose Monitor matches wins. Recommended to put an "any monitor" rule last.</summary>
    public List<PlacementRule> Rules { get; set; } = new();

    // ---- M3 reserved switches (engine support added incrementally) ----
    public bool Topmost { get; set; }
    public bool KeepAspectRatio { get; set; }
    public bool PreserveClientArea { get; set; }
    public bool MuteInBackground { get; set; }
    public bool ClipCursor { get; set; }

    /// <summary>
    /// Unity startup-resolution preset (nullable, none by default). When enabled, the engine writes the
    /// Screenmanager registry values to the target resolution while the game is not running, so the game
    /// renders at the target resolution; combine with a MoveOnly rule to position without scaling.
    /// </summary>
    public UnityResolutionPreset? ResolutionPreset { get; set; }

    /// <summary>Launch command (nullable). Empty falls back to ExePath; supports launcher URIs (e.g. hoyoplay://) or any executable path.</summary>
    public string? LaunchCommand { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 1500;
    public bool EngineEnabled { get; set; } = true;

    /// <summary>When dragging a window with the modifier (Shift) held, show the zone overlay and snap to a zone on release (FancyZones-style).</summary>
    public bool DragSnapEnabled { get; set; } = true;

    /// <summary>
    /// Whether the logon autostart should start silently minimized to the tray. Only affects the
    /// <c>--minimized</c> argument baked into the start-on-login scheduled task: true (default) adds the
    /// flag so a logon launch goes straight to the tray; false omits it so the logon launch shows the
    /// main window. Has no effect on manual launches (a double-click passes no args and always shows the
    /// window) and is ignored unless start-on-login is enabled.
    /// </summary>
    public bool StartMinimizedOnLogin { get; set; } = true;

    /// <summary>Main-window backdrop material. Takes effect immediately (via ConfigService.Changed → MainWindow.ApplyBackdrop).</summary>
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>Application theme (dark mode). Takes effect immediately (via ConfigService.Changed → MainWindow.ApplyTheme).</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// UI display language. A BCP-47 tag or "system". "system" (default) = follow the Windows display
    /// language; "zh-CN" / "en-US" = force that language (via ApplicationLanguages.PrimaryLanguageOverride,
    /// set early in App startup). Changing it requires an app restart (WinUI does not hot-swap x:Uid
    /// reliably). Core does not consume this field — only the UI layer (App/SettingsPage) reads/writes it.
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// SteamGridDB API key (nullable). Only when set does the "online icon" fallback kick in: when all local
    /// sources fail, fetch an icon online by game name. Free signup:
    /// https://www.steamgriddb.com/profile/preferences/api . Empty = the feature is silently disabled.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    /// <summary>
    /// Hotkey bindings: action Id → gesture string (e.g. "Ctrl+Alt+B"). Missing entries are filled with
    /// defaults by HotkeyService.
    /// Action Ids: ToggleBorderless / SendToZone1 / SendToZone2 / SendToZone3
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    public List<Layout> Layouts { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>
    /// User-defined ignored process names (lowercase, without .exe). Matches are hidden from the left
    /// column's normal "running windows" list (reversible — un-ignore in the UI). Distinct from the
    /// hard-coded system-shell blacklist (<see cref="WindowScanner.IsBlacklistedProcess"/>, irreversible).
    /// Mirrors Borderless Gaming's winignore.
    /// </summary>
    public List<string> IgnoredProcesses { get; set; } = new();

    /// <summary>First-run default config: a 57″ game-left/secondary-right layout + three miHoYo games (apply the layout locally, fill the screen elsewhere).</summary>
    public static AppConfig CreateDefault()
    {
        var layout = new Layout
        {
            Name = "57寸·左游戏右副屏",
            RefWidth = 7680,
            RefHeight = 2160,
            Zones =
            {
                new Zone { Name = "游戏区", X = 0,       Y = 0, W = 2.0 / 3, H = 1 }, // 5120, flush left
                new Zone { Name = "副屏区", X = 2.0 / 3, Y = 0, W = 1.0 / 3, H = 1 }, // 2560, on the right
            }
        };
        var gameZone = layout.Zones[0];

        Profile Game(string name, MatchKind kind, string val) => new()
        {
            Name = name,
            MatchKind = kind,
            MatchValue = val,
            Rules =
            {
                new PlacementRule   // Local 57″: apply the layout's game zone (avoiding the taskbar)
                {
                    Monitor = new MonitorFilter { Width = 7680, Height = 2160 },
                    Kind = PlacementKind.Zone,
                    LayoutId = layout.Id,
                    ZoneId = gameZone.Id,
                    UseWorkArea = true
                },
                new PlacementRule   // Any other monitor (VDD streaming): fill the screen
                {
                    Monitor = new MonitorFilter(),
                    Kind = PlacementKind.Fullscreen
                }
            }
        };

        return new AppConfig
        {
            Layouts = { layout },
            Profiles =
            {
                Game("崩坏：星穹铁道", MatchKind.Process, "StarRail.exe"),
                Game("绝区零",        MatchKind.Process, "ZenlessZoneZero.exe"),
                Game("原神",          MatchKind.Process, "YuanShen.exe"),
            }
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
public partial class ConfigJsonContext : JsonSerializerContext { }
