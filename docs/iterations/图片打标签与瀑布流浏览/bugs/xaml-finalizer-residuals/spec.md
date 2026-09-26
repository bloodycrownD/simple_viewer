---
date: 2026-09-26
agile_trace: true
---

# xaml-finalizer-residuals 实现规格（SPEC）

## 根因

### 总机制（沿用 tag-op-finalizer-crash 已确证的转储铁证）

XAML 依赖对象必须在创建线程（UI STA）析构；CsWinRT 的 `WinRT.IObjectReference.Finalize` 在 GC 终结器线程直接 native Release，成批退休时触发 native 堆损坏（fastfail `0xc0000409 @ ucrtbase+0xa527e`，终结器栈实测；另一表现为 XAML stowed `0xc000027b`）。修法基线是 `Helpers\ImageSourceRetirement.cs` 退役队列（UI 线程 Retire/Drain），但本轮扫描发现它对**最大 churn 源 BitmapImage 实际上无效**，且链条本身有四处结构性缺口。

### P0-1（最大残留）BitmapImage 退役是空操作

- `ImageSourceRetirement.Release` 是全域唯一 Dispose 点：`(source as IDisposable)?.Dispose();`（修复前 :113-117）。
- **WinUI 3 投影中 `BitmapImage`/`BitmapSource`/`ImageSource` 均不实现 IClosable/IDisposable，只有 `SoftwareBitmapSource` 有 Close/Dispose**——该表达式对 BitmapImage 恒为 null：队列只把对象多持有几个 Drain 边界，之后仍由终结器线程 native Release。
- 主要 churn：`GalleryItemViewModel.LoadThumbnailAsync` 每张卡片每次 Realize `new BitmapImage()`（滚动/筛选/`ResetFrom` 批量回收是主力）；`Helpers\ImageSourceHelper.cs:30` 的 GIF 单图源为低频豁免项（本轮未动，见已知残留）。

### P0-2 缩略图未提交源漏网

修复前 `bitmap` 是内层 `try` 局部变量，外层 `catch` 不可见：在 `await bitmap.SetSourceAsync` 窗口内被 `ReleaseVisuals` 取消或抛异常时，该实例无任何处置（裸交 GC）。与 `MainViewModel.LoadCurrentAsync` 的 `pendingSource` 同构缺口。

### P0-3 退役链无异常隔离

`Release`/`Drain` 无 try/catch，`ReleaseVisuals`/`ResetFrom` 批量回收也无兜底：任一 `Dispose()` 抛出会中断整批回收（剩余 VM 从未释放 → 其 Thumbnail 裸丢），且异常可能上传到 XAML 回调（stowed `0xc000027b`）。

### P0-4 未释放者被统计成已释放

`Release` 末尾无条件 `_releasedCount++`：对 BitmapImage（no-op）也记成已释放，会让池化后应恒为 0 的回归判据假绿；`Snapshot()` 也不区分两类。

### P0-5 批量退役的「边界」名不副实

`ResetFrom` 逐项 `ReleaseVisuals()`，每次内部 Retire+Drain——每个 Drain 都算一次边界：一次 1000 项重置会在**同一同步循环内** Dispose 掉除最后约 9 个之外的全部源；注释声称的「至少经历一次换帧边界」在批量路径上不成立（口径本身也不准确：边界是「跨一次 Drain 调用」而非「换帧窗口」）。

### P1-6 低速但落在热路径的一次性 XAML 对象

- `Views\SingleImageView.xaml.cs`：每次显示名变化 `Inlines.Clear()` + `new Run`（打标改名即触发）；
- `MainWindow.xaml.cs`：每次开筛选 Flyout `new Style` + 4 个 Setter；
- `Views\TagFilterPanelControl.xaml.cs`：每次点击 `RebuildTree()` 整树重建（40~300 个代码创建 XAML 对象）——**本轮明确不重构**，改为有界保命环。

### P1-7 资源卫生（实证修正：不可实施）

原扫描结论称 `Services\ImageLoaderService.cs:54`、`Services\ThumbnailService.cs:238/:259` 的 `BitmapDecoder`/`BitmapEncoder`「Agile 且可 Dispose」。实证推翻：

- 按 `using` 改造后编译失败：`error CS1674: "BitmapDecoder": using 语句中使用的类型必须可隐式转换为 "System.IDisposable"`（两处）与 `error CS1061: "BitmapEncoder"未包含"Dispose"的定义`；
- 对 `C:\Users\BloodyCrown\.nuget\packages\microsoft.windows.sdk.net.ref\10.0.19041.56\lib\net8.0\Microsoft.Windows.SDK.NET.dll` 做元数据探测（PEReader）：`BitmapDecoder`/`BitmapEncoder` 不实现 `System.IDisposable`、无 `Close`/`Dispose` 成员，`Windows.Foundation.IClosable` 未投影；对照 `SoftwareBitmap`/`IRandomAccessStream` 均实现 `System.IDisposable`。
- 结论：**显式释放不可实施**（只能裸调 COM vtable，风险大于收益）；二者非 XAML DependencyObject，GC 终结安全，不属本轮崩溃机制残留。改为代码注释注明。

### P2-8 可观测性缺口

`ImageSourceRetirement.Snapshot()` 全仓零调用；池化后也无池计数埋点——长会话趋势不可见。

## 变更点清单

| 提交 | 内容 |
|------|------|
| `feefa0e` | 缩略图池化：新增 `Views\ThumbnailImagePool.cs`（新文件）；`ViewModels\GalleryItemViewModel.cs`（LoadThumbnailAsync 池取 + 未提交 finally 还池 + ReleaseVisuals 还池/deferDrain/异常兜底）；`ViewModels\WaterfallViewModel.cs`（ResetFrom 逐项 deferDrain + 统一一次 Drain）；`Views\WaterfallView.xaml.cs`（DragUI 实例 ExcludeFromPool）。4 files changed, +269/−35 |
| `94fffaa` | 退役链：`Helpers\ImageSourceRetirement.cs`（Release/Drain 异常隔离、Released/Undisposable 分计、首次不可 Dispose 源一次性说明日志、Snapshot 扩字段、边界措辞订正）。1 file changed, +91/−24 |
| `23eac32` | 视图侧与埋点：`Views\UiKeepAlive.cs`（新文件）；`Views\SingleImageView.xaml.cs`（直写 Text）；`MainWindow.xaml.cs`（Style 按宽缓存）；`Views\TagFilterPanelControl.xaml.cs`（保命环接入）；`ViewModels\MainViewModel.cs`（retire/pool 埋点）；`Services\ImageLoaderService.cs`/`Services\ThumbnailService.cs`（P1-7 实证修正注释）。7 files changed, +147/−12 |

## 详细改动说明

### 1. BitmapImage 池设计与 DragUI 排除（P0-1）

- 新增 `Views\ThumbnailImagePool.cs`（UI-only，放 `Views\` 下——`Helpers\` 新增 UI-only 文件须同步 Core csproj 的 `<Compile Remove>`，RULE:35；Views 由主工程默认 glob 编入，无需动 csproj）。
- 结构：空闲实例栈 `Stack<BitmapImage>` + 「已交 DragUI」引用集 `HashSet<BitmapImage>(ReferenceEqualityComparer.Instance)`；硬上限 `PoolCapacity = 64`。
- `Acquire()`：池空则 `new`（计 `Created`）、否则弹栈并兜底 `UriSource = null`（清源失败则弃用该实例转退役并新建）；`Return(BitmapImage)`：排除集命中 → 转退役队列一次性处置（计 `Excluded`）；否则 `UriSource = null`（释放已解码纹理）后入池，池满/置空失败 → 转退役队列。**溢出与置空失败一律转退役而非裸交 GC**（比需求原文的「交 GC」更严，保持 RULE:26 不破）。
- 仅 UI 线程访问（`Debug.Assert(DispatcherQueue.GetForCurrentThread() is not null)` 兜底，Release 静默——与 `ImageSourceRetirement`/`TagBrushCache` 同约定）；计数 `Acquired/Returned/Created/Pooled/Excluded` 经 `Snapshot()` 供埋点。
- 线程/寿命语义：池实例寿命 = 进程寿命（有界 ≤64 个），终结器永不运行；同一实例不可能同时被两个卡片持有（只有已归还的实例在池中）。
- DragUI 排除：`WaterfallView.OnCardDragStarting` 回退链用缩略图 BitmapImage 前调 `ExcludeFromPool(fallback)`——拖拽会话可能仍引用该位图，重置 `UriSource`/复用会破坏跟随视觉；该实例自此退出池生命周期，卡片回收时 `Return` 将其转退役队列（UI 线程延迟 Dispose，保留窗口覆盖拖拽会话即时引用）。

### 2. 还池时机（P0-1/P0-2）

- `LoadThumbnailAsync`：`pendingBitmap` 提升到外层 try 外声明；Acquire 后设 `UriSource`（缓存文件缺失走既有 `SetSourceAsync` 分支，设置前先 `UriSource = null`）；提交点 `pendingBitmap = null; Thumbnail = committed;`；**未提交路径统一在 `finally` 还池**（取消 / SetSourceAsync 异常 / 闸门取消 / 应用段异常）。
- `ReleaseVisuals(bool deferDrain = false)`：先 `previous = Thumbnail` 并从界面断开（`Thumbnail = null; _dragVisual = null;`），再 `BitmapImage → Return` 还池、非 BitmapImage 源（SoftwareBitmapSource 等）→ `Retire` 退役（**有效路径不动**）；释放链异常 catch 落日志，`finally` 内按 `deferDrain` 决定是否 `Drain`——任一步失败都不中断批处理（P0-3）。
- 未改：单图路径（`ImageSourceHelper`/`MainViewModel`）的 SoftwareBitmapSource 退役链、GIF BitmapImage 退役（走队列、且是 `undisposable` 计数的首个样本）。

### 3. 异常隔离与双计数（P0-3/P0-4）

- `Release`：整体 try/catch——`IClosable` 真 Dispose 成功计 `Released`；非 IClosable 计 `Undisposable`，**首次遇到**落一条含类型名的说明日志（一次性闸门，不刷屏）；Dispose 抛出计失败数并落日志后继续。
- `Drain`：循环整体 try/catch 兜底（Release 已自隔离，这层覆盖队列操作本身的意外）；异常后收尾仍执行 `_seenCount = _pending.Count`，队列状态不丢。
- `Snapshot()` 扩展为 `(Retired, Released, Undisposable, Pending, Seen)`；字段语义写入类注释「计数口径」，防止日后把 `Undisposable` 读成 `Released`。

### 4. 批量边界（P0-5）

- `WaterfallViewModel.ResetFrom`：逐项 `ReleaseVisuals(deferDrain: true)`（只还池/退役不 Drain）→ 集合与路径集替换 → **统一 `Drain()` 一次**。
- 注释口径订正：`ImageSourceRetirement` 与调用侧注释中「一次操作边界 = 合成器换帧窗口」如实改为「跨一次 **Drain 调用**边界（调用点通常是一次操作收尾/换帧窗口）」；旧 spec（`bugs/tag-op-finalizer-crash/spec.md` §4）加一行日期订正脚注。

### 5. 保命环语义与内存代价（P1-6）

- 新增 `Views\UiKeepAlive.cs`：UI 线程、FIFO、容量 4 的强引用环；`Hold(object?)` 入环，超容量放走最老项。
- 接入点：`TagFilterPanelControl.RebuildTree` 在 `TreeHost.Children.Clear()` 前把旧树根（`Children[0]`）交给它。
- **语义如实声明：这是「不释放」而非「正确释放」**——被摘下的 XAML 子树仍会进入终结器流程，本类只把「整棵树同一瞬间失去引用」摊成「每次只放走一棵」，属有界内存换安全的止血措施，不是根治；正确释放需要元素级确定性拆解（面板整树重建属既有架构，本轮按需求不重构）。代价：最多同时多保 4 棵已摘下子树（树内画刷已经 `TagBrushCache` 静态复用，额外常驻为树结构壳与文本对象，量级 KB~百 KB）。

### 6. 视图侧低速裸丢（P1-6）

- `SingleImageView.RebuildFileNameInlines`：删 `Inlines.Clear()` + `new Run`，直写 `InfoFileNameText.Text`（该 Run 无独立样式，呈现等价）；移除 `using Microsoft.UI.Xaml.Documents;`。
- `MainWindow`：新增字段 `_filterFlyoutStyle` / `_filterFlyoutStyleWidth`（NaN=未缓存）+ `GetOrCreateFilterFlyoutStyle(width)`——宽度未变复用同一 Style（Style 不可变，共享安全），宽度变化才重建（窗口 resize 后仍正确）。

### 7. 埋点（P2-8）

- `MainViewModel.RebuildTagSidebar` 的既有 churn 埋点旁新增 `LogRetirementSamples()`：
  - `retire: retired=X released=Y undisposable=Z pending=N seen=M`；
  - `pool: acquired=A returned=R created=C pooled=P`。
- 仅在计数较上次采样变化时落行（首个采样必落）；`_lastRetireSample`/`_lastPoolSample` 为静态可空元组基线。稳态判据：`pool.created` 稳定在池容量/视口峰值量级、`retire.undisposable` 只应来自 GIF 等豁免源。

## 测试策略

### 无头实测（本轮全部可执行验证，按用户要求未启动任何 GUI）

1. `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug`：先跑基线（改动前）120/120；实现后重跑 **120/120**（`已通过! - 失败: 0，通过: 120，已跳过: 0，总计: 120`）。中途 P1-7 的 `using` 尝试编译失败（CS1674/CS1061，见根因节），实证后回退为注释。
2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1`：`[build] 成功（第 1 次尝试）`（前置还原 --force + `-m:1 -nr:false`）。本轮 XAML 侧只改 code-behind（未动 .xaml），未触发 MSB3073。
3. 代码级自检 grep 证据（提交后全仓）：
   - `new BitmapImage` 仅 `Views\ThumbnailImagePool.cs:75/90`（池内新建）——`GalleryItemViewModel` 只剩注释提及；`Helpers\ImageSourceHelper.cs:30` 为 GIF 分支豁免；
   - `ThumbnailImagePool.` 调用点：`GalleryItemViewModel:284/384/426`（ReleaseVisuals 还池 / Acquire / 未提交 finally 还池）、`WaterfallView:236`（DragUI 排除）、`MainViewModel:2378`（埋点）；
   - `ReleaseVisuals(deferDrain: true)` 在 `WaterfallViewModel:113`，统一 `Drain()` 紧随集合替换；
   - `ImageSourceRetirement`：`Release` 内 `source is IDisposable` 分派 + `_undisposableCount`/`_undisposableLogged` + try/catch；`Drain` 整体 try/catch；
   - `new Run`/`new Style` 残留：`SingleImageView`/`MainWindow` 已无（`MainWindow:264` 的 `new Style` 在 `BuildFilterFlyoutStyle` 内，仅宽度变化时调用）；`TagFilterPanelControl.MakeRun`（表达式预览段着色）为既有独立用途，不在本轮清单；
   - `UiKeepAlive.Hold` 在 `TagFilterPanelControl.xaml.cs:89`，紧邻 `TreeHost.Children.Clear()`。
4. 未执行：`scripts\tag-op-finalizer-verify.ps1` 等既有 GUI 验证脚本（本轮不弹窗口）；未动 `release\`、`E:\App\Others\viewer`；未 tag/push/改 CHANGELOG。

### 待实机项（无法静态证明）

- 池化后缩略图重载路径的视觉正确性（滚动回收再实现、快速滚动、筛选重置）；
- DragUI 拖拽跟随视觉（拖拽手势无法自动化注入，RULE:57 环境限制）；排除实例在拖拽会话后是否正确转入退役；
- 长会话（≥2h）终结器压力是否真正下降——`pool.created` 有界 + 无 fastfail 是最终判据；
- 保命环对筛选面板连续编辑的稳定性（是否仍有 stowed 窗口外的低概率风险）。

## 已知残留

- **P1-7 不可实施**：BitmapDecoder/BitmapEncoder 在该投影下无 IClosable（详见根因节实证）；显式释放需手写 COM vtable 调用，风险大于收益，明确不做。
- **GIF BitmapImage 豁免**：`ImageSourceHelper.FromLoadedImageAsync` 的 GIF 分支仍 `new BitmapImage` + 退役队列（无 Dispose 成员）——低频（每次单图 GIF 查看一次）；`retire.undisposable` 会因此非零，`[退役队列] …无 IClosable…` 首次说明日志即它的样本；后续若要归零需给 GIF 源也接池（需处理 UriSource 生命周期与 `ImageOpened` 等待，本轮不动）。
- **`_dragVisual` 属 agile SoftwareBitmap 豁免**：GC 终结合法，`ReleaseVisuals` 只断引用（既有口径，本轮未动）。
- **ContentDialog 与 XAML 模板内 MenuFlyout/非虚拟化 ItemsControl**：框架托管对象，代码侧无法介入退役，属既有边界（上轮 spec 已列，本轮未动）。
- **筛选面板整树重建**：保命环只是摊薄终结时刻（有界内存换安全），不是正确释放；根治需增量更新/复用面板树，属后续迭代。
- **DragUI 排除实例的处置**：转退役队列（保留窗口后 Dispose）；若拖拽会话远超保留窗口仍引用该位图，理论上存在竞态——属低概率残留，实机拖拽验证覆盖。

## 风险与回滚方案

| 风险 | 评估 | 缓解 |
|---|---|---|
| 池实例被复用后仍被旧卡片引用（错图/闪烁） | 复用只发生在实例已还池（界面引用已断）之后；提交点 `pendingBitmap = null` 保证单次所有权 | 实机滚动/筛选回归（待主代理）；异常时 `ReleaseVisuals` 幂等，仅 `Thumbnail` 字段持有者可归还 |
| 池实例 `UriSource` 置空导致仍显示的卡片掉图 | 还池前先 `Thumbnail = null`（绑定已断开） | 顺序写在 `ReleaseVisuals` 首段，代码注释明确；UI 验证清单第 2/3 条 |
| 池容量 64 不够/过多 | 视口卡片量级通常 <64；超出转退役（不裸丢） | `pool.pooled`/`created` 埋点可观测；容量常量单点可调 |
| `deferDrain` 被误用于非批量路径导致退役延迟 | 默认 false（单张回收保持原行为）；批量只有 ResetFrom 一处 | `resetFrom` 统一 Drain 在集合替换后；`retire.pending` 埋点可观察队列是否滞留 |
| `Undisposable` 计数上升被误读 | 首次日志含类型名与建议（改走池） | 计数分列 + 新 spec 口径说明 |
| 保命环让旧树多存活，内存上升 | 有界 4 项；树壳量级 KB~百 KB | 容量常量单点可调；面板连续编辑实机观察 |
| 回滚 | 三个提交各自独立 | `git revert feefa0e`（池化，回到退役队列语义）、`git revert 94fffaa`（退役链隔离/计数）、`git revert 23eac32`（视图侧/埋点）互不依赖；池化回滚后 `deferDrain` 调用点随 ① 一起回滚，无悬空引用 |
