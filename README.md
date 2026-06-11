<div align="center">

<img src="docs/logo.png" width="128" alt="Reframe logo" />

<h1>Reframe</h1>

<p><b>A Windows borderless-window manager with per-monitor adaptive layouts.</b></p>

<p>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License: MIT" /></a>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4.svg" alt=".NET 9" />
  <img src="https://img.shields.io/badge/Windows-11-0078D6.svg" alt="Windows 11" />
  <img src="https://img.shields.io/badge/WinUI-3-2D7D9A.svg" alt="WinUI 3" />
  <a href="https://github.com/shuiandy/Reframe/releases"><img src="https://img.shields.io/github/v/release/shuiandy/Reframe" alt="GitHub release" /></a>
</p>

<p><b>English</b> · <a href="README.zh-CN.md">简体中文</a></p>

<img src="docs/screenshots/dashboard.png" alt="Reframe dashboard" />

</div>

Reframe strips the title bar and border off a window and snaps it to a position you define — but the position is a *named layout* stored as ratios, resolved against whichever monitor the window currently lives on. Move the same game from a 57" ultrawide to a streamed virtual display and it re-fits automatically, no manual editing.

## Why Reframe

Most borderless tools store a fixed pixel position per game. Reframe is built around a different idea — **layouts are first-class, and positions are relative**:

- **Reusable layouts.** A *layout* is a named set of zones; a profile just references a `layout + zone`. Edit the layout once and every game that uses it follows — no per-game rework.
- **Resolution-independent zones.** Zones are stored as `0..1` ratios and resolved against whichever monitor the window is currently on. ⅔ of a 7680-wide screen is 5120 px; move the window to a smaller display — or a streamed virtual one — and it recomputes itself, no manual editing.
- **Per-monitor rules.** Each profile carries an ordered rule list, *first matching monitor wins*: `7680×2160 → snap to the game zone`, `any other screen → fullscreen`. The same game can behave differently on your desktop and on a stream.
- **Live, not restart-bound.** UI edits apply immediately, and external edits to `config.json` are watched and hot-reloaded.
- **Event-driven.** WinEvent hooks react the moment a window appears, renames itself, or a game moves itself back — with a low-frequency poll as a safety net.
- **Always reversible.** A window's original style and position are snapshotted before any change and restored when you disable a profile or stop the engine.

## Features

#### 🪟 Windows & profiles
- **Window panel** (two columns): the left column lists running windows live — with icons, search, and a "show filtered" toggle — while the right column is your profile list. Create a profile from any window in one click.
- **Ignore list**: right-click a window to ignore its process (reversible, per-user); shell/system windows are filtered out automatically.

#### 🧩 Layouts & snapping
- **FancyZones-style layout editor**: carve zones on a canvas, with preset templates (split halves, thirds, left-⅔ + right-⅓, 16:9 centered, 21:9 left) and both pixel and ratio readouts.
- **Drag-snap**: hold Shift while dragging any window — zones highlight, release to snap into place.
- **Screenshot-style region picker**: drag out a rectangle on a full-screen overlay with edge/ratio snapping.

#### 🎮 Game launch
- **Launch command + Unity resolution presets**: a profile can carry a launch command (a launcher URI such as `hoyoplay://`, or an executable path). For Unity games whose render resolution is pinned in the registry, Reframe writes the resolution preset before launch, then positions the window without resizing it.
- **Icon system** with an optional [SteamGridDB](https://www.steamgriddb.com/) fallback for games whose icon can't be read locally (anti-cheat-protected processes).

#### ⚙️ System integration
- **Configurable global hotkeys**: toggle borderless on the foreground window, or send it into layout zones 1/2/3. All rebindable.
- **Tray-resident, single-instance, optional start-on-login** (via a scheduled task, so it runs elevated without a UAC prompt).
- **Mica / Mica Alt / Acrylic backdrops** and a system / light / dark theme switch.
- **Config import / export** (validated on import) and a **crash log** written to `%LOCALAPPDATA%\Reframe\crash.log`.

## Install

1. Download the latest `Reframe-v<version>-win-x64.zip` from [Releases](https://github.com/shuiandy/Reframe/releases).
2. Unzip anywhere and run `Reframe.exe`.

**Requirements**

- **Windows 10 1809+ / Windows 11**, x64.
- **[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)** — the release is framework-dependent for .NET. The Windows App SDK is bundled, so you do *not* need to install it separately.
- **Administrator** — Reframe self-elevates via its manifest.

<details>
<summary><b>Why administrator?</b></summary>

<br>

Anti-cheat games run their windows under elevated/protected integrity. Windows' [UIPI](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-messages-and-message-queues#message-integrity) blocks a lower-integrity process from manipulating a higher-integrity window, so Reframe has to run elevated to reposition those windows at all.

To be explicit about what that elevation is used for: **Reframe does not inject code and does not read or write game memory.** It only calls the standard Win32 window APIs — `SetWindowLongPtr` to drop the caption/frame styles and `SetWindowPos` to move and size the window. Nothing more. (See the [FAQ](#faq) for more.)

</details>

## Quick start

1. **Create a profile** — open the *Profiles* page, find your game in the left column (run it once so it shows up), and click to create a profile. Or use *New* and fill in the process name.
2. **Draw a layout** — on the *Layouts* page, pick a preset or carve your own zones on the canvas, then save it as a named layout.
3. **Hand it over** — in the profile, add a rule: pick a monitor (by resolution, or "any") and point it at a layout zone (or fullscreen). The top rule that matches the window's current monitor wins. Make sure the engine is on (toggle on the *Dashboard*).

**Default hotkeys**

| Action | Default |
|---|---|
| Toggle borderless on the foreground window | `Ctrl + Alt + B` |
| Send foreground window to zone 1 / 2 / 3 | `Ctrl + Alt + 1` / `2` / `3` |

All hotkeys are rebindable on the *Settings* page. Zone hotkeys use the zones of your first layout, resolved against the foreground window's current monitor.

## Build from source

Requires the **.NET 9 SDK** and the Windows App SDK workload.

```powershell
# Debug build
dotnet build -p:Platform=x64

# Run the test suite (independent project, links Core source files)
dotnet test Tests\Reframe.Core.Tests.csproj

# Produce a Release zip -> dist\Reframe-v<version>-win-x64.zip
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

`publish.ps1` produces a build that is **framework-dependent for .NET** (target machine needs the .NET 9 Desktop Runtime) but **self-contained for the Windows App SDK** (target machine does not). It publishes into a dedicated `publish_out\` folder so it never disturbs `bin\Debug` or a running instance.

## Architecture

Reframe is split into a UI-free **Core** and a WinUI 3 shell on top of it:

- **`Core/`** — the engine. Detection (`WinEventHook` + a safety-net poll), matching (process name / title / regex), resolution (`PlacementResolver`, a pure function: monitor rect → rule → pixel rect, fully unit-tested), application (`WindowOps`: style/position changes with snapshot-and-restore), and a thrash policy so the engine doesn't fight a game that keeps moving its own window. Zero UI dependencies, so the placement math is unit-testable in isolation.
- **`Services/`** — config (`System.Text.Json` source-generated, hot-reloaded), monitors, hotkeys, tray, icon cache, game launcher, start-on-login.
- **`UI/`** — the WinUI 3 pages (Dashboard, Profiles, Layouts, Settings) and custom canvas controls. Hand-written `INotifyPropertyChanged`, no MVVM framework.

The full design — data model, engine pipeline, and per-feature status — is in [DESIGN.md](DESIGN.md).

## Configuration

```
%LOCALAPPDATA%\Reframe\config.json
```

Written on first run if it doesn't exist. JSON is read/written via `System.Text.Json` source generation and carries a `Version` field for future migration. You can edit the file by hand — the app watches it and hot-reloads (atomic writes and partial-write tolerance mean a half-written file won't clobber your running config). The *Settings* page also has import / export.

## FAQ

**Is this safe to use with anti-cheat games?**
Reframe only changes window *styles* and *position* through documented Win32 APIs (`SetWindowLongPtr`, `SetWindowPos`). It never injects code, never reads or writes another process's memory, and never touches game files. That said, no third-party tool can offer a guarantee about how a given anti-cheat will react — use your judgment.

**Why is it unpackaged (not an MSIX from the Store)?**
The app needs a `requireAdministrator` manifest to manipulate elevated game windows (see [Why administrator?](#why-administrator)). MSIX packaging is incompatible with `requireAdministrator`, so Reframe ships as a plain unpackaged executable instead.

**Known limitations**
- Games in *exclusive* fullscreen have no manipulable bordered window — launch them in windowed / borderless mode. (The miHoYo titles in the default config are already borderless/windowed.)
- UWP and protected-process windows can't be modified; Reframe reports a lack of permission rather than failing silently.
- Some advanced options aren't implemented yet (preserve client area, span-all-monitors, hide taskbar, remove menu, etc.). See the status column in [DESIGN.md](DESIGN.md) §4.

## Contributing

Issues and pull requests are welcome — bug reports, feature ideas, and questions all help.

**Translations especially.** The UI is fully localized through `.resw` resources (English and Simplified Chinese today). Adding a language is mostly translating one folder of resource files — see [docs/dev/I18N.md](docs/dev/I18N.md) for the steps. If you'd like to help, open an issue.

When sending a PR, please keep the **Core** layer UI-free (the placement math lives there and is unit-tested) and run `dotnet test Tests\Reframe.Core.Tests.csproj` before you push.

## Acknowledgments

- [**PowerToys FancyZones**](https://github.com/microsoft/PowerToys) — inspiration for the zone-based layout editor and drag-snap interaction.

## License

[MIT](LICENSE) © 2026 shuiandy
