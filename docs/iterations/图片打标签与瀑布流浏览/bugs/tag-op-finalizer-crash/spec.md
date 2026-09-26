---
date: 2026-09-26
agile_trace: true
---

# tag-op-finalizer-crash 实现规格（SPEC）

## 根因

### 转储三证据链（已确证）

1. **终结器线程栈 = `WinRT.IObjectReference.Finalize()`，其 IP 与事件日志出错偏移 `ucrtbase.dll + 0xa527e` 完全一致** → 崩溃点是 GC 终结器线程在跨线程 Release WinRT/XAML 对象（DependencyObject 族须在创建线程析构，RULE:26）。
2. `eeheap -gc` 报 GC 堆数 0（托管堆不可遍历）→ native 堆已损坏，符合「跨线程 Release 破坏 native 引用计数/堆」的形态。
3. 事件日志 `0xc0000409`（fastfail），应用日志零条目（终结器线程崩溃不经 `UnhandledException`，故「无日志闪退」）；历史同指纹：09-24 三次同偏移（其中一次已归因 SHFileOperationW/MTA 并修复）——同一族机制反复出现。

触发时机：连续运行 15h40m 后的「添加标签」操作 → 打标收尾必然触发侧栏/筛选条全量重建 → 大批 XAML 对象裸交 GC → 终结器批量退休与合成器换帧窗口叠加。

### 代码审计查明的缺口（A~E，均已核实行号）

| 缺口 | 位置 | 内容 |
|---|---|---|
| A（最强，打标收尾必命中） | `Views\TagSidebarConverters.cs` 30 处 `new SolidColorBrush`（仅 White/Transparent 两个静态缓存） | x:Bind 函数转换器侧栏每行每次求值都新建画刷；侧栏/筛选条在每个打标操作收尾全量重建（`MainViewModel.cs:1357/1459/1611/2630` + `TagSidebarViewModel.cs:151-152 Groups.Clear()/UndefinedTags.Clear()` + `MainViewModel.cs:2291 FilterChips.Clear()`），扫描期每 5s、扫描结束、筛选应用、组展开折叠、主题切换、配置保存也会触发。对照组：同族 `WaterfallConverters` 09-24 已改静态缓存 |
| B（强，拖拽打标） | `Views\TagSidebarControl.xaml.cs` `SetDropOverlay`（:184，调用点 :131/:153/:169） | 每次 DragOver 新建 DropOverlay 淡底/描边画刷，随后被透明画刷替换 → 旧画刷裸丢 |
| C（中，条件性） | `Views\WaterfallView.xaml.cs:263-264` / `:348`；`ViewModels\WaterfallViewModel.ResetFrom`（:95-102） | ① `TryEnqueue(BeginLoadThumbnail)` 与 `OnElementClearing → ReleaseVisuals` 的「先清后启」竞态（已回收 VM 被排队回调重新挂上缩略图，绕过退役队列）；② ResetFrom 整体丢弃旧 VM 却无显式 ReleaseVisuals 遍历，只依赖 ElementClearing 时序 |
| D（弱，低频但确实裸丢） | `Helpers\ImageSourceHelper.cs:78-89` / `:113-114`；`MainViewModel.cs:3436` 前后；`MainViewModel.cs:428-430` | ① retrySource/fallbackSource 失败路径未退役；② GIF 换源取消/异常路径未提交的 newSource 未退役；③ `EnsureFullResolutionAsync` 是「先 Retire 旧源、再 await 新源」的顺序隐患（LoadCurrentAsync 是「先提交后退役」，口径不一致） |
| E（机制稳健性） | `Helpers\ImageSourceRetirement.cs` `Drain()` | 纯计数窗口（KeepCount=8）：一次批量退役（Reset）立刻 Dispose 超窗的几十个源，只留最后 8 个，「余量」不由帧/操作边界保证 |

## 变更点清单

| 提交 | 内容 |
|------|------|
| `a36e426` | 画刷缓存化 + churn 埋点：新增 `Views\TagBrushCache.cs`（新文件）；`Views\TagSidebarConverters.cs` 30 处 `new SolidColorBrush` 全部改走缓存；`Views\TagSidebarControl.xaml.cs`（SetDropOverlay 注释 + 走缓存）；`ViewModels\MainViewModel.cs`（RebuildTagSidebar churn 埋点 + `LogSidebarBrushChurn`/两个基线字段/阈值常量 + D⑤ 换源顺序统一）；`scripts\tag-op-finalizer-verify.ps1`（新文件，验证脚本）。5 files changed, +864/−69 |
| `315ef72` | 图像源收口与退役窗口：`Helpers\ImageSourceRetirement.cs`（双缓冲 Drain + 硬上限兜底 + 统计快照）；`Helpers\ImageSourceHelper.cs`（`RetireUncommittedSources` + 失败路径退役）；`ViewModels\GalleryItemViewModel.cs`（视图代数 + ReleaseVisuals 自增 + `BeginLoadThumbnail(generation)`）；`ViewModels\WaterfallViewModel.cs`（ResetFrom 显式 ReleaseVisuals）；`Views\WaterfallView.xaml.cs`（入队捕获代数）。5 files changed, +164/−10 |

## 详细改动说明

### 1. 画刷缓存键设计（A+B）

- 新增 `Views\TagBrushCache.cs`（UI-only，放 `Views\` 下——`Helpers\` 新增 UI-only 文件须同步 Core csproj 的 `<Compile Remove>`，见 RULE:35）。
- **键 = 计算完成的最终 ARGB uint**（`Get(byte a, byte r, byte g, byte b)` / `Get(Windows.UI.Color)` / `Get(uint argb)` 三个入口）。取色语义完全由调用方计算，缓存不参与取色 → 不可能改变像素取值；主题差异天然分条目（`IsDarkTheme` 双值取色后颜色不同即不同键），**无「主题切换后拿到旧色」风险，也不依赖重建清理缓存**（明确不采用 (主题, 语义) 复合键）。
- UI 线程专用：x:Bind 求值与面板构建（`TagSidebarViewModel.Rebuild` / `TagFilterPanelControl` 代码构建）均在 UI 线程，故 `Dictionary` 无锁；`Debug.Assert(DispatcherQueue.GetForCurrentThread() is not null)` 兜底（与 `ImageSourceRetirement` 同约定）。
- 有界性：调色板 = HSL 组色相 + 少量固定色，条目上限 = 色数 × 主题数（实测本夹具 19 条，主题切换后再 +N）。
- 只缓存只读使用的画刷：全仓核对无 `.Color =` / 就地改 `Opacity` 的画刷消费（`Views/ViewModels/MainWindow` 全量 grep 为空），共享实例安全。
- 覆盖范围：`TagSidebarConverters` 全部取色函数（含 `FromHsl`，`WaterfallConverters.BadgeBackground` 经它取值）、`DropOverlayBackground/DropOverlayBorderBrush`（缺口 B 的三支：透明/淡底/描边全部走缓存）。`TagCatalogDialog.xaml` / `TagFilterPanelControl.xaml.cs` / `MainWindow.xaml`（筛选条与筛选按钮）**本来就只经 TagSidebarConverters 取色**（grep 核实：这些文件无独立 `new SolidColorBrush`），故随之一并覆盖。
- 不改：`WaterfallConverters` 的既有缓存（瀑布流卡片模板，09-24 已修）。

### 2. churn 埋点（第 4 条要求）

- 位置：`MainViewModel.RebuildTagSidebar` 的 `Rebuild()` 局部函数收尾（所有重建路径的唯一收口，含 DispatcherQueue 回投分支）。
- 输出：`sidebar:rebuild brushNew=N brushSinceLast=M callsSinceLast=C cache=K`。
  - `brushNew` = 本次同步重建窗口内的新建数；
  - `brushSinceLast` / `callsSinceLast` = 自上一次埋点以来的新建数 / 取画刷调用数——**churn 的真正判据**。x:Bind 函数求值发生在重建后的布局趟（ItemsRepeater 实现元素时），不在同步窗口内；实测一次打标收尾重建的全部求值都落在下一个埋点窗口里（首版只报同步窗口的 `brushCalls`，实测恒为 0-3，无法反映 churn，故改为累计口径）。
  - `callsSinceLast` 同时是**修复前同轮重建会新建的画刷数**：修复前每次调用对应一处 `new SolidColorBrush`（实测口径，不靠静态估算）。
  - `cache` = 缓存条目数（有界）。
- 阈值护栏：`brushSinceLast > 48`（`SidebarBrushChurnWarnThreshold`，冷启动首轮实测 10+9=19 条、留足余量）时补一行 `[侧栏画刷 churn 告警]`。**不用 `Debug.Assert`**：断言失败会弹模态对话框，在自动化走查/实机长跑里可能直接挂住进程（RULE:25 防弹精神）；落日志才是可观测且不阻塞的护栏。

### 3. 缩略图视图代数守卫（C①）

- `GalleryItemViewModel._visualGeneration`（int，仅 UI 线程读写）；`ReleaseVisuals()` 首行自增（幂等：重复调用只是继续自增，无副作用）；`internal int VisualGeneration` 只读暴露。
- `BeginLoadThumbnail(int generation)`：代数不符即返回（丢弃过期启动），再做既有幂等判定（cts/Thumbnail 均 null）。
- `WaterfallView.OnElementPrepared`：`var generation = vm.VisualGeneration;` → `TryEnqueue(() => vm.BeginLoadThumbnail(generation))`。
- 正常路径不受影响（ElementPrepared 与 ReleaseVisuals 交替时，最后一次入队携带最新代数）；卡片重新 Realize 时 ElementPrepared 带新代数再次入队，恢复路径照旧（ThumbnailService 缓存兜底）。

### 4. 双缓冲退役推导（E）

设某次 `Drain` 入口处队列 = [已跨过上次边界的 S 条（头部）] + [自上次 Drain 起新入队的 N 条（尾部）]：

1. 本轮只从头部 S 段释放超出 `KeepCount` 的部分（FIFO：最老的先走，最新 `KeepCount` 条继续留）；
2. 尾部 N 条一律不释放——它们尚未跨过任何边界，至少活到下一次 Drain；
3. 收尾把剩余全部条目计入边界窗口（`_seenCount = _pending.Count`），下一轮它们成为「可释放」的 S 段。

由 ③ 归纳：任一源从入队到被 Dispose 至少经历一次 Drain 边界——批量退役（如 `ResetFrom` 逐项 `ReleaseVisuals`）不再出现「只留最后 8 条、其余立即 Dispose」。有界性：③ 保证窗口内保留 ≤ KeepCount 条，加上本轮新增 N 条，峰值 ≤ KeepCount + N。

> 2026-09-26 订正（xaml-finalizer-residuals）：上文「一次操作边界 = 换帧窗口」的表述改为如实口径「**跨一次 Drain 调用边界**」；批量路径的逐项 Drain 已在 `feefa0e` 改为「逐项 deferDrain + 集合替换后统一一次 Drain」，逐项 Drain 的原文口径对批量路径不成立。详见 `bugs/xaml-finalizer-residuals/spec.md`。
另加硬上限 `HardCap = 512`（防御性）：`Retire`/`Drain` 约定成对调用（全部调用点已配对），若未来出现只 Retire 不 Drain 的路径，超限按 FIFO 释放最老条目并落一条诊断日志（约定破坏在 startup.log 可见）。新增 `Snapshot()`（入队数/释放数/队列长/窗口长）供诊断。

### 5. 未提交源收口（C②/D）

- `WaterfallViewModel.ResetFrom`：替换旧集合前 `foreach (var old in Items) old.ReleaseVisuals();`（顺序不可颠倒：替换后旧 VM 已无引用可遍历）。
- `ImageSourceHelper.FromLoadedImageAsync`：`source`/`retrySource` 提升到 try 外声明；首个 Set 失败 + 副本重试失败时经 `RetireUncommittedSources(source, retrySource)` 退役；**退役放 finally 且晚于副本重试**——`SoftwareBitmapSource.Dispose()` 有连带关闭其呈现过位图的可能（RULE:26 附注），必须等重试读完 `decoded`。兜底路径 `fallbackSource` 失败同样退役。
- `MainViewModel.LoadCurrentAsync`：新增 `ImageSource? pendingSource` 槽位（提交成功即置 null），`finally` 统一退役未提交源——覆盖 GIF 等 `ImageOpened` 期间被取消（`WaitForGifSourceOpenedAsync` 返回后 `ThrowIfCancellationRequested`）、改名重试、通用异常三条路径。
- `MainViewModel.EnsureFullResolutionAsync`：改为「先造新源 → 提交（`ImageSource`/`_currentLoaded`）→ 再退役旧源」；未提交的新源在 finally 退役。原实现先 Retire 旧源再 await 新源，await 抛异常时旧源仍在显示却已排进处置队列（下一次 Drain 会在显示中 Dispose 它）。

## 测试策略

### 单测与构建

- `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → **120/120 通过**（基线 120；本轮未改可测逻辑，改动集中在 UI 工程与 UI-only Helper）。
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` → 通过（0 错误 0 警告；末次一轮通过）。
- 顺序：先 tests、后 build.ps1。

### 验证脚本（`scripts\tag-op-finalizer-verify.ps1`，可复跑）

- `-Phase setup`：造两个夹具图库（10 张带标签文件名，含 2 个未定义标签）+ 备份 settings.json（带残留备份前置守卫）。
- `-Phase color`：部署版 v1.0.5 与 Debug 构建同状态截屏比对；每主题跑三遍（release / debug / **release 对照复跑**）以剔除瞬态假差异。
  - 截图口径 **PrintWindow(PW_RENDERFULLCONTENT)**：屏幕上有第三方悬浮窗口叠在窗口左下角，`CopyFromScreen` 会被遮挡污染（实测混入其他应用内容并带自身动画）。
  - 状态归一：UIA `SetFocus` 中和焦点 + ScrollViewer `ScrollPattern` 复位侧栏滚动 + 点桌面使窗口失活（WinUI 失活窗口不画系统焦点框）+ 等悬浮滚动条淡出 + 双次截屏一致才采信（`Wait-Settle`）。
- `-Phase churn`：开图库 → UIA「全选」→ 注入快捷键 `1`（settings 里临时加 `Number1 → applyTag(喜欢)` 绑定）批量打标 → 抓 `sidebar:rebuild` 行；随后组折叠/展开各一次作为额外重建样本。
- `-Phase chain`：单图加/移除标签、返回图库、筛选点击、组折叠/展开。
- `-Phase restore`：还原 settings.json（已执行，settings 已回原值：theme=Dark、root=F:\Pictures\…、7 shortcuts、2 groups）。

### 实测数据（2026-09-26）

**churn（Debug 构建，夹具 10 张 / 2 组 / 12 标签行；`%LocalAppData%\SimpleViewer\logs\startup.log`）**

```
[2026-09-26 16:51:24.051] sidebar:rebuild brushNew=0 brushSinceLast=0  callsSinceLast=0   cache=0     ← 会话首个重建（冷）
[2026-09-26 16:51:24.157] sidebar:rebuild brushNew=0 brushSinceLast=10 callsSinceLast=16  cache=10    ← 调色板首次成型
[2026-09-26 16:51:24.527] sidebar:rebuild brushNew=0 brushSinceLast=9  callsSinceLast=64  cache=19    ← 调色板完整（19 色）
[2026-09-26 16:51:29.303] sidebar:rebuild brushNew=0 brushSinceLast=0  callsSinceLast=151 cache=19    ← 批量打标收尾
[2026-09-26 16:51:37.480] sidebar:rebuild brushNew=0 brushSinceLast=0  callsSinceLast=55  cache=19    ← 组折叠
[2026-09-26 16:51:40.660] sidebar:rebuild brushNew=0 brushSinceLast=0  callsSinceLast=39  cache=19    ← 组展开
```

调色板成型后**每轮重建 brushNew=0 / brushSinceLast=0**（churn 归零），cache 恒 19（有界）。单图打标链路（16:53 会话）同样：单图加标签 151、移除 55、回图库 57、筛选应用 63（+3 新色，筛选激活引入 accent 系新颜色）、组展开 28，均 brushNew=0。

**修复前同轮重建会新建的画刷数量（算法）**：`callsSinceLast` 即实测值（每次调用对应修复前一处 `new SolidColorBrush`）。静态上界算法 = 每标签行 6 个取色函数（TagRowBackground / TreeLineBrush / RadioDotFill / RadioDotStroke / TagRowNameForeground / TreeCountForeground）× 12 行 = 72，组头行 4 × 2 = 8，侧栏标题 2，筛选条 1 + chip ≈ 4，合计 ≈ 87；实测 39-63（低于上界：ItemsRepeater 只对重新实现的元素求值，未实现的元素不重新求值）。取实测值 55（组折叠）与 63（筛选应用）：**修复前每次这类重建即新建 55-63 个 `SolidColorBrush` 并裸交 GC**；打标收尾窗口（含侧栏重建 + 布局求值 + 卡片角标）151 个。

**颜色回归（硬验收，RULE:56 屏幕像素采样）**

| 主题 | 状态 | 侧栏区 420×900（378000 px） | 顶栏+筛选条区 1180×260 | 整窗 1600×900 |
|---|---|---|---|---|
| Dark | A（未筛选） | release vs debug **diff=0**；release vs release2 diff=0 | 0；0 | 30（缩略图 ±1 噪）；0 |
| Dark | B（筛选激活） | **diff=0**；0 | 37；4 | 121；4 |
| Dark | C（组折叠） | **diff=0**；0 | 37；4 | 121；4 |
| Light | A | **diff=0**；0 | 0；0 | 64；34 |
| Light | B | **diff=0**；0 | 27；24 | 99；82 |
| Light | C | **diff=0**；0 | 27；24 | 99；82 |

- **侧栏区（画刷转换器的作用面）逐像素 diff=0**：深/浅两主题 × 三状态 × （v1.0.5 vs 修复版）全部为 0。
- 顶栏+筛选条区在 B/C 状态有 18-37 px 的 ±1 通道差：**同二进制对照复跑同样出现（4-27 px）**，为半透明筛选条叠在 Mica 背板上的运行间底噪（不是颜色转换差异）。
- 整窗残差集中在瀑布流缩略图的 ±1 通道差（对照复跑同量级）。
- 语义锚点逐点 RGB（窗口内坐标；release=v1.0.5，debug=修复版）**全部 SAME**：

| 主题/状态 | 锚点 | 坐标 | RGB |
|---|---|---|---|
| Dark/A | 组头行_互斥组 / 标签行_未激活 / 无标签按钮 | (210,195) / (210,514) / (352,138) | 39,39,46 / 39,39,46 / 143,143,144 |
| Dark/B | 标签行_激活 / 筛选条 chip | (210,350) / (585,179) | 65,65,71 / 0,120,212 |
| Dark/C | 未定义 chip / 未定义区标题 / 组头行_兼容组_折叠 | (210,562) / (210,522) / (210,462) | 255,255,255 / 39,39,46 / 39,39,46 |
| Light/A | 组头行_互斥组 / 标签行_未激活 / 无标签按钮 | 同上 | 243,243,248 / 243,243,248 / 127,126,126 |
| Light/B | 标签行_激活 / 筛选条 chip | 同上 | 222,222,227 / 0,120,212 |
| Light/C | 未定义 chip / 未定义区标题 / 组头行_兼容组_折叠 | 同上 | 25,25,25 / 243,243,248 / 243,243,248 |

**打标链路回归（实机）**

- 单图加标签：双击 `photo_8.jpg` 进单图 → 快捷键 `1`（toggle 加）→ 文件改名 `photo_8[喜欢].jpg`，右栏「标签」区出现「喜欢 ✕」chip（右栏 chip 区像素差 4462）；图片信息行未重载（显示名仍 `photo_8.jpg`）。
- 单图移除标签：右栏 chip ✕（UIA Invoke）→ 文件还原 `photo_8.jpg`，chip 消失（像素差 4462）。
- 批量打标：全选 10 张 → 快捷键 `1` → 10 个文件全部改名（互斥组语义：`灵魂/一般` 被替换为 `喜欢`，负面标签保留并按字母序重排：`photo_3[大水印 喜欢].jpg` 等），侧栏计数同步更新（喜欢程度 7→10、灵魂 1→0、一般 1→0、喜欢 5→10）。
- 标签筛选点击：侧栏「喜欢」行激活高亮 + 筛选条出现 chip（✕）+ 工具栏筛选按钮进入强调态（`FilterToggleButton` 存在、chip ✕ 存在）。
- 组展开折叠：折叠「负面标签」→ 8 行收起、未定义标签区（临时标记 1 / 陌生标签 1）进入视野；再展开恢复（像素差 47670）。
- **拖拽打标**：RULE:57 明确拖拽手势无法自动化注入，本机未做注入验证——仅做代码审查 + 静态保证：`SetDropOverlay` 三支取值全部经 `TagBrushCache`（缺口 B 的修法），`OnTagRowDragOver/DragLeave/Drop` 调用链未改语义。**留用户实机复测**（交付物为本地 Debug 构建）。
- 日志检查：本轮 `startup.log` **零新增异常条目**（16:20 后仅 `sidebar:rebuild` 与 `[GC 采样]` 行），亦无 `[侧栏画刷 churn 告警]`。

## 已知残留（低价值裸丢点，本轮未动）

- `MainWindow.xaml.cs:238`：每次打开筛选 Flyout `new Style(typeof(FlyoutPresenter))`（低频，一次一对象）。
- `Views\SingleImageView.xaml.cs:342`：每次文件名刷新 `new Run`（低频；`Run` 属 DependencyObject 族，理论上同族，但每次一个、无批量退休场景）。
- `MainWindow.xaml.cs:479/517/563/603/634/660/700/769`：每次弹窗 `new ContentDialog`（用户交互触发、一次一个，且 ContentDialog 生命周期由框架持有到关闭）。
- XAML 模板内的框架托管对象（`MenuFlyout`/`MenuFlyoutItem`/模板内 Border 等）：由 XAML 框架创建与回收，代码侧无法介入退役——属既有边界。
- `WaterfallConverters` 保留自己的私有画刷缓存（09-24 先例，本轮按「不改瀑布流卡片模板既有缓存」要求未动；与 `TagBrushCache` 无冲突，色值不重叠或为同一实例）。

## 风险与回滚方案

| 风险 | 评估 | 缓解 |
|---|---|---|
| 缓存键取错导致颜色漂移 | 已用逐像素比对封堵：侧栏区 diff=0（两主题 × 三状态） | 键 = 最终 ARGB（不参与取色）；任何取色函数改动后重跑 `-Phase color` 即暴露 |
| 画刷实例被多处共享后出现串改 | 全仓核对无 `.Color =` / 就地改属性；画刷只读消费 | 新增消费方若需改写须先 `new` 副本（TagBrushCache 注释已写明只读约定） |
| 双缓冲 Drain 让退役源多活一轮，峰值内存上升 | 峰值 ≤ KeepCount + 本轮新增；批量场景（ResetFrom 逐项 ReleaseVisuals）中每次 Drain 都在推进释放 | 硬上限 512 兜底 + `Snapshot()` 诊断；实测长跑未见内存异常（本轮未做长跑压测，留用户实机） |
| 视图代数守卫误杀正常加载 | 只在「代数不符」时返回；重新 Realize 会带新代数再次入队 | 实机回归：开图库滚动/筛选/单图切换缩略图正常（本轮走查未见过早丢载） |
| 未提交源退役误伤仍在使用的位图 | `SoftwareBitmapSource.Dispose` 有连带关闭位图可能（RULE:26 附注） | 退役一律放在「确认未提交」路径（提交即置 null）；副本重试场景把退役放到重试之后 |
| 回滚 | 两个提交各自独立可回滚 | `git revert 315ef72`（图像源侧）与 `git revert a36e426`（画刷侧）互不依赖；churn 埋点随 ① 一起回滚（仅诊断，无功能依赖） |
