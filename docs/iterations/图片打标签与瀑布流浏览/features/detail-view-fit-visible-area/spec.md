---
date: 2026-09-25
agile_trace: true
---

# detail-view-fit-visible-area 实现规格（SPEC）

## 根因 / 方案摘要

- **根因（实机实测，推翻早期的「非 contain / 100% 1:1」静态推断）**：单图已是整窗 contain 适配
  （`Image` + `Stretch=Uniform` + Center 对齐 → 元素盒 = contain 盒；四夹具 UIA 盒与整窗 contain 期望
  吻合到 0.2%）。用户看到的「被切一块」是**遮挡**：画布铺满整窗，而左栏 280/36、右栏 280/36、
  顶部工具栏+信息横条都压在画布之上，图片约 40% 宽度落在 chrome 之下。
- **方案**：把**图片自身的适配盒**从「整窗画布」改为「可见区」——`ViewerImage` 对齐 Center → `Stretch`
  （元素盒 = 宿主 − `Padding` − `Margin`），再由 code-behind 把 chrome 内缩写进 `Margin`
  （左=左栏宽 / 上=横条底边 / 右=右栏宽 / 下=0）。`Stretch=Uniform` 自带 contain 语义 → `Scale=1`
  仍是 fit（相对新基准盒），缩放/平移/双击复位的既有实现无需改动。
- **铁律保持**：`ImageHost` 画布几何仍 = 整窗且恒定，收展 chrome 只改图片适配盒、不改画布、不触发重解码；
  解码尺寸链（VM 取 `max(画布宽,高)`）不动。

## 变更点清单

| 提交 | 内容 |
|------|------|
| cba27b3 | Views/SingleImageView.xaml：`ViewerImage` 对齐 Center → Stretch + 注释补「适配盒 = 可见区」。Views/SingleImageView.xaml.cs：新增 `InfoPanelExpandedWidth=280` / `InfoPanelCollapsedWidth=36` / `TopStatusStripFallbackHeight=48` 常量；`ApplyChromeInsets` 追加 `ViewerImage.Margin = (sidebarWidth, TopStatusStrip.Margin.Top + 横条高, infoPanelWidth, 0)`；属性白名单加 `IsInfoPanelCollapsed`；订阅 `TopStatusStrip.SizeChanged`（try/catch 兜底落日志）；`ZoomAt` 锚点中心改按元素盒中心实算 + 新增 `GetViewerImageBoxCenter()`；`MinZoom`/`MaxZoom`/`FullResZoomThreshold` 与文件头注释口径同步。2 files changed, +92/−8 |
| 536aa43 | scripts/fit-verify.ps1 入库（168 行，DPI 感知的实机量测资产：四夹具生成 → 固定窗口 → UIA Image 盒 + 像素红带 bbox + 判定行），断言逻辑一字未改 |

## 详细改动说明

### 1. XAML：对齐 Center → Stretch（`Views\SingleImageView.xaml:36-56`）

- `HorizontalAlignment`/`VerticalAlignment` 由 `Center` 改 `Stretch`；`Stretch="Uniform"`、
  `RenderTransformOrigin="0.5,0.5"`、`DoubleTapped`、`Source`/`Visibility` 绑定、`CompositeTransform`
  （旋转 VM 绑定 + 缩放/平移）全部保留。
- 语义变化：Center 对齐下元素盒 = 位图盒（`Image` 期望尺寸即 contain 盒）；Stretch 对齐下元素盒 =
  「宿主 − Padding(8) − Margin」，`Uniform` 再把位图按比例 contain 进该盒（位图小于盒时居中放置，
  故位图中心 == 元素盒中心 == 变换原点 `0.5,0.5`）。渲染关系与旧行为一致，只是基准盒换了。

### 2. code-behind：内缩、跟随与锚点（`Views\SingleImageView.xaml.cs`）

- 常量：`InfoPanelExpandedWidth = 280` / `InfoPanelCollapsedWidth = 36`（与 XAML `InfoPanelOverlay.Width`
  现 `:119`、`InfoPanelCollapsedBar.Width` 现 `:293` 同族锚点，注释沿用 `SidebarExpandedWidth/CollapsedWidth`
  的「改值两处同步」口径）；`TopStatusStripFallbackHeight = 48`（首帧 `ActualHeight` 为 0 时的**宁大勿小**
  保守值——大了图片偏小但绝不越出可见区）。
- `ApplyChromeInsets()` 追加（在既有浮层避让之后）：
  `ViewerImage.Margin = new Thickness(sidebarWidth, TopStatusStrip.Margin.Top + stripHeight, infoPanelWidth, 0)`；
  `stripHeight = TopStatusStrip.ActualHeight > 0 ? ActualHeight : TopStatusStripFallbackHeight`。
- 订阅补齐：`OnViewModelPropertyChanged` 白名单追加 `nameof(MainViewModel.IsInfoPanelCollapsed)`
  （右栏收展 → 重算适配盒）；构造函数内订阅 `TopStatusStrip.SizeChanged`，回调体**只做轻量赋值**
  （调 `ApplyChromeInsets`）+ 整体 `try/catch` 兜底 `App.WriteDiagnosticLog`，绝不外抛——遵 XAML 回调铁律
  （回调内逃逸异常 = stowed 直接杀进程，不弹不记；先例 `WaterfallView.OnElementPrepared`/`MasonryLayout`）。
- `ZoomAt()` 锚点中心：由 `ImageHost.ActualWidth/2, ActualHeight/2` 改为
  `(ImageHost.Padding.Left + ViewerImage.Margin.Left + ViewerImage.ActualWidth/2, ImageHost.Padding.Top + ViewerImage.Margin.Top + ViewerImage.ActualHeight/2)`
  （抽成 `GetViewerImageBoxCenter()`；盒未布局时回退宿主中心 = 旧行为）。
  **推导一句**：变换序 Scale→Rotate→Translate 且原点在元素布局中心 C 时，屏幕点 = C + T + R·(s·p)，
  锚点不动条件 `T' = v − (s'/s)(v − T)`，`v = A − C`——上下内缩不对称后 C 不再是宿主中心（省的 `Padding`
  项会带来恒定 8 DIP 偏差，省的 `Margin.Top` 项会带来约一个横条高的纵向偏移），故必须按元素盒实算；
  `Padding` 项取自 `ImageHost.Padding`（自同步 XAML `Padding="8"`，不新增常量）。
- 注释口径：`MinZoom`/`MaxZoom`/`FullResZoomThreshold` 的「相对 fit 尺寸」自本次起字面成立
  （`Scale=1` 即「可见区 contain」），已注明；文件头「ImageHost 画布铺满整根（几何=整窗恒定）」叙述
  补一句「图片适配盒 = 可见区（避开左栏/右栏/顶部横条遮挡）」。

### 3. 明确未改动的部分

- 解码尺寸链（`CalculateDecodeSize` 仍取 `max(画布宽,高)`）、`MainWindow.xaml` 任何布局、
  `ImageHost.Padding=8`、缩放/平移/双击复位/Esc 逻辑本体。

## 测试策略

- 单测：`dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → **120/120 通过**（失败 0、跳过 0）。按还原顺序铁律先跑 tests 后跑 build。
- 构建：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` → 第 1 次尝试成功（exit 0）。
- **实机量测前后对照**（同一脚本 `scripts\fit-verify.ps1`，1600x1000 物理窗口 @144dpi，
  client 1578x944 px = 1052x629 DIP；窗口置于 (40,40)）：

修复前（master `8804295` 于临时 `git worktree` 构建）：

```
=== fixture=small  src=400x300  dpi=144 scale=1.5
  expect contain(px) = 1227 x 920   (fit=2.044)
  UIA Image box(px) = 1225 x 919 at (227,97)
  pixel red band(win-local) = (429,157)-(1170,975) size=742x819 area=607698
  verdict: boxVsContain W=0.998 H=0.999  boxVsNatural W=2.042 H=2.042
  verdict: aspect(band)=0.906 srcAspect=1.333      ← 图被裁，宽高比失真
=== fixture=wide  src=800x200  dpi=144 scale=1.5
  UIA Image box(px) = 1554 x 389 at (63,362)
  pixel red band(win-local) = (429,322)-(1170,710) size=742x389
  verdict: aspect(band)=1.907 srcAspect=4
=== fixture=tall  src=600x3000  dpi=144 scale=1.5
  UIA Image box(px) = 184 x 919 at (748,97)
  pixel red band(win-local) = (708,157)-(891,975) size=184x819
  verdict: aspect(band)=0.225 srcAspect=0.2
=== fixture=big  src=4000x3000  dpi=144 scale=1.5
  UIA Image box(px) = 1225 x 919 at (227,97)
  pixel red band(win-local) = (429,157)-(1170,975) size=742x819
```
（small/big 水平可见 742/1225 = 60.6%、tall 垂直可见 819/919 = 89.1%——被 chrome 遮挡实锤）

修复后（分支 `cba27b3` 构建的 Debug exe）：

```
=== fixture=small  src=400x300  dpi=144 scale=1.5
  expect contain(px) = 1227 x 920   (fit=2.044)    expect natural-100%(px) = 600 x 450
  UIA Image box(px) = 714 x 536 at (483,346)
  pixel red band(win-local) = (443,306)-(1156,841) size=714x536 area=382704
  pixel red band(screen)    = (483,346)  size=714x536
  verdict: boxVsContain W=0.582 H=0.583  boxVsNatural W=1.19 H=1.191
  verdict: aspect(band)=1.332 srcAspect=1.333
=== fixture=wide  src=800x200  dpi=144 scale=1.5
  UIA Image box(px) = 714 x 179 at (483,525)
  pixel red band(win-local) = (443,485)-(1156,663) size=714x179 area=127806
  verdict: boxVsContain W=0.459 H=0.461  boxVsNatural W=0.595 H=0.597
  verdict: aspect(band)=3.989 srcAspect=4
=== fixture=tall  src=600x3000  dpi=144 scale=1.5
  UIA Image box(px) = 161 x 806 at (759,211)
  pixel red band(win-local) = (719,171)-(879,976) size=161x806 area=129766
  verdict: boxVsContain W=0.875 H=0.876  boxVsNatural W=0.179 H=0.179
  verdict: aspect(band)=0.200 srcAspect=0.2
=== fixture=big  src=4000x3000  dpi=144 scale=1.5
  UIA Image box(px) = 714 x 536 at (483,346)
  pixel red band(win-local) = (443,306)-(1156,841) size=714x536 area=382704
  verdict: boxVsContain W=0.582 H=0.583  boxVsNatural W=0.119 H=0.119
  verdict: aspect(band)=1.332 srcAspect=1.333
```

关键判定：修复后 **pixel red band 尺寸 == UIA Image 盒尺寸**（四夹具全部相等）⇒ 位图整张可见、无任何
遮挡；band 宽高比 == 源图宽高比（1.332/3.989/0.200/1.332 vs 1.333/4.0/0.2/1.333）⇒ 不失真；
UIA 盒 < 整窗 contain 盒（boxVsContain 0.459~0.875）⇒ 适配基准已换成可见区。
几何自洽核对（small 夹具，DIP）：元素盒 x∈[288,764]（8+280 … 1052−280−8）、y∈[84,620.67]
（8+76 … 629−8）；位图 400x300 按宽约束 fit=1.19 → 476x357.33，居中于元素盒 ⇒ 位图左上
= (288, 174) DIP = 客户区 (432, 261) px = 屏幕 (483, 346) px ✓ 与 UIA 实测完全一致。

- 右栏收展跟随（临时探针，不入库）：`ImageBox-expanded: 483,525 714x179` →
  `ImageBox-collapsed: 483,479 1080x270`（宽 +366px = 244 DIP×1.5 = 280−36）→
  `ImageBox-reexpanded: 483,525 714x179`（可逆）。
- 回归：`single-strip-verify.ps1` 实机 `NO-RED-SEAM` + 六按钮可见 + `SETTINGS-RESTORED`；
  `toolbar-catalog-verify.ps1` 图库段/批量目录/删除段全过。
- 日志：`%LocalAppData%\SimpleViewer\logs\startup.log` 本轮零新增异常条目
  （4 次 fit-verify 启动 + 40s 长驻后仅多 1 条 `[GC 采样]` 行；长驻前 262754 字节 → 长驻后 262851 字节）。

## 已知限制（本轮不实现，留后续项）

1. **旋转后不重适配**：左旋/右旋后图片仍按未旋转尺寸适配可见区，旋转溢出被 chrome 遮盖属既有行为
   （2026-09-19 用户拍板「放大/溢出被 chrome 遮盖」语义），本次未改。
2. **解码未按可见区 × DisplayScale 做到物理 1:1**：解码尺寸链仍是 `max(画布宽,高)`（画布 = 整窗，
   比可见区大），图片在可见区里只会被进一步缩小显示——不会溢出或锯齿，但未做到「可见区 1:1 物理像素」
   的锐度上限；若要更锐需改解码链（本次刻意不动，避免与画布恒定铁律冲突）。

## 风险与回滚方案

- 风险①**内缩参数与 XAML 漂移**：右栏宽 280/36 是双处锚点（XAML Width 与 code-behind 常量）、
  ImageHost `Padding="8"` 由 `ImageHost.Padding` 自同步读取（未新增常量）。改右栏宽或 Padding 时需同步
  常量注释；顶部横条高度走 `ActualHeight` 动态取值 + 回退常量兜底，横条改高不会失配（SizeChanged 重算）。
- 风险②**首帧回退值偏小导致图片越界**：回退值 48 DIP 大于实测横条高（43 DIP）——宁大勿小，且
  `SizeChanged` 到达后立即修正；实测四夹具无越界（band 全部落在可见区内）。
- 风险③**XAML 回调异常**：`TopStatusStrip.SizeChanged` 回调按铁律整体 try/catch 落日志不外抛；
  冒烟（4 次启动）未产生任何异常日志条目。
- 风险④**滚轮缩放锚点**：锚点中心已按元素盒实算（含 `Padding` 项）；若省略会带来约一个横条高的纵向偏移
  ——属交互观感回归而非崩溃，长期可由 zoom-path 走查捕获。
- 回滚：`cba27b3` 独立可 revert（XAML 两行对齐 + code-behind 内缩/订阅/锚点）；revert 后图片回到
  「整窗 contain + 被 chrome 遮盖」的旧行为。`536aa43`（量测脚本入库）可独立保留。
