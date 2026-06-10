using System.Text.Json.Serialization;

namespace Reframe.Core;

/// <summary>匹配方式:进程名 / 窗口标题(含) / 标题正则。</summary>
public enum MatchKind { Process, Title, TitleRegex }

/// <summary>无边框实现。GpuScaling 为 M4 预留(WGC 捕获 + 覆盖层渲染)。</summary>
public enum BorderlessMethod { Win32, GpuScaling }

/// <summary>主窗口背景材质。Mica/MicaAlt = 云母及其变体,Acrylic = 桌面亚克力(毛玻璃)。</summary>
public enum BackdropKind { Mica, MicaAlt, Acrylic }

/// <summary>规则命中后对窗口几何做什么。</summary>
public enum PlacementKind
{
    None,        // 只去边框,不动几何
    Fullscreen,  // 铺满当前屏
    Zone,        // 套用某布局的某分区
    CustomRect   // 绝对矩形(相对当前屏左上角,物理像素)
}

/// <summary>物理像素矩形(相对某显示器左上角)。</summary>
public sealed class RectPx
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>最终矩形的四边微调(对标 BG 的窗口偏移)。</summary>
public sealed class Offsets
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

/// <summary>按分辨率识别一块屏。0 = 任意。直观且跨设备名稳定(\\.\DISPLAYn 会漂移)。</summary>
public sealed class MonitorFilter
{
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Matches(int w, int h)
        => (Width == 0 || Width == w) && (Height == 0 || Height == h);
}

/// <summary>布局中的一个分区。坐标为 0..1 比例,相对"窗口当前所在显示器"。</summary>
public sealed class Zone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;
}

/// <summary>命名布局 = 一组分区。一等公民:多个 profile 复用,改一处全跟随。</summary>
public sealed class Layout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名布局";

    /// <summary>编辑器用的参考分辨率,仅供比例↔像素换算显示,不参与运行时解析。</summary>
    public int RefWidth { get; set; } = 7680;
    public int RefHeight { get; set; } = 2160;

    public List<Zone> Zones { get; set; } = new();
}

/// <summary>放置规则:窗口所在屏命中 Monitor 过滤器时,执行 Kind。</summary>
public sealed class PlacementRule
{
    public MonitorFilter Monitor { get; set; } = new();
    public PlacementKind Kind { get; set; } = PlacementKind.Fullscreen;

    // Kind == Zone
    public string? LayoutId { get; set; }
    public string? ZoneId { get; set; }

    // Kind == CustomRect
    public RectPx? CustomRect { get; set; }

    /// <summary>true = 以工作区(rcWork,避开任务栏)为基准;false = 整屏(rcMonitor)。</summary>
    public bool UseWorkArea { get; set; }

    /// <summary>
    /// 只定位:把窗口左上角放到目标矩形左上角,保持窗口当前尺寸不变(不 resize)。
    /// 用于"渲染分辨率钉死在注册表"的 Unity 游戏(见 <see cref="UnityResolutionPreset"/>):
    /// resize 只会整张缩放(拉伸),所以这里只挪位置。与 KeepAspectRatio 冲突时 MoveOnly 优先。
    /// </summary>
    public bool MoveOnly { get; set; }
}

/// <summary>Unity 游戏的启动分辨率预设:启动前把 Screenmanager 注册表写成目标值(原神等"渲染分辨率钉死在注册表"的游戏)。</summary>
public sealed class UnityResolutionPreset
{
    public bool Enabled { get; set; }
    public string RegistryPath { get; set; } = "";  // 如 Software\miHoYo\原神(HKCU 下相对路径)
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Windowed { get; set; } = true;       // Is Fullscreen mode = 0
}

/// <summary>一个游戏/应用的完整配置。</summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名";
    public bool Enabled { get; set; } = true;

    public MatchKind MatchKind { get; set; } = MatchKind.Process;
    public string MatchValue { get; set; } = "";

    /// <summary>
    /// 可执行文件完整路径(可空)。主要用作图标来源:反作弊保护的游戏(绝区零/原神等)
    /// 读不到 MainModule,手动指定此路径后 IconCache 可直接从 exe 提取图标。将来也可用于启动。
    /// </summary>
    public string? ExePath { get; set; }

    public bool Borderless { get; set; } = true;
    public BorderlessMethod Method { get; set; } = BorderlessMethod.Win32;

    /// <summary>检测到窗口后等待多久再处理(游戏启动期会重建窗口)。</summary>
    public int DelayMs { get; set; } = 1000;

    public Offsets Offsets { get; set; } = new();

    /// <summary>自上而下,第一条 Monitor 命中的规则生效。建议末尾放一条"任意屏"规则。</summary>
    public List<PlacementRule> Rules { get; set; } = new();

    // ---- M3 预留开关(引擎逐步支持) ----
    public bool Topmost { get; set; }
    public bool KeepAspectRatio { get; set; }
    public bool PreserveClientArea { get; set; }
    public bool MuteInBackground { get; set; }
    public bool ClipCursor { get; set; }

    /// <summary>
    /// Unity 启动分辨率预设(可空,默认无)。启用后引擎会在游戏未运行时把 Screenmanager
    /// 注册表写成目标分辨率,使游戏按目标分辨率渲染,再配合 MoveOnly 规则只定位不缩放。
    /// </summary>
    public UnityResolutionPreset? ResolutionPreset { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 1500;
    public bool EngineEnabled { get; set; } = true;

    /// <summary>按住修饰键(Shift)拖窗口时,显示分区覆盖层并在松手时吸附到分区(FancyZones 式)。</summary>
    public bool DragSnapEnabled { get; set; } = true;

    /// <summary>主窗口背景材质。改即生效(经 ConfigService.Changed → MainWindow.ApplyBackdrop)。</summary>
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>
    /// SteamGridDB API key(可空)。配了才启用"在线图标"兜底:本地全失败时按游戏名联网取图标。
    /// 免费申请:https://www.steamgriddb.com/profile/preferences/api 。空 = 该功能静默关闭。
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    public List<Layout> Layouts { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>首次运行的默认配置:57″ 左游戏右副屏布局 + 三个米哈游游戏(本地套布局、其它屏铺满)。</summary>
    public static AppConfig CreateDefault()
    {
        var layout = new Layout
        {
            Name = "57寸·左游戏右副屏",
            RefWidth = 7680,
            RefHeight = 2160,
            Zones =
            {
                new Zone { Name = "游戏区", X = 0,       Y = 0, W = 2.0 / 3, H = 1 }, // 5120 贴左
                new Zone { Name = "副屏区", X = 2.0 / 3, Y = 0, W = 1.0 / 3, H = 1 }, // 2560 在右
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
                new PlacementRule   // 本地 57″:套布局游戏区(避开任务栏)
                {
                    Monitor = new MonitorFilter { Width = 7680, Height = 2160 },
                    Kind = PlacementKind.Zone,
                    LayoutId = layout.Id,
                    ZoneId = gameZone.Id,
                    UseWorkArea = true
                },
                new PlacementRule   // 其它任何屏(VDD 串流):铺满
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
