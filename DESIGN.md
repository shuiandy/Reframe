# Reframe — 架构设计

> 自用窗口管理器:无边框化 + 按显示器自适应布局。对标并超越 Borderless Gaming(下称 BG)。
> WinUI 3(Windows App SDK 1.8)、.NET 9、unpackaged、requireAdministrator。

## 1. 要解决的痛点(为什么不用 BG)

| # | BG 的问题 | Reframe 的解法 |
|---|---|---|
| 1 | 布局不可复用:每个游戏 profile 都要手填 X/Y/宽/高,改一次布局要逐个改 | **布局(Layout)是一等公民**:命名布局含若干分区(Zone),profile 只引用 `布局+分区`;改布局一处,所有游戏跟着变 |
| 2 | 尺寸是绝对坐标,换显示器(VDD 串流)就错位 | Zone 用 **0~1 比例坐标**存储,相对"窗口当前所在的显示器"解析 → 57″ 上是 5120,VDD 上自动换算 |
| 3 | 没有"按显示器分支":同一游戏在不同屏要不同行为做不到 | Profile 持有**规则列表**(自上而下第一条命中):`7680×2160 → 套布局游戏区`、`任意其它屏 → 铺满` |
| 4 | 改配置要重启 BG 才生效 | 配置热重载(UI 即改即生效 + FileSystemWatcher 监听外部改动) |
| 5 | 3 秒轮询,慢 | **WinEvent 钩子事件驱动**(窗口出现/标题变化/被游戏抢回)+ 低频兜底轮询 |
| 6 | 去框后不可恢复 | 改动前快照原始样式+位置,**禁用 profile / 退出引擎自动还原** |

## 2. 核心数据模型

```
AppConfig
├─ Layouts: Layout[]            ← 一等公民,可复用
│   └─ Zones: Zone[]            ← 比例坐标 (X,Y,W,H ∈ 0..1)
│      (RefWidth/RefHeight 仅供编辑器以像素显示换算)
└─ Profiles: Profile[]
    ├─ Match: Process | Title | TitleRegex        (BG 同款三种)
    ├─ Borderless: bool + Method: Win32 | GpuScaling(预留)
    ├─ DelayMs / Offsets(L,T,R,B)                  (BG 的延迟/偏移)
    └─ Rules: PlacementRule[]   ← 自上而下第一条 Monitor 命中生效
        ├─ Monitor: {Width,Height}  0=任意        (按分辨率识别屏)
        ├─ Kind: None | Fullscreen | Zone | CustomRect
        ├─ LayoutId+ZoneId(Kind=Zone)/ CustomRect(px,相对屏左上角)
        └─ UseWorkArea: bool       (rcWork 避开任务栏 — BG 里 2091 高度就是这么来的)
```

**你的场景表达**(默认配置即此):
- 布局 `57寸·左游戏右副屏`:zone `游戏区`(0, 0, ⅔, 1) + zone `副屏区`(⅔, 0, ⅓, 1),Ref 7680×2160
- 崩铁/绝区零/原神 三个 profile,规则都是:
  1. `7680×2160` → Zone(`57寸·左游戏右副屏`, `游戏区`) → 本地 = 5120 贴左
  2. `任意` → Fullscreen → VDD/其它屏 = 铺满(随 Moonlight 分辨率自适应)

## 3. 引擎管线

```
检测 ──→ 匹配 ──→ 解析 ──→ 应用 ──→ 守护
WinEventHook         PlacementResolver   WindowOps
+ 兜底轮询(5s)       (屏→规则→px矩形)    (样式/位置/快照还原)
```

1. **检测**:`SetWinEventHook`(OUT_OF_CONTEXT)订阅 `EVENT_OBJECT_SHOW`(新窗口)、`EVENT_OBJECT_NAMECHANGE`(标题后到,如原神)、`EVENT_SYSTEM_FOREGROUND`;对已接管窗口订阅 `EVENT_OBJECT_LOCATIONCHANGE`(游戏自己改回去 → 防抖 500ms 后重新糊上,设最大重试次数防拉锯)。兜底:5s 轮询一次全量扫描。
2. **匹配**:进程名(忽略 .exe、大小写)/ 标题包含 / 标题正则。同进程多窗口:取首个"像主窗口"的(可见、有标题、无 owner、非 toolwindow)。
3. **解析**:`MonitorFromWindow` 拿当前屏 → 走 profile 规则表 → Zone 比例 × (rcMonitor|rcWork) → 加 Offsets → 目标矩形(物理像素;PMv2 清单已配,多屏不同 DPI 坐标不歪)。
4. **应用**:`DelayMs` 未到不动手(游戏启动期会重建窗口);先快照原始 style/rect;去样式(`WS_CAPTION|WS_THICKFRAME|WS_BORDER|WS_DLGFRAME` + 对应 EX)→ `SetWindowPos(SWP_FRAMECHANGED)`。仅在与目标不一致时动手。
5. **还原**:profile 禁用 / 引擎停止 / 退出 → 还原快照。

## 4. BG 功能逐项分析与对应

### 无边框两种实现
| BG 模式 | 原理 | Reframe |
|---|---|---|
| **Win32 (Classic)** | `SetWindowLongPtr` 砍样式 + `SetWindowPos` | **M1 实装**(同原理) |
| **缩放 (GPU)** | Windows.Graphics.Capture 捕获游戏窗口 → D3D 渲染到全屏覆盖层(可挂 Anime4K/FSR 等 shader、控制 sync/direct flip、画光标) | **M4 预留**接口 `BorderlessMethod.GpuScaling`;本质是个小合成器,先不做 |

### 配置项映射(截图逐项)
| BG 选项 | 实现原理 | 计划 |
|---|---|---|
| 启用无边框 | 砍 caption/thickframe 样式 | M1 |
| 窗口大小:全屏 / 自定义(X,Y,W,H) | rcMonitor / 绝对矩形 | M1(新增 **Zone** 模式) |
| 可视化选择区域 | 截图式拖拽选区 | M2(全屏遮罩 overlay,拖拽出矩形,显示尺寸角标,吸附边缘/等分线/常见比例 16:9·21:9·32:9) |
| 无边框延迟 | 检测后等 N 秒再处理 | M1 |
| 窗口偏移 L/T/R/B | 最终矩形加偏移 | M1 |
| 保持客户区大小 | 去框后用 `AdjustWindowRectExForDpi` 反算,使客户区尺寸不变 | M3 |
| 保持宽高比 | 在目标区域内 letterbox 等比居中 | M3 |
| 超级尺寸(跨所有屏) | 虚拟桌面矩形(SM_*VIRTUALSCREEN) | M3(PlacementKind.SpanAll) |
| 移除菜单 | `SetMenu(hwnd, NULL)`(备份以还原) | M3 |
| 始终置顶 | `HWND_TOPMOST` | M3 |
| 隐藏任务栏 / 失焦恢复 | 操作 Shell_TrayWnd 显示状态 | M3 |
| 将光标限制在窗口内 | 前台时 `ClipCursor(rect)`,失焦释放 | M3 |
| 后台静音 | WASAPI `IAudioSessionManager2` 按 pid 静音 | M3 |
| 发送最大化命令 | `ShowWindow(SW_MAXIMIZE)`(部分游戏要先最大化再去框) | M3 |
| Force redraw | `RedrawWindow` | M3 |
| 微调窗口(nudge) | ±1px 触发框架重算 | M3 |
| 翻转处理顺序 | 先尺寸后样式 ↔ 先样式后尺寸(逐 profile) | M3 |
| 隐藏鼠标光标 | 光标克隆/替换,脏活 | M4(随 GPU 模式) |
| 背景容器(纯色/渐变/图片) | 在游戏矩形之外铺一层底色窗 | M3「幕布」:letterbox 时把 zone 外区域遮黑,本地 5120 时右侧不遮(可配) |

### 新增(BG 没有的)
- **FancyZones 式布局编辑器**(M2):画布上把"屏"分区;预设模板:二等分、三等分、左⅔+右⅓(你的场景)、16:9 居中、21:9 居左、自定义网格;拖动分隔线调整;存为命名布局。
- **一键套用布局到多个 profile**(M2):选中 N 个游戏 → 套同一 zone。
- **全局热键**(M3):对前台窗口手动触发/还原(对标 BG 的 Win+F6),`RegisterHotKey`。
- **拖拽吸附**(backlog):按住修饰键拖窗口 → zone 高亮 → 松手入位(FancyZones 行为)。

## 5. UI 结构(WinUI 3)

```
MainWindow = NavigationView
├─ 仪表盘   引擎开关 · 当前命中的窗口(实时) · 日志流
├─ 配置文件 列表(启用开关/名称/匹配) → 编辑页
│            编辑页 = 匹配区 + 无边框区 + 规则表(可排序) + 高级折叠区
├─ 布局     布局列表 → 布局编辑器(分区画布 + 预设 + 像素/比例双显)
└─ 设置     轮询间隔 · 开机自启(计划任务) · 语言 · 关于
托盘:H.NotifyIcon(关闭至托盘、右键 引擎开/关·退出)
```

## 6. 技术决策

- **unpackaged WinExe + `requireAdministrator` 清单**:必须管理员才能动反作弊游戏的窗口(UIPI);打包(MSIX)与 requireAdministrator 冲突,所以不打包。
- **开机自启**:计划任务(最高权限、登录触发)→ 免 UAC 弹窗。设置页一键创建/删除。
- **PerMonitorV2 DPI**:全引擎物理像素坐标,多屏混缩放不歪。
- **MVVM**:CommunityToolkit.Mvvm 源生成;Core 层零 UI 依赖(可单测解析数学)。
- **配置**:`%LOCALAPPDATA%\Reframe\config.json`,System.Text.Json 源生成(留 AOT 余地),`Version` 字段做迁移。
- **单实例**:命名互斥量,二次启动唤起已有窗口。

## 7. 里程碑

- **M1 能用**(当前):核心引擎(检测/匹配/解析/应用/还原)+ 默认配置(三个游戏、57″布局)+ 最小窗口(开关+日志)+ 托盘。→ 先解决串流问题。
- **M2 好用**:布局编辑器、可视化选区、profile 编辑 UI、配置热重载、开机自启、WinEvent 钩子(替代纯轮询)。
- **M3 平价+**:BG 高级选项全量(上表 M3 列)、全局热键、幕布。
- **M4 炫技**(可选):GPU 缩放模式(WGC + 合成器 + shader)。

## 8. 风险

- 个别游戏(独占全屏)没有可操作的边框窗口 → 提示用户改窗口/无边框模式启动(米哈游三件套都是 borderless/windowed,没问题)。
- UWP/受保护进程窗口动不了 → 明确报"无权限"。
- 反作弊对 `SetWindowLongPtr` 的容忍:BG 多年实践证明安全(只动样式不注入),沿用同等保守做法:**绝不注入、绝不读写游戏内存**。
