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
}
