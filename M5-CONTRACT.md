# M5 开发契约(主动操作轮;继承 M2~M4 契约全部约定,本文件只写增量)

## 模型已就位(统筹者已加,所有 agent 只读 Models.cs,不许再改)
- `Profile.LaunchCommand`(string?,空则用 ExePath;支持 URI)
- `AppConfig.CurtainOpacity`(double,默认 0.7)
- `AppConfig.Hotkeys`(Dictionary<string,string>,动作 Id → 手势;缺省由 HotkeyService 补默认)

## 共享 API(签名即合同)

### CurtainService — 提供:Agent-Curtain(`Services/CurtainService.cs`)
```csharp
public static class CurtainService
{
    public static bool IsOn { get; }
    public static void Toggle();   // UI 线程调用
    public static void Off();      // 幂等;应用退出路径必须调
}
```
消费方:Agent-Hotkey(ToggleCurtain 动作经 DispatcherQueue 切 UI 线程后调 Toggle)、App 退出路径(Agent-Hotkey 维护 App.xaml.cs,加 Off())。

### ConfigBackup — 提供:Agent-Pack(`Services/ConfigBackup.cs`)
```csharp
public static class ConfigBackup
{
    public static Task<bool> ExportAsync(Microsoft.UI.Xaml.XamlRoot root); // FileSavePicker 导出 config.json
    public static Task<bool> ImportAsync(Microsoft.UI.Xaml.XamlRoot root); // FileOpenPicker 导入+校验+Save
}
```
消费方:Agent-Hotkey(SettingsPage 的「配置备份」按钮按此签名调用)。

### HotkeyService 默认手势(Agent-Hotkey 实现)
ToggleBorderless=Ctrl+Alt+B(沿用现有)、ToggleCurtain=Ctrl+Alt+F、SendToZone1/2/3=Win+Alt+1/2/3。

## 文件所有权(M5)

| Agent | 文件 |
|---|---|
| **Agent-Launcher** | `UI/ProfilesPage.*`、`UI/ProfileEditorPage.*`、`Services/GameLauncher.cs`(新) |
| **Agent-Curtain** | `Services/CurtainService.cs`(新)、`UI/CurtainWindow.*`(新,或纯代码)、`UI/DashboardPage.*`(仅加专注模式开关+不透明度控件) |
| **Agent-Hotkey** | `Services/HotkeyService.cs`(新)、`Services/TrayIcon.cs`(热键逻辑迁出/菜单加专注模式)、`App.xaml.cs`(集成)、`UI/SettingsPage.*`(热键区+配置备份按钮) |
| **Agent-Pack** | `Services/ConfigBackup.cs`(新)、`tools/publish.ps1`(新)、`Reframe.csproj`(仅版本属性)、`Tests/*`(新字段往返用例)、`README.md`(新) |

只读公共:其余全部(Models.cs 本轮也是只读!)。NativeMethods 本轮归 Agent-Hotkey 只增。

## 验收口径
1. 配置行可一键启动游戏(先写分辨率预设再启动;支持 URI)
2. 专注模式:游戏区外遮暗、可调透明度、点击穿透(遮暗区域仍可操作)、热键/托盘/仪表盘开关
3. Win+Alt+1/2/3 送前台窗口入分区;全部热键可在设置页改绑;冲突/无效手势有提示
4. 设置页可导出/导入配置;publish.ps1 产出带版本号 Release zip
5. build 0 错,测试全绿(75+新增)
