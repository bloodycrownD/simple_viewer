---
date: 2026-09-26
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# xaml-finalizer-residuals Bug PRD

## 背景

tag-op-finalizer-crash（v1.0.5 长会话 15h40m 后 fastfail 0xc0000409 @ ucrtbase+0xa527e）已确证崩溃机制：**XAML 依赖对象必须在创建线程（UI STA）析构**；CsWinRT 的 `WinRT.IObjectReference.Finalize` 在 GC 终结器线程直接 native Release → native 堆损坏 → 无日志闪退。上一轮已修画刷 churn（`Views\TagBrushCache.cs`）与图像源四类缺口，但**三次只读全仓扫描**（对 XAML 对象创建点/退役链的静态审计）发现仍有残留：

1. `BitmapImage` 退役是**空操作**——退役队列的 `(source as IDisposable)?.Dispose()` 对它恒为 null，队列只把裸交 GC 的时刻推迟过保留窗口；瀑布流缩略图是最大 churn 源（每卡每次 Realize 一张）。
2. 缩略图未提交源漏网、退役链无异常隔离、未释放者被统计成已释放、批量退役边界名不副实。
3. 若干低速裸丢点（文件名 `Run`、筛选 `Style`、筛选面板整树重建）与可观测性缺口。

本 bug 即上述残留的收口轮（敏捷名 `xaml-finalizer-residuals`）。

## 现象描述

- 长会话（数小时）滚动瀑布流、反复筛选/打标重排后，进程表现为**无日志闪退**（fastfail `0xc0000409`，事件日志模块 `ucrtbase`，偏移如 `+0xa527e`）或 stowed `0xc000027b`；转储指纹为终结器线程停在 `WinRT.IObjectReference.Finalize`。
- 触发面比上轮认知更宽：**缩略图 BitmapImage 的退役队列对它是 no-op**——队列只是把它多持有几个 Drain 边界，之后仍由终结器线程 native Release。滚动/筛选 `ResetFrom` 批量回收是主力 churn。
- 另有两类次生风险：任一 `Dispose()` 抛出会中断整批回收（剩余 VM 的 Thumbnail 从此无处置点）；`Release` 无条件把 no-op 记成「已释放」，会让日后回归判据假绿。

## 预期行为 / 实际行为

| | 预期 | 实际 |
|---|---|---|
| 缩略图退役 | 缩略图实例在 UI 线程确定性处置/复用，终结器永不触碰 | `BitmapImage` 无 IClosable，退役队列对它 no-op；终结器线程仍会 native Release |
| 未提交缩略图（取消/异常） | 未提交路径同样有处置（还池或退役） | `bitmap` 是内层 try 局部变量，外层 catch 不可见——取消/SetSourceAsync 异常窗口的实例无任何处置（裸交 GC） |
| 批量回收（筛选 Reset） | 每个源至少跨过一次 Drain 边界 | 逐项 `ReleaseVisuals` 各 Drain 一次：一次 N 项重置在同一同步循环内 Dispose 掉除最后约 9 个外的全部源 |
| 退役失败 | 单条失败只落日志，批处理继续 | `Release`/`Drain` 无 try/catch：任一 Dispose 抛出中断整批 + 异常可能上传 XAML 回调（stowed 0xc000027b） |
| 诊断计数 | 真 Dispose 与「无 Dispose 成员、只能延迟 GC」分开 | `_releasedCount` 无条件自增，BitmapImage 被记成已释放 |
| 低速裸丢点 | 热路径不产生一次性 XAML 对象 | 打标改名 `new Run`；每次开筛选面板 `new Style` + 4 Setter；每次编辑重建 40~300 个 XAML 对象直接落 GC |

## 影响范围

- **瀑布流缩略图 churn（主）**：滚动实现/回收（`ElementPrepared/ElementClearing`）、筛选切换与重新打开图库（`WaterfallViewModel.ResetFrom`）——每张卡片每次 Realize 一张 BitmapImage；
- 单图 GIF 源（`ImageSourceHelper` 的 `new BitmapImage`，低频豁免）；
- 打标改名路径的文件名文本刷新（单图右栏，每次一个 `Run`）；
- 筛选 Flyout 每次打开（Style + Setter）、筛选面板每次编辑（条件树整树重建）；
- 长会话可观测性：退役队列 `Snapshot()` 全仓零调用，池化前无任何 churn 趋势数据。

## 验收标准

**无头部分（本轮已完成，硬验收）**

1. `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → 120/120 通过；
2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` → 通过；
3. 代码级自检（grep 证据）：`new BitmapImage` 全仓仅剩池内一处（+ GIF 分支豁免）；`ReleaseVisuals`/`LoadThumbnailAsync` 还池与 finally 覆盖；`Release` 的 try/catch 与 `Undisposable` 分计；`new Run`/`new Style` 残留点已改；`UiKeepAlive` 接入点存在。

**待主代理 UI 验证（本轮按用户要求不启动 GUI，未做）**

1. 开图库滚动 → 缩略图正常渐入、无空白卡片（池化后重载路径）；
2. 滚动到顶部/底部/快速拖滚动条 → 回收后重新实现卡片恢复显示（还池后重 Acquire）；
3. 筛选切换（侧栏点击、筛选面板编辑、筛选条 ✕）→ 重置后整墙重新呈现、无残留旧图/错图；
4. 连开连关筛选 Flyout（含改窗口宽度）→ 面板尺寸正确（Style 缓存命中/重建两路径）；
5. 单图打标改名 → 右栏文件名即时更新、tooltip 正确（第 3 行文本路径替换内联）；
6. 单图 GIF 查看/切图 → 显示正常（GIF BitmapImage 仍走退役队列，可见 `[退役队列] …无 IClosable…` 首次说明日志）；
7. 拖拽卡片到侧栏打标 → 跟随视觉正常、拖后卡片缩略图长期显示正常（DragUI 一次性排除路径）；
8. 长会话（数小时）滚动 + 打标 + 筛选 → 无 fastfail/stowed；`startup.log` 中 `pool:` 的 `created` 稳态有界、`retire:` 的 `undisposable` 只在 GIF 等豁免源出现；
9. 筛选面板连续编辑点击 → 无闪退/无卡顿（保命环为「不释放」的止血措施，见 spec 已知残留）。

## 回归测试要点

- 单测 120/120 为基线，本轮改的是 UI 工程与 UI-only Helper（不改可测逻辑，测试数量与断言口径不变）；
- `scripts\build.ps1` 通过（XAML 改动涉及两处 code-behind 直写属性，x:Bind 未动）；
- 长会话趋势判据：`retire:` 行（`released` 为真 Dispose 数，`undisposable` 为 no-op 源数，两者分开）与 `pool:` 行（`created` 不再随滚动无限增长）——若日后出现「new BitmapImage 回潮」，`pool.created` 与 `retire.undisposable` 会同时异常抬升；
- 本 bug 的崩溃属非确定性（需终结器批量退休 + 合成器换帧窗口叠加），无头部分不能证明崩溃不再发生；**实机长跑（≥2h 滚动 + 打标 + 筛选）仍是最终判据**。
