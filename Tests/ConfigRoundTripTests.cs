using System.Text.Json;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// 配置往返:CreateDefault → 序列化 → 反序列化,关键字段一致;老 JSON 多余字段不炸。
/// 用源生成 ConfigJsonContext(与 App 实际写盘路径一致)。
/// </summary>
public class ConfigRoundTripTests
{
    private static string Serialize(AppConfig cfg)
        => JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);

    private static AppConfig Deserialize(string json)
        => JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

    [Fact(DisplayName = "默认配置往返:profiles / rules / zone 比例 / UseWorkArea 全部保真")]
    public void Default_RoundTrip_KeyFieldsPreserved()
    {
        var original = AppConfig.CreateDefault();
        var json = Serialize(original);
        var back = Deserialize(json);

        // 顶层
        Assert.Equal(original.Version, back.Version);
        Assert.Equal(original.PollIntervalMs, back.PollIntervalMs);
        Assert.Equal(original.EngineEnabled, back.EngineEnabled);

        // 布局数 / zone 数
        Assert.Equal(original.Layouts.Count, back.Layouts.Count);
        Assert.Single(back.Layouts);
        var layout = back.Layouts[0];
        Assert.Equal(2, layout.Zones.Count);

        // zone 比例(2/3 与 1/3)保真到 double
        Assert.Equal(0.0, layout.Zones[0].X, 12);
        Assert.Equal(2.0 / 3, layout.Zones[0].W, 12);
        Assert.Equal(2.0 / 3, layout.Zones[1].X, 12);
        Assert.Equal(1.0 / 3, layout.Zones[1].W, 12);

        // Ref 分辨率
        Assert.Equal(7680, layout.RefWidth);
        Assert.Equal(2160, layout.RefHeight);

        // profiles 数
        Assert.Equal(3, back.Profiles.Count);
        Assert.Equal(original.Profiles.Count, back.Profiles.Count);

        // 每个 profile 两条规则,首条 Zone + UseWorkArea=true,末条 Fullscreen
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

    [Fact(DisplayName = "枚举以字符串形式存盘(UseStringEnumConverter)")]
    public void Enums_SerializedAsStrings()
    {
        var json = Serialize(AppConfig.CreateDefault());
        // 应出现可读枚举名,而非数字
        Assert.Contains("\"Zone\"", json);
        Assert.Contains("\"Fullscreen\"", json);
        Assert.Contains("\"Process\"", json);
    }

    [Fact(DisplayName = "Zone 与规则 Id 引用一致:首条规则 ZoneId 指向布局内的游戏区")]
    public void ZoneId_References_StayConsistent()
    {
        var back = Deserialize(Serialize(AppConfig.CreateDefault()));
        var layout = back.Layouts[0];
        var prof = back.Profiles[0];
        var zoneRule = prof.Rules[0];

        Assert.Equal(layout.Id, zoneRule.LayoutId);
        Assert.Contains(layout.Zones, z => z.Id == zoneRule.ZoneId);
    }

    [Fact(DisplayName = "未知字段容忍:老/新 JSON 含多余字段反序列化不抛")]
    public void UnknownFields_Tolerated()
    {
        // 在合法 AppConfig JSON 里塞入未知字段(顶层 + 嵌套)
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

        // 已知字段仍正确解析
        Assert.Single(cfg!.Layouts);
        Assert.Equal("测试布局", cfg.Layouts[0].Name);
        Assert.Single(cfg.Profiles);
        Assert.Equal("StarRail.exe", cfg.Profiles[0].MatchValue);
        Assert.Equal(PlacementKind.Zone, cfg.Profiles[0].Rules[0].Kind);
        Assert.True(cfg.Profiles[0].Rules[0].UseWorkArea);
    }

    [Fact(DisplayName = "M3 布尔开关默认值往返:Topmost/KeepAspectRatio 等默认 false 保真")]
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

    [Fact(DisplayName = "M3 布尔开关置真后往返保真")]
    public void M3Switches_TrueRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[0].Topmost = true;
        cfg.Profiles[0].KeepAspectRatio = true;
        var back = Deserialize(Serialize(cfg));
        Assert.True(back.Profiles[0].Topmost);
        Assert.True(back.Profiles[0].KeepAspectRatio);
    }

    // ---- Unity 分辨率预设 + MoveOnly ----

    [Fact(DisplayName = "ResolutionPreset 默认为 null,且往返仍为 null")]
    public void ResolutionPreset_DefaultsNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].ResolutionPreset);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].ResolutionPreset);
    }

    [Fact(DisplayName = "ResolutionPreset 全字段往返保真(原神 5120×2088 窗口化)")]
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

    // ---- 背景材质 ----

    [Fact(DisplayName = "Backdrop 默认 Mica,且以字符串形式存盘")]
    public void Backdrop_DefaultMica_SerializedAsString()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Equal(BackdropKind.Mica, cfg.Backdrop);

        var json = Serialize(cfg);
        Assert.Contains("\"Mica\"", json);

        var back = Deserialize(json);
        Assert.Equal(BackdropKind.Mica, back.Backdrop);
    }

    [Theory(DisplayName = "Backdrop 三种取值往返保真")]
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

    // ---- ExePath(图标来源) ----

    [Fact(DisplayName = "Profile.ExePath 默认 null,且往返仍为 null")]
    public void ExePath_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].ExePath);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].ExePath);
    }

    [Fact(DisplayName = "Profile.ExePath 设值后往返保真(含空格与反斜杠路径)")]
    public void ExePath_FullRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[1].ExePath = @"D:\Games\Zenless Zone Zero\ZenlessZoneZero.exe";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(@"D:\Games\Zenless Zone Zero\ZenlessZoneZero.exe", back.Profiles[1].ExePath);
        // 其它未设的仍为 null
        Assert.Null(back.Profiles[0].ExePath);
    }

    // ---- SteamGridDB API key ----

    [Fact(DisplayName = "AppConfig.SteamGridDbApiKey 默认 null,且往返仍为 null")]
    public void SteamGridDbApiKey_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.SteamGridDbApiKey);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.SteamGridDbApiKey);
    }

    [Fact(DisplayName = "AppConfig.SteamGridDbApiKey 设值后往返保真")]
    public void SteamGridDbApiKey_FullRoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.SteamGridDbApiKey = "abc123DEF456";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal("abc123DEF456", back.SteamGridDbApiKey);
    }

    [Fact(DisplayName = "PlacementRule.MoveOnly 默认 false,置真后往返保真")]
    public void MoveOnly_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.False(cfg.Profiles[0].Rules[0].MoveOnly); // 默认
        cfg.Profiles[0].Rules[0].MoveOnly = true;
        var back = Deserialize(Serialize(cfg));
        Assert.True(back.Profiles[0].Rules[0].MoveOnly);
        Assert.False(back.Profiles[0].Rules[1].MoveOnly);
    }

    // ---- LaunchCommand(M5:一键启动) ----

    [Fact(DisplayName = "Profile.LaunchCommand 默认 null,且往返仍为 null")]
    public void LaunchCommand_DefaultNull_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Null(cfg.Profiles[0].LaunchCommand);
        var back = Deserialize(Serialize(cfg));
        Assert.Null(back.Profiles[0].LaunchCommand);
    }

    [Fact(DisplayName = "Profile.LaunchCommand 设值后往返保真(启动器 URI)")]
    public void LaunchCommand_Uri_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[2].LaunchCommand = "hoyoplay://launchgame?gameId=1";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal("hoyoplay://launchgame?gameId=1", back.Profiles[2].LaunchCommand);
        // 其它未设的仍为 null
        Assert.Null(back.Profiles[0].LaunchCommand);
    }

    [Fact(DisplayName = "Profile.LaunchCommand 设值后往返保真(含空格与反斜杠的本地路径)")]
    public void LaunchCommand_LocalPath_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Profiles[1].LaunchCommand = @"D:\Games\Zenless Zone Zero\launcher.exe";
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(@"D:\Games\Zenless Zone Zero\launcher.exe", back.Profiles[1].LaunchCommand);
    }

    // ---- CurtainOpacity(M5:专注模式幕布) ----

    [Fact(DisplayName = "AppConfig.CurtainOpacity 默认 0.7,以数值存盘并往返保真")]
    public void CurtainOpacity_Default_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.Equal(0.7, cfg.CurtainOpacity, 12);
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(0.7, back.CurtainOpacity, 12);
    }

    [Theory(DisplayName = "AppConfig.CurtainOpacity 各取值往返保真(含端值 0 / 1)")]
    [InlineData(0.0)]
    [InlineData(0.35)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    public void CurtainOpacity_Values_RoundTrip(double opacity)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.CurtainOpacity = opacity;
        var back = Deserialize(Serialize(cfg));
        Assert.Equal(opacity, back.CurtainOpacity, 12);
    }

    // ---- Hotkeys(M5:热键绑定字典) ----

    [Fact(DisplayName = "AppConfig.Hotkeys 默认空字典,往返仍为空且非 null")]
    public void Hotkeys_DefaultEmpty_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        Assert.NotNull(cfg.Hotkeys);
        Assert.Empty(cfg.Hotkeys);
        var back = Deserialize(Serialize(cfg));
        Assert.NotNull(back.Hotkeys);
        Assert.Empty(back.Hotkeys);
    }

    [Fact(DisplayName = "AppConfig.Hotkeys 多条绑定往返保真(键值对全保留)")]
    public void Hotkeys_Entries_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Hotkeys["ToggleBorderless"] = "Ctrl+Alt+B";
        cfg.Hotkeys["ToggleCurtain"] = "Ctrl+Alt+F";
        cfg.Hotkeys["SendToZone1"] = "Win+Alt+1";
        cfg.Hotkeys["SendToZone2"] = "Win+Alt+2";
        cfg.Hotkeys["SendToZone3"] = "Win+Alt+3";

        var back = Deserialize(Serialize(cfg));

        Assert.Equal(5, back.Hotkeys.Count);
        Assert.Equal("Ctrl+Alt+B", back.Hotkeys["ToggleBorderless"]);
        Assert.Equal("Ctrl+Alt+F", back.Hotkeys["ToggleCurtain"]);
        Assert.Equal("Win+Alt+1", back.Hotkeys["SendToZone1"]);
        Assert.Equal("Win+Alt+2", back.Hotkeys["SendToZone2"]);
        Assert.Equal("Win+Alt+3", back.Hotkeys["SendToZone3"]);
    }

    [Fact(DisplayName = "AppConfig.Hotkeys 单条改绑往返保真(覆盖默认手势)")]
    public void Hotkeys_Rebind_RoundTrip()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Hotkeys["ToggleCurtain"] = "Ctrl+Shift+D";
        var back = Deserialize(Serialize(cfg));
        Assert.Single(back.Hotkeys);
        Assert.Equal("Ctrl+Shift+D", back.Hotkeys["ToggleCurtain"]);
    }
}
