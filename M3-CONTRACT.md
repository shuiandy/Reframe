# M3 开发契约附录(继承 M2-CONTRACT.md 全部约定,本文件只写增量)

## 共享 API 变更/新增(签名即合同)

### PlacementResolver(`Core/PlacementResolver.cs`)— 所有者:Agent-Engine
```csharp
// Target 扩展(消费方:WindowOps.Apply、Watcher、测试)
public readonly record struct Target(bool MakeBorderless, NativeMethods.RECT? Rect, bool Topmost);

// 纯函数解析核(单元测试靶点,不碰任何 Win32):
// rcMonitor/rcWork 为该屏物理矩形,currentWindowRect 为窗口当前矩形(KeepAspectRatio 用)
public static NativeMethods.RECT? ResolveRect(
    NativeMethods.RECT rcMonitor, NativeMethods.RECT rcWork,
    NativeMethods.RECT currentWindowRect, Profile p, AppConfig cfg);
// 原 Resolve(WindowInfo, Profile, AppConfig) 保留,内部取屏幕信息后调 ResolveRect
```

### Watcher(`Core/Watcher.cs`)— 所有者:Agent-Engine
```csharp
public void ReleaseProfile(string profileId);  // 还原该 profile 接管的全部窗口并解除跟踪(UI 禁用 profile 时调)
```

### WindowOps(`Core/WindowOps.cs`)— 所有者:Agent-Engine
`Apply(IntPtr, in PlacementResolver.Target)` 签名不变,内部支持 Topmost(HWND_TOPMOST/HWND_NOTOPMOST)。

### TrayIcon — 所有者:Agent-Shell2(新文件,自带 P/Invoke,不碰 NativeMethods.cs)
实现方式自定(H.NotifyIcon 或裸 Shell_NotifyIcon),对外行为:
- 主窗口点关闭 = 隐藏到托盘,引擎继续跑
- 托盘左键 = 显示/激活主窗口;右键菜单:打开 / 引擎开·关 / 退出(退出=还原窗口+真正退出)

## 文件所有权(M3)

| Agent | 文件 |
|---|---|
| **Agent-Engine** | `Core/PlacementResolver.cs`、`Core/WindowOps.cs`、`Core/Watcher.cs`、`Core/WinEventHook.cs`、`Interop/NativeMethods.cs`(只增)、`Core/AudioMute.cs`(新,选做) |
| **Agent-Shell2** | `App.xaml.cs`、`MainWindow.xaml(.cs)`、托盘新文件(`Services/` 或 `UI/` 下)、`UI/LayoutsPage.xaml.cs`(仅删除级联部分)、`UI/ProfilesPage.xaml.cs`(仅禁用还原联动部分) |
| **Agent-Tests** | `Tests/` 整个目录(独立 csproj,链接主工程源文件,不改主 csproj) |

只读公共:`Core/Models.cs`、`Core/WindowScanner.cs`、`Services/*`(Shell2 名下除外)、其余 UI 页面。

## 验收口径(成品定义,集成阶段逐项核)

1. 托盘常驻:关窗不退、托盘可恢复/退出
2. 单实例:二次启动唤起已有窗口
3. 引擎:Topmost / KeepAspectRatio(zone 内等比 letterbox)/ ClipCursor(前台才夹)生效;MuteInBackground 选做
4. 禁用 profile → 其窗口立即还原
5. 删除布局 → 引用规则清理(提示后)
6. `dotnet test` 全绿(解析数学、匹配、配置往返)
7. 全 UI 运行时走查无崩溃
