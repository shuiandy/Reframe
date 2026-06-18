# Reframe 窗口位置持久化 — 实现计划（PersistentWindows 完整移植）

> 目标：把 [PersistentWindows](https://github.com/kangyu-california/PersistentWindows) 的能力完整移植进 Reframe，
> 解决"显示器切走再切回 / 睡眠唤醒 / 串流后回桌面，所有窗口位置和大小被打乱"的问题。
> 适配 Reframe 既有架构（Core 可单测、Interop 放 P/Invoke、Services 放磁盘/系统集成、专用线程 + 消息泵的钩子模式）。

---

## 0. v2 修订（Codex 评审后）

> Codex 的沙箱未能读到仓库源码（`CreateProcessAsUserW failed: 5`），故其评审基于设计描述 + Microsoft Win32 文档核验，非代码实证；几处关于"写进 config.json / listener 依赖 Services"的判断与本计划不符（计划本就用独立 `LayoutStore` + 纯事件源 listener）。以下为**采纳的实质性修订**：

1. **冻结改为单线程 actor + 拓扑 epoch（取代散落 flag/锁）** —— 所有 capture/restore/takeover/系统事件串行进**一个事件队列**处理。修订 §3.2、§8。
2. **还原决策由"拓扑签名变化"驱动，而非 raw 消息** —— 任何触发源（display/power/session/DPI/workarea）只负责"重算 `MonitorService` 拓扑签名 + 投递事件"，签名变了才进还原。补订阅 `WM_DPICHANGED`。修订 §5、§8。
3. **引擎协作：source-tagged 写 + mutation scope（取代仅跳过 `_takeover`）** —— `WindowOps` 所有写入带 `MutationSource`（Takeover / PersistenceRestore / User）；`Watcher` 暴露 `BeginSystemMutation/EndSystemMutation`；捕获层忽略非 User 来源；**还原自身产生的 `LOCATIONCHANGE` 用 per-HWND suppression token 屏蔽**（原计划漏点）。修订 §11、§6。
4. **几何记录更丰富** —— 存 monitor-relative 物理矩形 + DPI + 工作区 + `showCmd`；**严禁把 `rcNormalPosition` 传给 `SetWindowPos`**。修订 §4、§7。
5. **电源/会话 API 纠正** —— 唤醒用普通 `WM_POWERBROADCAST`(`PBT_APMSUSPEND`/`PBT_APMRESUMEAUTOMATIC`/`PBT_APMRESUMESUSPEND`)或 `RegisterSuspendResumeNotification`；`RegisterPowerSettingNotification` 仅用于显示器电源 GUID；WTS 配套 unregister + TermSrv 未就绪降级重试。修订 §5。
6. **分层修正** —— Core 的 `PersistenceEngine` 不直接引用 `Services.MonitorService`，改注入 `Func<IReadOnlyList<MonitorDesc>>`（仿 `Watcher` 注入 `Func<AppConfig>`）。修订 §3.1。
7. **`LayoutStore` 单写者 + epoch 比较 + TTL/上限**，防互相覆盖与无限堆积。修订 §9.2。
8. **新增竞态单测**（fake `IWindowOps`/monitor provider/事件源）：覆盖"WinEvent 先于显示消息""接管集滞后""还原期间捕获""解锁+显示变化叠加"。修订 §14。

**保留意见**：Codex 建议做跨 DPI 重映射；本计划坚持只在**相同 DisplayKey**(同显示器集⇒同 DPI)下还原，P1 不实现 remap，仅**记录 DPI** 留作 P3 余地。

---

## 0b. v3 修订（Codex 代码实证后）

> 第二轮 Codex 实际读取了源码并逐条给出文件:行号，修正了 v1/v2 里几处**对现有代码的硬假设**。以下为代码实证后的更正，**优先级高于正文相应段落**：

**更正的硬假设：**
1. **`MonitorDesc` 在 Services 不在 Core**（[MonitorService.cs:7](../Services/MonitorService.cs)）。`PersistenceEngine`（Core）无法直接注入 `Func<IReadOnlyList<MonitorDesc>>` 而不破坏分层。**P1 第一步：把 monitor DTO 下沉到 Core**（或建 Core 侧 DTO），再做注入。修正 §0-6、§3.1。
2. **`WinEventHook` 仅注册 `SHOW..NAMECHANGE` + `FOREGROUND`**（[WinEventHook.cs:86](../Core/WinEventHook.cs)），`MOVESIZESTART/END` 是 `DragSnap` 另起的独立 hook（[DragSnap.cs:123](../Core/DragSnap.cs)）。捕获信号源**需扩 `WinEventHook` 第三组 hook 或新起一个**，不是"多订阅一个消费者"。修正 §3.2、§6。
3. **隐藏窗口模板不是 `WinEventHook`**（它无 HWND，只是 hook+pump）。真正可仿的是 `HotkeyService`/`TrayIcon` 的 message-window（[HotkeyService.cs:197](../Services/HotkeyService.cs)、[TrayIcon.cs:84](../Services/TrayIcon.cs)）。**退出走 `PostMessage(WM_CLOSE) → DestroyWindow → WM_DESTROY → PostQuitMessage`，且在 destroy 前 unregister power/WTS**，不能 `PostThreadMessage(WM_QUIT)` 直接掐泵。修正 §5。
4. **`CreateWindowEx/RegisterClassEx/DefWindowProc` 已在 `HotkeyService`、`TrayIcon` 各私有重复**（[HotkeyService.cs:485](../Services/HotkeyService.cs)、[TrayIcon.cs:408](../Services/TrayIcon.cs)）。P1 要决策：**上移到 `NativeMethods` 统一**（推荐，顺带消重）还是接受第三份。计入迁移成本。
5. **`WindowOps.Apply/Restore` 调用点遍布**：UI 快捷去框、热键、设置页 `RestoreAll`（[ProfilesPage.xaml.cs:714](../UI/ProfilesPage.xaml.cs)、[HotkeyService.cs:393](../Services/HotkeyService.cs)、[SettingsPage.xaml.cs:463](../UI/SettingsPage.xaml.cs)）。加 `MutationSource` 改签名要**逐个调用点标注**；**默认"无 token = `User`"**（`DragSnap`/热键的直接 `SetWindowPos` 视为用户意图）。修正 §11。
6. **`App` 先 `Engine.Start()` 再起其它**（[App.xaml.cs:173](../App.xaml.cs)）。持久化若要订阅引擎 mutation，**须在 `Engine.Start()` 之前接好**，否则启动首轮 `ScheduleTick()`（[Watcher.cs:156](../Core/Watcher.cs)）已排队，漏掉首批 mutation 通知。

**两个 v2 仍未闭合的并发隐患：**
- **actor/epoch 只串行持久化自身事件，串不动 `Watcher`**：`Watcher` 仍由 hook/timer/poll 各自驱动，`SafeTick` 只防重入不入队（[Watcher.cs:319](../Core/Watcher.cs)）。跨系统协调**只能靠 §11 的 source-tag + mutation-scope**，不能指望 actor 统一两套。
- **"WinEvent 先于 `WM_DISPLAYCHANGE`" 未堵死**：若 `LOCATIONCHANGE` 先到、capture debounce 先执行，而 `WM_DISPLAYCHANGE` 尚未进队，epoch 未自增 → 旧事件逃过过期判定，把重排后的瞬态写进快照。**修补：每次 capture 执行前先重算当前 DisplayKey，与上次比对；不一致即转 `Freezing` 并丢弃本次捕获，不写快照。** 补入 §8。

**Codex 给的 P1 落地顺序（采纳为正式顺序，取代 §15 的 P1 行）：**
1. **修分层契约**：monitor DTO 下沉 Core → 写 `LayoutKey` 纯函数 + 单测。
2. **`WindowOps` 加 `MutationSource` + per-HWND TTL suppression 查询**；更新所有 `Apply/Restore/RestoreAll` 调用点（默认 `User`）。
3. **`Watcher` 内围住 `Apply/Restore` 暴露 active mutation scope**；不再把 `_takeover` 当主防线。
4. **`DisplayChangeListener`**：真隐藏顶层 HWND，仅接 `WM_DISPLAYCHANGE`；生命周期 `WM_CLOSE → DestroyWindow → PostQuitMessage`。
5. **memory-only `PersistenceEngine` actor**：capture 前重算 DisplayKey；`Restoring/Settling` 丢弃 capture；跳过 active mutation 与 `_takeover`。
6. **`RestorePlacement` 普通/最大化路径 + `RedrawWindow`**。磁盘 `LayoutStore`、WTS/Power、`QueryDisplayConfig` 推到 P2/P3。

---

## 1. 范围与非目标

### 1.1 范围（完整移植，分阶段交付）
- 显示拓扑变化（分辨率 / 显示器增删 / 方向 / 串流 VDD 上下线）后**自动还原**所有普通顶层窗口的位置与大小。
- 唤醒、显示器上电、解锁/RDP 重连等触发源同样还原。
- 持续**捕获**窗口位置，按"显示器配置指纹"分桶记忆。
- **跨重启持久化**到磁盘；重启后对重新启动的应用按身份匹配并还原。
- **命名快照**（手动存/取布局）+ 热键。
- 与现有无边框引擎协作不打架。

### 1.2 非目标（本期不做）
- 虚拟桌面感知（多桌面分别记忆）—— 列为待定，见 §16。
- 跨机器同步布局。
- GPU 缩放/合成相关（属 M4 另一条线）。

---

## 2. 概念模型与术语

| 术语 | 含义 |
|---|---|
| **DisplayKey** | 当前显示器集合的稳定指纹（字符串）。同一组显示器/分辨率/拓扑 ⇒ 同一 key。 |
| **WindowRecord** | 一个窗口在某 DisplayKey 下的记忆：身份 + 位置矩形 + 显示状态（normal/max/min）+ z-order 提示。 |
| **LayoutSnapshot** | 某 DisplayKey → `WindowRecord[]`。一个 key 一份"好布局"。 |
| **会话内身份** | 进程存活期间用 `hWnd` 唯一标识窗口（够用且简单）。 |
| **跨会话身份** | 重启后用 `(进程可执行路径 + 窗口类名 + 标题签名)` 重新匹配窗口。 |
| **冻结捕获** | 处于还原过渡期时暂停捕获，防止被打乱的布局覆盖好快照（核心正确性不变式）。 |

**与现有引擎的根本区别**（见 DESIGN §3）：现有 `WindowOps._originals` 只记忆**被 profile 命中的游戏窗口**，"还原"= 撤销我们对它做的去框/改位。本功能记忆**所有普通顶层窗口**，按 DisplayKey 分桶，"还原"= 在显示变化后**重放上一次该配置下的好布局**。数据、生命周期、触发源都不同 —— 是兄弟功能，不是改引擎。

---

## 3. 架构总览

### 3.1 分层落位

```
Interop/NativeMethods.cs        + WINDOWPLACEMENT/Get/SetWindowPlacement、RedrawWindow、
                                  CreateWindowEx/DefWindowProc/RegisterClass、
                                  RegisterPowerSettingNotification、WTSRegisterSessionNotification、
                                  QueryDisplayConfig（P3 稳定显示器 id）、相关消息/常量

Core/DisplayChangeListener.cs   隐藏顶层窗口 + 专用线程 + 消息泵，抛 SystemLayoutEvent（display/power/session/workarea）
                                  —— 仿 Core/WinEventHook.cs 的线程模型，仅依赖 Interop
Core/LayoutKey.cs               纯函数：从 MonitorDesc 列表算 DisplayKey（可单测）
Core/WindowIdentity.cs          纯函数：会话内/跨会话身份计算与匹配（可单测）
Core/LayoutSnapshot.cs          数据模型 + diff/还原计划算法（纯，可单测）
Core/PersistenceEngine.cs       编排：捕获 ⊕ DisplayChangeListener ⊕ 状态机 ⊕ 还原；
                                  注入 ILayoutStore（磁盘）与 Func<ISet<IntPtr>>（引擎接管集）；
                                  暴露 static OnThreadError（仿 Watcher）
Core/WindowOps.cs               + RestorePlacement(hWnd, record)（放置 + 状态 + z-order + 重绘）
Core/ILayoutStore.cs           接口：Load()/Save(snapshot)/LoadAll()（Core 只认接口，保持可测）

Services/LayoutStore.cs         ILayoutStore 的磁盘实现：%LOCALAPPDATA%\Reframe\layouts\，
                                  System.Text.Json 源生成 + 原子写 + 防抖（仿 ConfigStore）
Services/...                    托盘菜单项、设置页接线

UI/SettingsPage 等              "窗口位置记忆"开关组 + 命名快照管理（P4）
```

> 关键约束复述：**Core 不得引用 Services**（Tests 项目单独链接 Core 源码）。因此磁盘存储经 `ILayoutStore` 注入，
> 隐藏窗口/钩子这类"纯 Win32"代码放 Core（与 `WinEventHook` 一致），系统集成的磁盘部分放 Services。

### 3.2 线程模型

| 线程 | 职责 | 说明 |
|---|---|---|
| `DisplayChangeListener` 专用线程 | 隐藏窗口 WndProc + 消息泵 | 收 `WM_DISPLAYCHANGE` / `WM_POWERBROADCAST` / `WM_WTSSESSION_CHANGE` / `WM_SETTINGCHANGE`。回调里**只抛事件**，不做重活。退出靠 `PostThreadMessage(WM_QUIT)`（同 `WinEventHook`）。 |
| 复用现有 `WinEventHook` 线程 | 捕获信号源 | 现有钩子已订阅 `EVENT_OBJECT_SHOW..NAMECHANGE`（含 `LOCATIONCHANGE`）+ `FOREGROUND`，还声明了 `MOVESIZESTART/END`。捕获只需在此基础上**多订阅一个消费者**，不另开钩子。 |
| `PersistenceEngine` worker（Timer/串行队列） | 捕获落库 + 还原 | 所有重活（枚举窗口、读写 store、`SetWindowPos`/`SetWindowPlacement`）都在这里，**绝不在钩子回调或 WndProc 线程同步执行**。仿 `Watcher` 的防抖 Timer。 |

store 自身线程安全（`lock` 或 `ConcurrentDictionary`）。

**单线程 actor（v2）**：worker 不是"几个 Timer + 共享 flag"，而是一条**串行事件队列**。`DisplayChangeListener`、`WinEventHook`、兜底 Timer、引擎的 mutation 通知都只**投递不可变事件**到该队列；epoch/状态机/捕获/还原全部在这一条 worker 上处理，杜绝跨线程读写冻结 flag 的竞态。每个事件携带一个 `TopologyEpoch`（拓扑签名变化时自增），过期 epoch 的捕获/还原直接丢弃。

---

## 4. 数据模型

```csharp
// Core/LayoutSnapshot.cs
public sealed record WindowRecord(
    // 身份
    string ProcessPath,     // 跨会话匹配：完整 exe 路径（小写）
    string ClassName,       // 窗口类名
    string TitleSig,        // 标题签名（见 §9：原标题或归一化后的稳定片段）
    // 几何（v2：monitor-relative 物理像素 + 锚定显示器 + DPI，避免绝对坐标在拓扑变化后失真）
    string MonitorId,       // 锚定的显示器稳定 id（P1 用分辨率+拓扑串，P3 升 CCD/EDID）
    int RelLeft, int RelTop, int RelRight, int RelBottom,  // 相对该显示器工作区左上角的物理像素矩形
    int Dpi,                // 捕获时窗口所在屏 DPI（防御性记录；P1 不做 remap，P3 余地）
    int ShowCmd,            // SW_SHOWNORMAL / SW_MAXIMIZE / SW_SHOWMINIMIZED
    // z-order 提示（同 DisplayKey 内的相对次序；P1 可省，P4 完善）
    int ZOrder);

public sealed record LayoutSnapshot(
    string DisplayKey,
    IReadOnlyList<WindowRecord> Windows,
    long CapturedUnixMs);   // 时间戳由调用方注入（Core 内不可用 Date.Now，沿用项目约定）
```

- **几何只存 `GetWindowRect` 的物理矩形 + `ShowCmd`**，刻意不存完整 `WINDOWPLACEMENT.rcNormalPosition`，绕开它在 PerMonitorV2 下的工作区坐标坑（见 §7）。
- **DPI 在本设计里是免费的**：只在**相同 DisplayKey** 下还原（同显示器集 ⇒ 同 per-monitor DPI），存的物理像素直接有效，无需跨 DPI 重算 —— 这是相对 PersistentWindows 的一处简化红利。

会话内运行态另持一份内存索引：`Dictionary<DisplayKey, Dictionary<IntPtr hwnd, WindowRecord>>`，捕获时刷新、还原时读取。

---

## 5. 事件检测层：`DisplayChangeListener`

仿 **`Services/HotkeyService.cs` / `Services/TrayIcon.cs` 的 message-window 模式**（非 `WinEventHook` —— 后者无 HWND）：专用后台线程 → `RegisterClassEx` + `CreateWindowEx`（**普通隐藏顶层窗口**，非 `HWND_MESSAGE`）→ `GetMessage` 泵。**退出走 `PostMessage(WM_CLOSE) → WndProc 内 DestroyWindow → WM_DESTROY → PostQuitMessage`**，并在 `DestroyWindow` **之前** `UnregisterPowerSettingNotification` / `WTSUnRegisterSessionNotification`，不能 `PostThreadMessage(WM_QUIT)` 直接掐泵（会漏掉注销）。

> **关键坑**：`SetWinEventHook` **收不到** `WM_DISPLAYCHANGE`（它是广播窗口消息）；而 message-only（`HWND_MESSAGE`）窗口**也收不到广播**。必须建**真·顶层隐藏窗口**才能收到。一个窗口可同时承接全部触发源：

| 消息 | 触发场景 | 备注 |
|---|---|---|
| `WM_DISPLAYCHANGE` | 分辨率 / 显示器增删 / 串流 VDD 上下线 | 主触发源 |
| `WM_SETTINGCHANGE` (`SPI_SETWORKAREA`) | 任务栏/工作区变化 | 轻量复位 |
| `WM_POWERBROADCAST` `PBT_APMRESUMEAUTOMATIC/SUSPEND` | 睡眠唤醒 | P2 |
| `WM_POWERBROADCAST` `PBT_POWERSETTINGCHANGE` (`GUID_MONITOR_POWER_ON`/`GUID_CONSOLE_DISPLAY_STATE`) | 显示器下电再上电（DPMS） | P2，需 `RegisterPowerSettingNotification` |
| `WM_WTSSESSION_CHANGE` `WTS_SESSION_UNLOCK` / `WTS_REMOTE_CONNECT` | 解锁 / RDP 重连 | P2，需 `WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION)` |

事件抛给 `PersistenceEngine`，统一进"还原"路径（带去抖与稳定等待，见 §8）。相比 PersistentWindows 走 WinForms `SystemEvents`，自建隐藏窗口更轻、与项目无 WinForms 依赖的风格一致。

---

## 6. 捕获引擎

**信号源**：`WinEventHook` 现仅订阅 `SHOW..NAMECHANGE`（含 `LOCATIONCHANGE`）+ `FOREGROUND`（[WinEventHook.cs:86](../Core/WinEventHook.cs)）；`MOVESIZESTART/END` 是 `DragSnap` 另起的钩子。捕获要么**扩 `WinEventHook` 增加第三组 `MOVESIZESTART/END` hook**，要么**自起一个捕获专用 hook**（更解耦，避免与现有消费者抢回调）。再加低频兜底 Timer。
**落库时机**：钩子只 `ScheduleCapture()`（防抖 ~400–800ms），worker 线程执行真正捕获。

捕获流程：
1. `WindowScanner.EnumerateTopLevel()` 拿候选；按 `WindowScanner.Classify` 丢掉系统外壳/用户忽略/cloaked/过小者；再丢掉**引擎接管集**（`Func<ISet<IntPtr>>` 注入，来自 `Watcher.GetTakenWindows`）与独占全屏窗口。
2. 对每个窗口取 `GetWindowRect` + `GetWindowPlacement().showCmd` + 身份，生成 `WindowRecord`。
3. 与内存索引里该 DisplayKey 的旧值比对，**有位移才更新**（仿 `WindowOps.Apply` 的 ±2px 容差）。
4. 标脏 → 防抖后由 `ILayoutStore.Save` 落盘（P3）。

**只在状态机 `Normal` 态捕获**；`Restoring`/`Settling` 态一律跳过（见 §8）。

---

## 7. 还原引擎 + `WindowOps` 扩展

新增 `WindowOps.RestorePlacement(IntPtr hWnd, WindowRecord r)`：

1. **最小化**：保持最小化，仅把目标矩形写入其 normal 位（可选），不弹出。
2. **最大化**：先 `SetWindowPos` 到目标显示器内的某点 → `ShowWindow(SW_MAXIMIZE)`，确保在正确屏最大化。
3. **普通**：若当前是 min/max 先 `SW_RESTORE`，再 `SetWindowPos(r.Rect, SWP_NOZORDER|NOACTIVATE|FRAMECHANGED)`。
4. **z-order**（P4）：按记录的相对次序逐个 `SetWindowPos(hWndInsertAfter=前一个)`。
5. **消黑框**：收尾 `RedrawWindow(RDW_FRAME|RDW_INVALIDATE|RDW_UPDATENOW|RDW_ALLCHILDREN)`，必要时 ±1px nudge。
   —— 正好实现 DESIGN §4 里仍 ⬜ 的 **Force redraw** 与 **微调(nudge)**，一举两得；米哈游启动器的大黑框就由这步消除。

**为什么不用完整 `WINDOWPLACEMENT` 还原**：`rcNormalPosition` 是工作区坐标，在多屏混 DPI / PerMonitorV2 下含义微妙，直接 `SetWindowPlacement` 易落到错误的屏。改为"`SetWindowPos` 定精确像素 + `ShowWindow` 定状态"，规避该坑。

**多趟还原**：部分窗口会在自己的 `WM_DISPLAYCHANGE` 处理里把自己挪回去。还原分 N 趟（默认 3 趟，间隔 ~300–500ms），每趟校验是否到位、对未到位者重试，设上限防拉锯（复用 `Core/ThrashPolicy.cs` 的思路或并入其策略）。

---

## 8. 状态机（核心正确性）

```
            display/power/session 事件
   Normal ───────────────────────────────► Settling
     ▲                                         │  收到事件即重置去抖计时
     │ 还原完成 + grace(~1s)                    │  指定静默窗口(~800ms)内无新事件
     │                                         ▼
  Restored ◄──────────── 多趟还原 ◄──────── Restoring
     │ 捕获恢复                          计算当前 DisplayKey；
     └──────────────────────────────►   有该 key 的快照 → 还原；无 → 直接回 Normal 并开始为新 key 捕获
```

**v2 规范化状态流**（单线程 actor 串行处理，见 §3.2）：
`Idle → Freezing → TopologyStable → Restoring → Settling → Idle`。
- `Freezing`：收到任一系统事件即进入并重置去抖；**立即停捕获**。
- `TopologyStable`：静默窗口（~800ms）内无新系统事件 → 重算 `MonitorService` 拓扑签名；**签名变了**才往下走还原，**没变**直接回 `Idle`（避免无谓还原）。
- `Restoring`：多趟还原（§7），还原写操作带 `PersistenceRestore` source + per-HWND suppression token。
- `Settling`：还原后 grace（~1s）吸收自还原产生的 `LOCATIONCHANGE`，再回 `Idle` 恢复捕获。

**不变式**：`Freezing`/`Restoring`/`Settling` 态**禁止捕获且丢弃（不排队）**捕获事件。否则"屏切回来 → Windows 打乱 → 捕获先于还原触发 → 用乱布局覆盖好快照"。
按 DisplayKey 分桶已能隔离不同拓扑（串流单屏布局存在另一个桶里，不会污染桌面多屏布局），但**同 key 切回**这一下必须靠冻结兜底。每个捕获/还原事件携带 `TopologyEpoch`，跨过一次拓扑变化的旧事件按 epoch 作废。

`Suspended` 态：用户在托盘/设置里暂停本功能时进入，钩子仍在但既不捕获也不还原。

---

## 9. 跨重启身份匹配 + 磁盘持久化（P3）

### 9.1 身份
- 会话内：`hWnd`。
- 跨会话：`(ProcessPath, ClassName, TitleSig)`。
  - `TitleSig`：多数应用标题含易变片段（文档名、计数）。策略：优先精确标题匹配；失配则退化到 `(ProcessPath, ClassName)` 匹配该进程的"第 k 个窗口"（按 z-order 序）。
  - **误匹配风险**（两个 Chrome 窗口）：同 `(path,class)` 多窗口时，按捕获时的次序对位，宁可少还原不可乱还原 —— 列为给 Codex 的重点。

### 9.2 存储
- `Services/LayoutStore.cs` 实现 `ILayoutStore`：`%LOCALAPPDATA%\Reframe\layouts\<safe-display-key>.json`，
  `System.Text.Json` 源生成（新增 `LayoutJsonContext`）+ 原子写（tmp→Move，仿 `ConfigStore.Save`）。
- 写入防抖（捕获稳定后 ~2s 落盘一次），避免高频写盘。
- 选 JSON 而非 LiteDB：与项目"无重依赖、留 AOT 余地、System.Text.Json 源生成"的既定决策一致。

### 9.3 启动还原
- 进程启动时按当前 DisplayKey 读盘，对已在运行的窗口做一次身份匹配还原；可配"启动时是否提示再还原"。

### 9.4 稳定 DisplayKey
- P1 用"分辨率 + 虚拟坐标 + 主屏标志"的有序拼接（与现有 `MonitorFilter` 按分辨率识别屏的模型一致，简单）。
- 局限：两块相同分辨率显示器对调位置可能混淆。P3 升级为 CCD API（`QueryDisplayConfig` → EDID/显示器设备路径）做稳定 id —— 给 Codex 评估是否前置到 P1。

---

## 10. 命名快照 + 热键（P4）

- 快照槽（如 0–9 或命名）独立于自动还原：`SnapshotStore`（同 `ILayoutStore` 风格）。
- 热键复用 `Services/HotkeyService` + `Config.Hotkeys` 字典；新增 action id：`CaptureSnapshotN` / `RestoreSnapshotN` / `PausePersistence`。
- 托盘菜单：立即捕获 / 立即还原 / 暂停。

---

## 11. 与无边框引擎协作（v2：source-tag + mutation scope）

仅靠"跳过 `_takeover` 集合"不足以避免双写竞态——同一轮 `WM_DISPLAYCHANGE` 下，接管集那一刻可能尚未更新（Codex 评审确认）。改用三层防护：

1. **`WindowOps` 写操作打 source 标签**：所有 `Apply/Restore/RestorePlacement` 带 `MutationSource ∈ {User, Takeover, PersistenceRestore}`，并把"刚写过的 (hWnd, source, 时戳)"登记进一个短 TTL 的 suppression 表。
2. **捕获层只认 `User` 来源**：捕获前查 suppression 表，**忽略由 `Takeover`/`PersistenceRestore` 触发的 `LOCATIONCHANGE`**——这同时解决"我们自己还原窗口又被自己当用户移动记下来"的自反馈（原计划漏点）。
3. **`Watcher` 暴露 mutation scope**：`BeginSystemMutation()/EndSystemMutation()`，引擎接管一个窗口的整段操作期间持有 scope；持久化 worker 在 scope 活跃期推迟该窗口的捕获/还原。

- 兜底仍保留：还原时跳过当前 `Watcher.GetTakenWindows()` 集合（次要防线，非唯一）。
- 注入方式：`PersistenceEngine` 构造接收 `Func<ISet<IntPtr>> getEngineOwned` + 订阅 `Watcher` 的 mutation 通知；`App` 在 `OnLaunched` 接线（紧挨 `Engine.Start()`）。

---

## 12. UI / 托盘

- 设置页新增"窗口位置记忆"分组：总开关、触发源勾选（显示变化/唤醒/解锁）、启动时还原 + 是否提示、忽略名单（复用 `IgnoredProcesses` 或独立）。
- 托盘：暂停/继续、立即捕获、立即还原。
- 仪表盘日志复用 `Watcher.Emit`/`LogExternal` 风格输出"已还原 N 个窗口 @ <DisplayKey>"。

---

## 13. 配置项（`AppConfig` 扩展）

```csharp
public bool WindowPersistenceEnabled { get; set; } = true;
public bool PersistOnDisplayChange { get; set; } = true;
public bool PersistOnResume { get; set; } = true;     // P2
public bool PersistOnUnlock { get; set; } = true;     // P2
public bool RestoreFromDiskOnStartup { get; set; } = false; // P3
public bool PromptBeforeStartupRestore { get; set; } = false;
public List<string> PersistenceIgnoredProcesses { get; set; } = new();
```
布局数据**不进 config.json**（写频不同），仅开关进 config。`Version` 字段做迁移。

---

## 14. 测试策略

Core 纯逻辑单测（仿现有 `Tests/*.cs`，目前 219 个）：
- `LayoutKeyTests`：从 `MonitorDesc[]` 算 key —— 顺序无关、拓扑变化敏感、串流单屏 vs 桌面多屏不同 key。
- `RestorePlanTests`：给定"已存快照 vs 当前窗口集"，产出正确的移动集；跳过引擎接管窗口；缺失窗口跳过；已在位窗口不动。
- `WindowIdentityTests`：跨会话匹配（同 path/class 多窗口的对位、标题失配退化）。
- `StateMachineTests`：`Settling`/`Restoring` 态拒绝捕获；事件风暴下去抖与稳定等待。

手动验证场景（写进 `verify/`）：
1. **串流回桌面**（用户主场景）：ZakoVDD 上线→下线，桌面窗口全部归位、启动器无黑框。
2. 显示器输入源 KVM 切走再切回。
3. 睡眠唤醒（P2）。
4. 锁屏解锁 / RDP 重连（P2）。
5. 改分辨率再改回。
6. 重启后启动还原（P3）。

---

## 15. 分阶段交付（完整范围，按序）

| 阶段 | 内容 | 价值 |
|---|---|---|
| **P1 会话内自动还原** | `DisplayChangeListener`(仅 `WM_DISPLAYCHANGE`) + 捕获 + 还原 + 状态机 + 内存态按 DisplayKey 分桶 + hWnd 身份 + 跳过引擎窗口 + 消黑框 + 设置开关/托盘暂停 | **直接解决用户日常痛点** |
| **P2 更多触发源** | 唤醒 / 显示器上电 / 解锁 / 工作区变化（同一隐藏窗口承接） | 覆盖睡眠/锁屏/RDP |
| **P3 跨重启持久化** | `ILayoutStore` 磁盘实现 + 跨会话身份匹配 + 启动还原(可提示) + CCD 稳定显示器 id | 真·PersistentWindows 等价 |
| **P4 命名快照 + 热键 + UI** | 快照槽 + 热键 + 管理页 + 逐窗口忽略 | 锦上添花 |

> PersistentWindows "代码量大头"恰在 P3 的跨重启身份匹配 + DB；P1 砍掉这部分即可 100% 覆盖"没重启、只是切了下显示器"的实际痛点，工作量小得多。本计划仍交付全量，只是把价值最高的 P1 前置。

---

## 16. 风险与待定问题（请 Codex 重点评审）

1. **DisplayKey 稳定性**：P1 用"分辨率+拓扑"够不够稳？两块同分辨率屏对调的概率与后果，是否值得把 CCD/EDID 稳定 id 前置到 P1？
2. **`WINDOWPLACEMENT` 取舍**：只存 `rect + showCmd`、放弃完整 placement，是否会丢失某些窗口（如吸附/平铺态）的还原保真度？
3. **多趟还原的时序与上限**：3 趟 / 300–500ms 是否合理？与引擎现有 `ThrashPolicy`（10s 窗口 / cap 3）如何统一，避免两套节流互相打架？
4. **隐藏窗口线程归属**：`DisplayChangeListener` 该独立开线程，还是复用 `WinEventHook` 那条已有消息泵线程（少一个线程，但职责耦合）？
5. **存储格式与写频**：JSON-per-DisplayKey + 2s 防抖是否足够；高频捕获下的写放大与磨损是否需要内存合并/批写。
6. **跨会话误匹配**：同 `(path,class)` 多窗口（多 Chrome/多 Explorer）按次序对位的误还原风险，是否需要更强的窗口指纹（如 GUID 写入 `SetProp`，但有副作用）。
7. **虚拟桌面**：是否应纳入 DisplayKey 或单独维度；不处理时跨桌面窗口被误抓的风险。
8. **z-order 还原**：保真度 vs 成本，P4 是否值得做完整 z-order，还是只保证"该置顶的置顶"。
9. **独占全屏游戏**：捕获/还原时如何稳健识别并跳过，避免把全屏游戏窗口挪坏。
10. **与现有去框引擎的时序竞争**：同一 `WM_DISPLAYCHANGE` 下，引擎的 hook/poll 与本功能的还原并发触发，跳过 `_takeover` 是否足以避免竞态（接管集在那一刻可能尚未更新）。
