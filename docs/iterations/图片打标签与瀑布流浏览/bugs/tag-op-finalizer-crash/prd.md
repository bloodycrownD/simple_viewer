---
date: 2026-09-26
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# tag-op-finalizer-crash Bug PRD

## 背景

v1.0.5 部署版（`E:\App\Others\viewer`）在连续运行 15h40m 后无日志闪退。事故取证（dump + 事件日志 + 托管堆状态）已确证崩溃点是 **GC 终结器线程跨线程 Release WinRT/XAML 依赖对象**，属 RULE:26 记载的「XAML 对象（DependencyObject 族）永不可裸交 GC」同一族。既有机制 `Helpers\ImageSourceRetirement.cs` 只覆盖图像源 4 个接入点，**打标收尾路径上的画刷 churn 完全没被覆盖**——本 bug 即该缺口。

用户反馈（原话）：**「长时间使用后一次添加标签过程直接崩溃」**。

## 现象描述

- 2026-09-26 16:00:19，部署版 v1.0.5 连续运行 15h40m 后崩溃；Windows 事件日志 `0xc0000409`（fastfail）+ `ucrtbase.dll +0xa527e`；应用日志零条目（无日志闪退）。
- 转储 `C:\Users\BloodyCrown\AppData\Local\CrashDumps\viewer.exe.4728.dmp` 三证据链：
  1. **终结器线程栈 = `WinRT.IObjectReference.Finalize()`，其 IP 与出错偏移 `ucrtbase+0xa527e` 完全一致** → 崩溃点是 GC 终结器线程在跨线程 Release WinRT/XAML 对象；
  2. `eeheap -gc` 报 GC 堆数 0（托管堆不可遍历）→ native 堆已损坏；
  3. 历史同指纹：09-24 三次 `0xc0000409 @ ucrtbase+0xa527e`（其中一次已归因 SHFileOperationW/MTA 并修复）——同一族机制反复出现。
- 崩溃前一刻的操作为「添加标签」，属打标收尾路径（必然触发侧栏全量重建）。

## 复现条件

- 长会话（数小时）+ 反复打标/筛选/组展开折叠/主题切换（每次操作都触发侧栏全量重建）；
- **非确定性**：单次打标不会崩，需终结器批量退休 + 合成器换帧窗口叠加（本机无法稳定复现，故以 dump 指纹 + 代码审计 + churn 埋点作为验收判据）。

## 预期行为 / 实际行为

| | 预期 | 实际 |
|---|---|---|
| 打标收尾重建侧栏 | 只复用既有画刷实例，不产生新的 DependencyObject | 每轮重建为每个「最终颜色」新建 `SolidColorBrush`，用后裸丢给 GC（终结器线程跨线程 Release） |
| 长会话稳定性 | 无 fastfail | 15h40m 后 `0xc0000409 @ ucrtbase+0xa527e` |
| 崩溃可诊断性 | 有日志/埋点可追 | 零日志闪退（终结器线程崩溃不经 UnhandledException） |

## 影响范围

所有触发侧栏/筛选条全量重建的路径：

- **打标/移除标签收尾**（`ApplyTagToPathsAsync → ShowTagOperationResult → RefreshTagDataAsync → RebuildTagSidebar`；单图/批量/快捷键/拖拽/目录批量全部走同一管线）；
- 扫描期节流刷新（每 5s）与扫描结束；
- 筛选应用（侧栏点击、筛选面板编辑、筛选条 ✕）；
- 组展开/折叠切换；
- 主题切换（`RefreshThemeDependentVisuals`）；
- 标签组/标签配置保存后的重建。

旁路缺口（同族，一并收口）：

- 拖拽打标 DragOver 的落下高亮画刷（每次 DragOver 新建后裸丢）；
- 瀑布流整体重置（`WaterfallViewModel.ResetFrom`）丢弃旧卡片 VM 时未显式退役缩略图；
- `TryEnqueue(BeginLoadThumbnail)` 与 `OnElementClearing → ReleaseVisuals` 的「先清后启」竞态（已回收 VM 被排队的回调重新挂上缩略图，绕过退役队列）；
- `ImageSourceHelper` 失败/自愈路径未提交的 `SoftwareBitmapSource`、`MainViewModel` GIF 换源取消路径未提交的新源、`EnsureFullResolutionAsync` 的「先退役旧源、再 await 新源」顺序隐患；
- `ImageSourceRetirement.Drain` 是纯计数窗口：一次批量退役（筛选 Reset / 打开图库整体重置）立刻 Dispose 超窗的几十个源，只留最后 8 个——「余量」不由帧/操作边界保证。

## 验收标准

1. **颜色逐像素一致（硬验收）**：同一图库状态下，部署版 v1.0.5 与本次构建的侧栏区（420×900 物理像素）逐像素比对 **diff=0**（深色 + 浅色两主题、未筛选/筛选激活/组折叠三状态）；组头行/激活与未激活标签行/计数徽标/筛选条 chip/未定义标签区等代表性位置逐点 RGB 一致。
2. **churn 归零**：修复后重复重建（打标收尾、组展开折叠、筛选应用）的 `sidebar:rebuild` 埋点满足 `brushNew=0` 且 `brushSinceLast=0`；缓存条目数有界（不随操作次数增长）。
3. **各链路不回归**：单图加/移除标签（右栏 chips 就地变化）、批量打标、标签筛选点击、组展开折叠全部正常；拖拽打标按 RULE:57 只做代码审查（无法自动化注入）。
4. **转储指纹不再出现的判据**：长会话下 `sidebar:rebuild` 的 `brushSinceLast` 恒为 0（画刷不再裸交 GC），且 `startup.log` 无新增异常条目；若未来 churn 回潮，埋点会打印 `[侧栏画刷 churn 告警]` 行。

## 回归测试要点

- 单测 120/120 通过（基线 120，本次未改可测逻辑）；
- `scripts\build.ps1` 通过；
- `scripts\tag-op-finalizer-verify.ps1` 三个 phase（color / churn / chain）可复跑，产出像素比对与埋点证据；
- 颜色比对必须带**同二进制复跑对照**（release vs release2）以剔除 UI 瞬态（焦点框/悬浮滚动条/半透明底噪）造成的假差异；
- 每次改动画刷取色函数后必须重跑 color phase（缓存键取错会立刻暴露为 diff>0）。
