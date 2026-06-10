# M2 开发契约(多 agent 并行,严格遵守)

> 4 个 agent 并行开发,本文件是唯一权威:文件所有权 + 共享 API 签名。
> 先读 `DESIGN.md` 了解整体设计,再读本文件,只动自己名下的文件。

## 公共约定

- 项目根:`C:\Users\shuia\Projects\Reframe`。命名空间:页面=`Reframe.UI`,服务=`Reframe.Services`,核心=`Reframe.Core`,互操作=`Reframe.Interop`。
- **禁止**:修改 `Reframe.csproj`、`app.manifest`、`DESIGN.md`、本文件、他人名下文件;**禁止运行 dotnet build/run**(集成阶段统一编译);禁止新增 NuGet 包。
- 可用包:Microsoft.WindowsAppSDK 1.8、CommunityToolkit.Mvvm 8.4、H.NotifyIcon.WinUI 2.3。
- UI 文案全部中文。代码注释跟随现有风格(克制,只写非显然约束)。
- WinUI 3 注意:`Window` 没有 Width/Height(用 `AppWindow.Resize`);优先 `x:Bind`;只用系统自带控件和 ThemeResource,不引外部样式。
- 坐标约定:引擎一律物理像素(进程是 PerMonitorV2)。XAML 内指针坐标是 DIP,换算用 `XamlRoot.RasterizationScale`。

## 只读公共文件(谁都不许改)

`Core/Models.cs`、`Core/PlacementResolver.cs`、`Core/WindowOps.cs`、`Core/WindowScanner.cs`、`Services/ConfigStore.cs`、`App.xaml`

## 共享 API(提供方按此实现,消费方按此调用,签名即合同)

### ConfigService — 提供:Agent-Core(`Services/ConfigService.cs`)
```csharp
namespace Reframe.Services;
public sealed class ConfigService
{
    public static ConfigService Instance { get; }   // 单例,首次访问时 Load
    public AppConfig Config { get; }                 // 热重载后引用会更换,用完即取不要缓存
    public event Action? Changed;                    // Save 或外部文件改动后触发;任意线程,UI 自行 DispatcherQueue
    public void Save();                              // 写盘 + 触发 Changed
}
```

### MonitorService — 提供:Agent-Core(`Services/MonitorService.cs`)
```csharp
namespace Reframe.Services;
public sealed record MonitorDesc(string DeviceName, bool IsPrimary,
    int X, int Y, int Width, int Height,            // rcMonitor 物理像素
    int WorkX, int WorkY, int WorkW, int WorkH);    // rcWork
public static class MonitorService
{
    public static IReadOnlyList<MonitorDesc> GetMonitors();
}
```

### Watcher — 提供:Agent-Core(`Core/Watcher.cs` 升级,公共 API 不变)
```csharp
public sealed class Watcher : IDisposable
{
    public Watcher(Func<AppConfig> getConfig);
    public void Start();
    public void Stop(bool restoreWindows = true);
    public bool Running { get; }
    public event Action<string>? Log;
}
```

### RegionPickerWindow — 提供:Agent-Layouts(`UI/RegionPickerWindow.xaml(.cs)`)
```csharp
namespace Reframe.UI;
public sealed partial class RegionPickerWindow : Window
{
    // 在指定显示器上全屏遮罩拖拽选区。
    // 返回相对该显示器左上角的物理像素矩形;Esc 或右键取消 → null。
    public static Task<RectPx?> PickAsync(MonitorDesc monitor);
}
```

### 页面(都是 `: Page`、无参构造、namespace `Reframe.UI`)
| 页面 | 提供方 | 导航参数 |
|---|---|---|
| DashboardPage | Agent-Shell | 无 |
| SettingsPage | Agent-Shell | 无 |
| ProfilesPage | Agent-Profiles | 无 |
| ProfileEditorPage | Agent-Profiles | `e.Parameter` = profile Id (string) |
| LayoutsPage | Agent-Layouts | 无 |
| LayoutEditorPage | Agent-Layouts | `e.Parameter` = layout Id (string) |

导航:列表页内 `this.Frame.Navigate(typeof(XxxEditorPage), id)`;编辑页返回 `this.Frame.GoBack()`。

### App 静态成员(Agent-Shell 维护,他人只读)
```csharp
App.Config  // => ConfigService.Instance.Config(转发)
App.Engine  // Watcher 实例
```

## 文件所有权

| Agent | 文件 |
|---|---|
| **Agent-Shell** | `App.xaml.cs`、`MainWindow.xaml(.cs)`、`UI/DashboardPage.*`、`UI/SettingsPage.*`、`Services/StartupTaskService.cs` |
| **Agent-Profiles** | `UI/ProfilesPage.*`、`UI/ProfileEditorPage.*` |
| **Agent-Layouts** | `UI/LayoutsPage.*`、`UI/LayoutEditorPage.*`、`UI/RegionPickerWindow.*`、`UI/Controls/*`(自由) |
| **Agent-Core** | `Services/ConfigService.cs`、`Services/MonitorService.cs`、`Core/Watcher.cs`、`Core/WinEventHook.cs`(新)、`Interop/NativeMethods.cs`(只增不删改现有成员) |

## 完成标准

每个 agent 交付:自己名下文件全部写完、对照契约自查签名一致、汇报"创建/修改的文件清单 + 实现要点 + 已知风险/未尽事项"。不要求能独立编译(跨 agent 依赖在集成时才齐)。
