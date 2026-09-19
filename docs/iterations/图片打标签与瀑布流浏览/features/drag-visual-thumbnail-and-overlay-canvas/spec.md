---
date: 2026-09-19
agile_trace: true
---

# drag-visual-thumbnail-and-overlay-canvas 实现规格（SPEC）

## 根因 / 方案摘要

**拖拽视觉**：卡片从未定制 DragUI → 系统默认整卡快照。WinAppSDK 1.6 的 `Microsoft.UI.Xaml.DragUI` 实际提供 `SetContentFromBitmapImage` / `SetContentFromSoftwareBitmap`（旧注释误记为不可定制）。DragUI 渲染位图不缩放，bucket 缩略图 360+px 偏大，故预生成短边 120px 小位图。

**首次收展闪跳 + 位置移动**：同根因——收展改变 ImageHost 尺寸 → 解码尺寸变化 → 首次缓存 miss 的重载窗口内旧源拉伸重布局 + 换源跳变；位置移动是尺寸变化必然结果。根治方案（用户拍板）：**遮盖式布局**——画布铺满整窗放最底层、几何恒定，侧栏/工具栏/状态栏全部成为 chrome 遮盖层；收展只改变遮盖范围，画布 SizeChanged 不再触发，重载与重布局从根上消失。

**GIF 换源空窗（顺修）**：GIF 走 `BitmapImage(UriSource)` 异步打开且不入解码缓存，同图换源在新源 ImageOpened 前无像素。改为换源处等待 ImageOpened（ImageFailed/2s 超时/取消任一放行）再提交并清旧源句柄，等待期旧源保持显示。

## 变更点清单

| 提交 | 内容 |
|------|------|
| 10745d6 | 拖拽视觉小缩略图：`ImageSourceHelper.TryCreateDragVisualAsync`（WIC+Fant 降采样、`IgnoreExifOrientation` 防二次旋转）+ `GalleryItemViewModel._dragVisual` 预生成（UiApplyGate 闸门段之后，不占串行闸门）+ `OnCardDragStarting` 三级回退 + 纠正旧注释 |
| b0e05fd | 多选计数：`MainViewModel.DragPayloadCount` 只读属性；`OnTagRowDragOver` 在 N>1 时设 `DragUIOverride.Caption="打标 N 张"` |
| 532c637 | 遮盖式布局：RootGrid → CanvasLayer(ZIndex=0，承载 SingleImageHost 整窗) + ChromeLayer(ZIndex=1，原四行结构)；SingleImageView 根 Grid 单 cell 叠加（ImageHost 铺满 Z=0、右栏/文件名栏改右/底浮层 Z=1 且不透明背景）；放大置顶收敛两级单点（ImageHost 提 Z=2 反盖浮层、CanvasLayer 提 100 盖全部 chrome） |
| a127599 | GIF 换源等 ImageOpened（`WaitForGifSourceOpenedAsync`，PixelWidth>0 防快照竞态；WinUI3 下 ImageOpened=RoutedEventHandler、ImageFailed=ExceptionRoutedEventHandler 分别声明） |
| 6512655 | RULE 铁律②口径更新：解码尺寸=整窗画布区；补"chrome 遮盖层收展不得改变画布几何"约束 |
| b1063b0 | 遮挡回归修复：右栏浮层（画布层 Z=0）顶部被 ChromeLayer 工具栏横行（Z=1）遮盖、收起按钮真实鼠标点不到（首版走查用 UIA AXPress 无视觉命中测试，假阳性）。MainWindow 依工具栏/InfoBar/状态栏 SizeChanged 写 VM.TopChromeHeight/BottomChromeHeight，SingleImageView.ApplyChromeInsets 据此设右栏展开/折叠条/文件名栏的避让 Margin（文件名栏硬编码 34 一并动态化） |

## 详细改动说明

### 布局结构（改造前 → 后）

```
改造前：RootGrid 四行（工具栏/InfoBar/MainAreaGrid/状态栏），侧栏与画布同级占位
        收展 → ImageHost 尺寸变 → 重解码 + 重布局
改造后：RootGrid 单 cell
        ├─ CanvasLayer（ZIndex=0，整窗）→ SingleImageView
        │    └─ 单 cell：ImageHost 铺满（Z=0，Padding=8，指针事件不变）
        │                右栏 280/折叠 36 右侧浮层（Z=1）；文件名栏底部浮层（Z=1，Margin 底 34 让出主状态栏）
        └─ ChromeLayer（ZIndex=1，原四行）
             ├─ 工具栏 / InfoBar / 状态栏（原样搬入）
             └─ MainAreaGrid：Col0 左栏（收展原样）+ Col1 图库 Grid（图库模式占位结构不变）
```

- **命中测试**：ChromeLayer/MainAreaGrid/ContentAreaGrid 均无 Background（Grid 默认 null 不参与命中），单图模式未遮区域指针穿透到画布，缩放/平移手势正常；侧栏/工具栏等有背景区域遮盖画布（预期语义）。
- **图库模式**：SingleImageHost 随 SingleVisibility 隐藏，图库走 ChromeLayer 内占位结构，行为与改造前一致。
- **窗口 resize**：画布随窗口 → ImageHost SizeChanged → 同图重载（579f33e 保持旧源逻辑 + 本次 GIF 等待 ImageOpened，均不闪空）。
- **放大置顶**：`UpdateZoomLayering`（SingleImageView 内提 ImageHost ZIndex）+ `OnViewModelPropertyChangedForZoomLayering`（MainWindow 提 CanvasLayer ZIndex）各一个单点，复位/切图/回图库清残留逻辑保留。

### 拖拽视觉回退链

`OnCardDragStarting`：`_dragVisual`（SoftwareBitmap，短边 120px）→ `SetContentFromSoftwareBitmap(visual, new Point(w/2, h/2))`；无小位图且 Thumbnail 已解码 → `SetContentFromBitmapImage`；都没有 → 系统默认整卡快照。小位图生成失败静默降级（null），占位中拖拽退回默认快照。

## 测试策略

### 测试用例

- **自动化**：`dotnet test` 71 项基线全绿（新逻辑重度依赖 WinRT/UI 层，Core 测试工程不可达，未强行加测）；build.ps1 通过（restore 踩踏按 RULE 第 8 条定向还原绕过）。
- **实机走查（已完成）**：单图模式收展左栏/右栏四态，image 元素 UIA bounds 恒为 [427,323,959,720] 不变；打开图库走 FolderPicker 选 test-library，瀑布流 10 卡正常；双击卡片回单图正常。
- **留用户实机验证**：拖拽小缩略图观感与"打标 N 张"caption（本机 SendInput 移动事件被丢弃，拖拽/滚轮均无法注入——滚轮放大置顶亦留实机）。

## 风险与回滚方案

- **风险**：① 整窗解码比原可见区多约 25-40% 像素（LRU 容量 6 下 resize 频繁时互逐加剧）；② 宽图左右边缘被侧栏遮盖（产品语义变化，用户已拍板）；③ ChromeLayer 若未来被加上 Background 会吞掉画布手势（已注释警示）。
- **回滚**：532c637 单提交 revert 即回到占位式布局（拖拽视觉两提交独立可保留）；RULE 铁律②文本需随回滚同步改回（6512655）。
