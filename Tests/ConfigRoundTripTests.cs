using System.Text.Json;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Config round-trip: CreateDefault → serialize → deserialize, key fields preserved; extra fields in old JSON
/// don't blow up. Uses the source-generated ConfigJsonContext (the same path the App actually writes to disk).
/// </summary>
public class ConfigRoundTripTests
{
    private static string Serialize(AppConfig cfg)
        => JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);

    private static AppConfig Deserialize(string json)
        => JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

    [Fact(DisplayName = "Default config round-trip: profiles / rules / zone ratios / UseWorkArea all preserved")]
    public void Default_RoundTrip_KeyFieldsPreserved()
    {
        var original = AppConfig.CreateDefault();
        var json = Serialize(original);
        var back = Deserialize(json);

        // Top level
        Assert.Equal(original.Version, back.Version);
        Assert.Equal(original.PollIntervalMs, back.PollIntervalMs);
        Assert.Equal(original.EngineEnabled, back.EngineEnabled);

        // Layout count / zone count
        Assert.Equal(original.Layouts.Count, back.Layouts.Count);
        Assert.Single(back.Layouts);
        var layout = back.Layouts[0];
        Assert.Equal(2, layout.Zones.Count);

        // Zone ratios (2/3 and 1/3) preserved to double precision
        Assert.Equal(0.0, layout.Zones[0].X, 12);
        Assert.Equal(2.0 / 3, layout.Zones[0].W, 12);
        Assert.Equal(2.0 / 3, layout.Zones[1].X, 12);
        Assert.Equal(1.0 / 3, layout.Zones[1].W, 12);

        // Ref resolution
        Assert.Equal(7680, layout.RefWidth);
        Assert.Equal(2160, layout.RefHeight);

        // Profile count
        Assert.Equal(3, back.Profiles.Count);
        Assert.Equal(original.Profiles.Count, back.Profiles.Count);

        // Each profile has two rules: first Zone + UseWorkArea=true, last Fullscreen
        foreach (var prof in back.Profiles)
        {
            Assert.Equal(2, prof.Rules.Count);

            var first = prof.Rules[0];
            Assert.Equal(PlacementKind.Zone, first.Kind);
            Assert.True(first.UseWorkArea);
            Assert.Equal(7680, first.Monitor.Width);
            Assert.Equal(2160, first.Monitor.Height);

            var last = prof.Rules[1];
            Assert.Equal(PlacementKind.Fullscreen, last.Kind);
            Assert.False(last.UseWorkArea);
            Assert.Equal(0, last.Monitor.Width);
            Assert.Equal(0, last.Monitor.Height);
        }
    }

    [Fact(DisplayName = "Enums serialized as strings (UseStringEnumConverter)")]
    public void Enums_SerializedAsStrings()
    {
        var json = Serialize(AppConfig.CreateDefault());
        // Readable enum names should appear, not numbers
        Assert.Contains("\"Zone\"", json);
        Assert.Contains("\"Fullscreen\"", json);
        Assert.Contains("\"Process\"", json);
    }

    [Fact(DisplayName = "Zone and rule Id references stay consistent: the first rule's ZoneId points at the game zone in the layout")]
    public void ZoneId_References_StayConsistent()
    {
        var back = Deserialize(Serialize(AppConfig.CreateDefault()));
        var layout = back.Layouts[0];
        var prof = back.Profiles[0];
        var zoneRule = prof.Rules[0];

        Assert.Equal(layout.Id, zoneRule.LayoutId);
        Assert.Contains(layout.Zones, z => z.Id == zoneRule.ZoneId);
    }

    [Fact(DisplayName = "Unknown fields tolerated: old/new JSON with extra fields deserializes without throwing")]
    public void UnknownFields_Tolerated()
    {
        // Inject unknown fields into valid AppConfig JSON (top-level + nested)
        var json = """
        {
          "Version": 1,
          "PollIntervalMs": 1500,
          "EngineEnabled": true,
          "SomeFutureTopLevelField": 42,
          "Layouts": [
            {
              "Id": "L1",
              "Name": "测试布局",
              "RefWidth": 7680,
              "RefHeight": 2160,
              "FutureLayoutFlag": "x",
              "Zones": [
                { "Id": "Z1", "Name": "游戏区", "X": 0, "Y": 0, "W": 0.6666666666666666, "H": 1, "Unknown": true }
              ]
            }
          ],
          "Profiles": [
            {
              "Id": "P1",
              "Name": "崩铁",
              "Enabled": true,
              "MatchKind": "Process",
              "MatchValue": "StarRail.exe",
              "Borderless": true,
              "Method": "Win32",
              "DelayMs": 1000,
              "Offsets": { "Left": 0, "Top": 0, "Right": 0, "Bottom": 0 },
              "GhostField": [1, 2, 3],
              "Rules": [
                {
                  "Monitor": { "Width": 7680, "Height": 2160 },
                  "Kind": "Zone",
                  "LayoutId": "L1",
                  "ZoneId": "Z1",
                  "UseWorkArea": true,
                  "ExtraNested": { "a": 1 }
                }
              ]
            }
          ]
        }
        """;

        AppConfig? cfg = null;
        var ex = Record.Exception(() => cfg = Deserialize(json));
        Assert.Null(ex);
        Assert.NotNull(cfg);

        // Known fields still parse correctly
        Assert.Single(cfg!.Layouts);
        Assert.Equal("测试布局", cfg.Layouts[0].Name);
        Assert.Single(cfg.Profiles);
        Assert.Equal("StarRail.exe", cfg.Profiles[0].MatchValue);
        Assert.Equal(PlacementKind.Zone, cfg.Profiles[0].Rules[0].Kind);
        Assert.True(cfg.Profiles[0].Rules[0].UseWorkArea);
    }

    [Fact(DisplayName = "M3 boolean switches default round-trip: Topmost/KeepAspectRatio etc. default false preserved")]
    public void M3Switches_DefaultsRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        var back = Deserialize(Serialize(cfg));
        var p = back.Profiles[0];
        Assert.False(p.Topmost);
        Assert.False(p.KeepAspectRatio);
        Assert.False(p.PreserveClientArea);
        Assert.False(p.MuteInBackground);
        Assert.False(p.ClipCursor);
    }

    [Fact(DisplayName = "M3 boolean switches set to true round-trip preserved")]
    public void M3Switches_TrueRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[0].Topmost = true;
        cfg.Profiles[0].KeepAspectRatio = true;
        var back = Deserialize(Serialize(cfg));
        Assert.True(back.Profiles[0].Topmost);
        Assert.True(back.Profiles[0].KeepAspectRatio);
    }

    // ---- Unity resolution preset + MoveOnly ----

    [Fact(DisplayName = "ResolutionPreset defaults to null, and round-trips as null")]
    public void ResolutionPreset_DefaultsNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].ResolutionPreset);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].ResolutionPreset);
    }

    [Fact(DisplayName = "ResolutionPreset all fields round-trip preserved (Genshin 5120×2088 windowed)")]
    public void ResolutionPreset_FullRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[2].ResolutionPreset = new UnityResolutionPreset
        {
            Enabled = true,
            RegistryPath = @"Software\miHoYo\原神",
            Width = 5120,
            Height = 2088,
            Windowed = true,
        };
        var back = Deserialize(Serialize(cfg));
        var rp = back.Profiles[2].ResolutionPreset;
        Assert.NotNull(rp);
        Assert.True(rp!.Enabled);
        Assert.Equal(@"Software\miHoYo\原神", rp.RegistryPath);
        Assert.Equal(5120, rp.Width);
        Assert.Equal(2088, rp.Height);
        Assert.True(rp.Windowed);
    }

    // ---- Backdrop material ----

    [Fact(DisplayName = "Backdrop defaults to Mica, and is stored as a string")]
    public void Backdrop_DefaultMica_SerializedAsString()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Equal(BackdropKind.Mica, cfg.Backdrop);

        var json = Serialize(cfg);
        Assert.Contains("\"Mica\"", json);

        var back = Deserialize(json);
        Assert.Equal(BackdropKind.Mica, back.Backdrop);
    }

    [Theory(DisplayName = "Backdrop all three values round-trip preserved")]
    [InlineData(BackdropKind.Mica)]
    [InlineData(BackdropKind.MicaAlt)]
    [InlineData(BackdropKind.Acrylic)]
    public void Backdrop_AllKinds_RoundTrip(BackdropKind kind)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Backdrop = kind;
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(kind, back.Backdrop);
    }

    // ---- Theme (dark mode) ----

    [Fact(DisplayName = "Theme defaults to System, and is stored as a string")]
    public void Theme_DefaultSystem_SerializedAsString()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Equal(AppTheme.System, cfg.Theme);

        var json = Serialize(cfg);
        Assert.Contains("\"System\"", json);

        var back = Deserialize(json);
        Assert.Equal(AppTheme.System, back.Theme);
    }

    [Theory(DisplayName = "Theme all three values round-trip preserved")]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Theme_AllValues_RoundTrip(AppTheme theme)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Theme = theme;
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(theme, back.Theme);
    }

    // ---- ExePath (icon source) ----

    [Fact(DisplayName = "Profile.ExePath defaults to null, and round-trips as null")]
    public void ExePath_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].ExePath);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].ExePath);
    }

    [Fact(DisplayName = "Profile.ExePath set value round-trips preserved (path with spaces and backslashes)")]
    public void ExePath_FullRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[1].ExePath = @"D:\Games\Zenless Zone Zero\ZenlessZoneZero.exe";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(@"D:\Games\Zenless Zone Zero\ZenlessZoneZero.exe", back.Profiles[1].ExePath);
        // Others left unset remain null
        Assert.Null(back.Profiles[0].ExePath);
    }

    // ---- SteamGridDB API key ----

    [Fact(DisplayName = "AppConfig.SteamGridDbApiKey defaults to null, and round-trips as null")]
    public void SteamGridDbApiKey_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.SteamGridDbApiKey);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.SteamGridDbApiKey);
    }

    [Fact(DisplayName = "AppConfig.SteamGridDbApiKey set value round-trips preserved")]
    public void SteamGridDbApiKey_FullRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.SteamGridDbApiKey = "abc123DEF456";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal("abc123DEF456", back.SteamGridDbApiKey);
    }

    [Fact(DisplayName = "PlacementRule.MoveOnly defaults to false, round-trips preserved when set true")]
    public void MoveOnly_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.False(cfg.Profiles[0].Rules[0].MoveOnly); // Default
        cfg.Profiles[0].Rules[0].MoveOnly = true;
        var back = Deserialize(Serialize(cfg));
        Assert.True(back.Profiles[0].Rules[0].MoveOnly);
        Assert.False(back.Profiles[0].Rules[1].MoveOnly);
    }

    // ---- LaunchCommand (M5: one-click launch) ----

    [Fact(DisplayName = "Profile.LaunchCommand defaults to null, and round-trips as null")]
    public void LaunchCommand_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].LaunchCommand);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].LaunchCommand);
    }

    [Fact(DisplayName = "Profile.LaunchCommand set value round-trips preserved (launcher URI)")]
    public void LaunchCommand_Uri_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[2].LaunchCommand = "hoyoplay://launchgame?gameId=1";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal("hoyoplay://launchgame?gameId=1", back.Profiles[2].LaunchCommand);
        // Others left unset remain null
        Assert.Null(back.Profiles[0].LaunchCommand);
    }

    [Fact(DisplayName = "Profile.LaunchCommand set value round-trips preserved (local path with spaces and backslashes)")]
    public void LaunchCommand_LocalPath_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[1].LaunchCommand = @"D:\Games\Zenless Zone Zero\launcher.exe";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(@"D:\Games\Zenless Zone Zero\launcher.exe", back.Profiles[1].LaunchCommand);
    }

    // ---- Hotkeys (M5: hotkey-binding dictionary) ----

    [Fact(DisplayName = "AppConfig.Hotkeys defaults to an empty dictionary, round-trips empty and non-null")]
    public void Hotkeys_DefaultEmpty_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.NotNull(cfg.Hotkeys);
        Assert.Empty(cfg.Hotkeys);
        var back = Deserialize(Serialize(cfg));
        Assert.NotNull(back.Hotkeys);
        Assert.Empty(back.Hotkeys);
    }

    [Fact(DisplayName = "AppConfig.Hotkeys multiple bindings round-trip preserved (all key-value pairs kept)")]
    public void Hotkeys_Entries_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Hotkeys["ToggleBorderless"] = "Ctrl+Alt+B";
        cfg.Hotkeys["SendToZone1"] = "Win+Alt+1";
        cfg.Hotkeys["SendToZone2"] = "Win+Alt+2";
        cfg.Hotkeys["SendToZone3"] = "Win+Alt+3";

        var back = Deserialize(Serialize(cfg));

        Assert.Equal(4, back.Hotkeys.Count);
        Assert.Equal("Ctrl+Alt+B", back.Hotkeys["ToggleBorderless"]);
        Assert.Equal("Win+Alt+1", back.Hotkeys["SendToZone1"]);
        Assert.Equal("Win+Alt+2", back.Hotkeys["SendToZone2"]);
        Assert.Equal("Win+Alt+3", back.Hotkeys["SendToZone3"]);
    }

    [Fact(DisplayName = "AppConfig.Hotkeys single rebind round-trips preserved (overriding the default gesture)")]
    public void Hotkeys_Rebind_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Hotkeys["ToggleBorderless"] = "Ctrl+Shift+B";
        var back = Deserialize(Serialize(cfg));
        Assert.Single(back.Hotkeys);
        Assert.Equal("Ctrl+Shift+B", back.Hotkeys["ToggleBorderless"]);
    }

    // ---- IgnoredProcesses (user-ignore list) ----

    [Fact(DisplayName = "AppConfig.IgnoredProcesses defaults to an empty list, round-trips empty and non-null")]
    public void IgnoredProcesses_DefaultEmpty_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.NotNull(cfg.IgnoredProcesses);
        Assert.Empty(cfg.IgnoredProcesses);
        var back = Deserialize(Serialize(cfg));
        Assert.NotNull(back.IgnoredProcesses);
        Assert.Empty(back.IgnoredProcesses);
    }

    [Fact(DisplayName = "AppConfig.IgnoredProcesses multiple entries round-trip preserved (order and values kept)")]
    public void IgnoredProcesses_Entries_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.IgnoredProcesses.Add("explorer");
        cfg.IgnoredProcesses.Add("steamwebhelper");
        cfg.IgnoredProcesses.Add("discord");

        var back = Deserialize(Serialize(cfg));

        Assert.Equal(3, back.IgnoredProcesses.Count);
        Assert.Equal(new[] { "explorer", "steamwebhelper", "discord" }, back.IgnoredProcesses);
    }

    [Fact(DisplayName = "AppConfig.IgnoredProcesses with Chinese/spaced process names round-trips preserved")]
    public void IgnoredProcesses_UnicodeAndSpaces_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.IgnoredProcesses.Add("某中文进程");
        cfg.IgnoredProcesses.Add("Epic Games Launcher");
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(2, back.IgnoredProcesses.Count);
        Assert.Contains("某中文进程", back.IgnoredProcesses);
        Assert.Contains("Epic Games Launcher", back.IgnoredProcesses);
    }
}
