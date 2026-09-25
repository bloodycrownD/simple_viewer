---
date: 2026-09-25
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# detail-view-fit-visible-area Feature PRD

## 背景与变更动机

用户反馈（原话）：**「100% 加载有问题、应该宽高均在视图内、但不是这个逻辑」**，并明确口径
**「用宽度或者高度最大一个均在视图内」**。

**实机实测（推翻早期静态推断）**：单图此前**已经是** contain 适配——WinUI `Image` + `Stretch=Uniform`
+ Center 对齐，元素盒 = contain 盒（DPI 感知量测：small/wide/tall/big 四夹具的 UIA Image 盒与整窗
contain 期望吻合到 0.2%，含小图放大）。真正的根因是**遮挡**：图片按「整窗画布」适配，而左栏
（280/36）、单图右栏信息面板（280/36）、顶部工具栏+信息横条（`TopStatusStrip`）都盖在画布上。

修复前基线量测（master `8804295` 在工作树 `git worktree` 中构建的 exe，1600x1000 物理窗口 @144dpi，
`scripts\fit-verify.ps1` 同脚本口径；客户区 1578x944 物理 px = 1052x629 DIP）：

| 夹具 | 源图 | UIA Image 盒(px) | 实际可见红像素带(px) | 可见红带宽高比 vs 源 | 水平可见比 |
|------|------|------------------|----------------------|----------------------|-----------|
| small | 400x300 | 1225x919 @(227,97) | 742x819 | 0.906 vs 1.333 | 742/1225 = 60.6% |
| wide | 800x200 | 1554x389 @(63,362) | 742x389 | 1.907 vs 4.0 | 742/1554 = 47.8% |
| tall | 600x3000 | 184x919 @(748,97) | 184x819 | 0.225 vs 0.2 | 高 819/919 = 89.1% |
| big | 4000x3000 | 1225x919 @(227,97) | 742x819 | 0.906 vs 1.333 | 60.6% |

即：UIA 盒 = 整窗 contain 盒（boxVsContain 0.998~1.003），但**可见红像素带明显小于整图**、宽高比
与源图不符——图片被左侧栏与右栏各遮掉约 163 DIP（合计约 40% 宽度），用户看到的就是「照片被切了一块」。

## 范围说明（相对原需求）

- 只改**图片自身的适配盒**：由「整窗画布」改为**未被 chrome 遮挡的可见区**——左内缩 = 左栏实际宽、
  上内缩 = 顶部信息横条底边、右内缩 = 右栏实际宽、下 = 0；图片整体落入可见区，仍保持比例
  （Uniform contain）与小图放大，约束轴贴合可见区边界。
- **不改画布几何**：`ImageHost` 仍铺满整窗且几何恒定，收展左/右栏不改变画布、不触发重解码
  （既有铁律保留：只更换「图片适配基准」，不换画布）。
- **不改解码尺寸链**（`MainViewModel.CalculateDecodeSize` 仍取 `max(画布宽,高)`）与任何 MainWindow 布局。
- 不改缩放/平移/双击复位/Esc 行为；仅修正滚轮缩放的锚点中心（内缩不对称后盒中心 ≠ 宿主中心，见 spec）。

## 影响模块与接口

| 模块 | 变更 |
|------|------|
| Views/SingleImageView.xaml | `ViewerImage` 对齐 `Center` → `Stretch`（`Stretch=Uniform`、`RenderTransformOrigin=0.5,0.5`、`DoubleTapped`、Source/Visibility 绑定、`CompositeTransform` 全保留）；注释补「适配盒 = 可见区」口径 |
| Views/SingleImageView.xaml.cs | 新增右栏宽常量（280/36）+ 横条高度回退常量（48）；`ApplyChromeInsets` 追加 `ViewerImage.Margin`；属性白名单加 `IsInfoPanelCollapsed`；订阅 `TopStatusStrip.SizeChanged`（防弹回调）；`ZoomAt` 锚点中心改按元素盒实算 |
| ViewModels/MainViewModel.cs | **零改动**（解码链、CanExecute、模式切换均不变） |
| scripts/fit-verify.ps1 | 入库（实机量测资产，断言逻辑未改，仅原样纳入版本管理） |

对外接口无变化；单图可见区适配盒（DIP，1600x1000@144dpi 客户区 1052x629）：
`x ∈ [288, 764]`（= 8 Padding + 280 左栏 … 1052 − 280 右栏 − 8 Padding）、
`y ∈ [84, 620.67]`（= 8 Padding + 76 横条底边 … 629 − 8 Padding）。

## 验收标准

- [x] 四个夹具图片**整体可见**：pixel red band 尺寸 == UIA Image 盒尺寸 → small 714x536、wide 714x179、
      tall 161x806、big 714x536（band 不再被裁；修复前分别差 483px 宽 / 812px 宽 / 100px 高）。
- [x] **宽高比不失真**：band 宽高比 1.332 / 3.989 / 0.200 / 1.332 vs 源图 1.333 / 4.0 / 0.2 / 1.333。
- [x] **约束轴贴合可见区边界**：UIA 盒 = 可见区 contain 盒（boxVsContain W/H 0.459~0.875，均 < 1 符合
      新基准）；tall 由高度约束（宽 161px = 107.3 DIP）、small/wide/big 由宽度约束（宽 = 714px = 476 DIP）。
- [x] **收展右栏后图片跟随重适配**（实机）：714x179 →（收起右栏，`IsInfoPanelCollapsed`）1080x270
      （宽 +366px = 244 DIP × 1.5，恰为 280−36）→（再展开）714x179，完全可逆。
- [x] 缩放/平移/双击复位/Esc 行为不变（交互代码未改；仅锚点中心改为按新盒中心实算，避免纵向偏移约一个横条高）。
- [x] 无新增异常：`%LocalAppData%\SimpleViewer\logs\startup.log` 本轮（4 次 fit-verify 启动 + 一次 40s 长驻）
      零新增异常条目，长驻期只多 1 条 `[GC 采样]` 行。
- 已知限制（本轮不实现，见 spec「已知限制」）：① 旋转（左旋/右旋）后仍按未旋转尺寸适配，旋转溢出被
  chrome 遮盖属既有行为；② 解码按「可见区 × DisplayScale」做到物理 1:1 为后续项。

## 测试用例

1. `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → 120/120 全绿；
   `scripts\build.ps1` → 第 1 次尝试成功。
2. 改造后 `fit-verify.ps1` 四夹具实机量测（修复后数据见 spec「测试策略」）。
3. 前后对照：`git worktree add` master 到临时目录 + `build.ps1` 构建原 exe，同脚本量测（本文件上表）。
4. 右栏收展实机探针（临时脚本，不入库）：展开 714x179 ↔ 收起 1080x270 ↔ 复展 714x179。
5. 单图横条走查 `single-strip-verify.ps1`（回归）：`NO-RED-SEAM`（顶带无透图缝隙）、
   `◀ 返回图库`/翻页/旋转按钮可见——适配盒变小后顶带仍无图透出。
6. `startup.log` 检查（见验收标准最后一条）。
