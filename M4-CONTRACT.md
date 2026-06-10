# M4 开发契约(体验改造轮;继承 M2/M3-CONTRACT.md 全部约定,本文件只写增量)

## 共享 API 变更/新增(签名即合同)

### Watcher(`Core/Watcher.cs`)— 所有者:Agent-Dashboard
```csharp
public sealed record TakenWindow(IntPtr Handle, string ProfileId);
public IReadOnlyList<TakenWindow> GetTakenWindows();  // 当前被接管的窗口快照
public void Poke();                                    // 立即调度一次扫描(重新应用)
```
(WindowOps.Restore(IntPtr) 已是 public,可直接用于"还原单个"。)

### DragSnapService(`Core/DragSnap.cs`,新)— 所有者:Agent-DragSnap
```csharp
public static class DragSnapService
{
    public static void Start(Func<AppConfig> getConfig);  // 启动拖拽吸附监听(内部自管线程/钩子)
    public static void Stop();
}
```
**集成点(Agent-Visual 负责,在 App.xaml.cs)**:`Engine.Start()` 之后调 `DragSnapService.Start(() => ConfigService.Instance.Config)`;退出路径(托盘退出)`DragSnapService.Stop()`。Visual 按此两行集成,不关心内部。

### Models(`Core/Models.cs`)— 本轮所有者:Agent-DragSnap(只加不改)
```csharp
// AppConfig 增加:
public bool DragSnapEnabled { get; set; } = true;   // 按住修饰键拖窗口吸附到分区
```

## 文件所有权(M4)

| Agent | 文件 |
|---|---|
| **Agent-Dashboard** | `UI/DashboardPage.*`、`UI/Controls/LiveMonitorMap.cs`(新)、`Core/Watcher.cs`(只加契约成员) |
| **Agent-Picker** | `UI/ProfilesPage.*`、`UI/WindowPickerDialog.*`(新,或用 ContentDialog 纯代码) |
| **Agent-DragSnap** | `Core/DragSnap.cs`(新)、`UI/SnapOverlayWindow.*`(新)、`Core/Models.cs`(仅加 DragSnapEnabled)、`UI/SettingsPage.*`(仅加开关一行)、`Interop/NativeMethods.cs`(只增) |
| **Agent-Visual** | `App.xaml(.cs)`、`MainWindow.xaml(.cs)`、`Reframe.csproj`(仅 ApplicationIcon/资源)、`Assets/*`(新)、`UI/LayoutsPage.xaml`(仅视觉)、`Services/TrayIcon.cs`(仅图标来源改进,可选) |

只读公共:其余全部。`ConfigService`/`MonitorService`/`WindowScanner`/`PlacementResolver`/`WindowOps` 按现有签名调用。

## 验收口径
1. 仪表盘:实时小地图画出各显示器+相关分区+被接管窗口位置(1~2s 刷新),接管窗口卡片可[还原][重新应用]
2. 配置文件页:「从窗口创建」列出运行中窗口(标题+进程),一键生成 profile
3. 按住 Shift 拖任意窗口:分区覆盖层高亮,松手吸附入位;设置页可关
4. Mica 背景 + 应用图标 + 布局卡片 hover;整体 Win11 原生质感
5. build 0 错、67 测试全绿
