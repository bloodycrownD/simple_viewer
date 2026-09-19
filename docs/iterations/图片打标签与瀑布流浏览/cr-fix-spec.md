# CR Fix Spec: 图片打标签与瀑布流浏览

## 元信息

- repo: `D:\Dev\Python\simple_viewer`（分支 master）
- base_sha: `d0055382c86132c17f96f8e4a06d5da5d80c1362`
- head_sha: `b6b3e7c1dbc67d6bef166e3d04c36ab8b2afb310`
- prd_path: `docs/iterations/图片打标签与瀑布流浏览/prd.md`（只读参考）
- spec_path: `docs/iterations/图片打标签与瀑布流浏览/spec.md`（只读参考）
- review_round: 2
- dag_version: 3
- 状态: fix-spec-ready（review-full round2 校验 23 条存量全部合格；本轮增补 P2-16/P2-17、修正 P1-7 锚点、open questions 扩充至 9 项——trivial 量级由主代理直接执行）
- 条目统计: P0 = 0；P1 = 8；P2 = 17；合计 25 条（同源 vm/C-1 与 views/C-2 已合并为 cr/P1-3 一条）

---

## Must-fix（按 P0 → P1 → P2）

本轮 P0 = 0。

### cr/P1-1 [P1] 扫描中重开图库竞态

- 维度：B
- 文件：`ViewModels/MainViewModel.cs:614-731`（StartLibraryScanAsync）、`ViewModels/MainViewModel.cs:1635-1644`（RefreshTagDataAsync）
- 问题：二次打开图库时旧扫描续体仍会执行：
  1. 旧续体的 catch 覆盖新扫描的状态行文案；
  2. finally 中 `RefreshTagDataAsync(旧 indexService)` 对已 Dispose 的服务调 `TagCountsAsync` 抛 `ObjectDisposedException`，成为全局未处理异常；
  3. 后台 `UpsertChunkAsync(旧服务)` 同样会抛；
  4. `_galleryItems.Clear()` 与后台 Add 对非线程安全 List 并发。
- 改法：
  1. 保存 `_scanTask`；新入口先 Cancel 并 await 旧任务（吞异常），再 Dispose 与清理状态；
  2. 增加 `_scanGeneration` 代次计数；旧续体发现代次不符即静默 return，不再触碰状态行与集合；
  3. finally 中的 `RefreshTagDataAsync` 包 `catch (ObjectDisposedException / SQLiteException)`，失败仅写诊断日志（App.WriteDiagnosticLog）。
- 验收/测试：大目录扫描中立即切换目录——无崩溃、无"已取消/失败"状态闪现、状态行只显示新扫描、startup.log 无未处理异常；自动恢复启动后立即手动打开图库的场景复测通过。
- 来源：review-scope-vm round1

### cr/P1-2 [P1] 扫描中激活筛选导致瀑布流重复卡片

- 维度：B
- 文件：`ViewModels/WaterfallViewModel.cs:40-66`、`ViewModels/MainViewModel.cs:1588-1618`
- 问题：筛选 `ResetFrom`（索引查询命中集合）与扫描渐进 `AppendChunkFromScan`（uiBuffer 攒批窗口内"已入索引但尚未投递 UI"的项）发生重叠——同一图片出现两张卡片。
- 改法：`WaterfallViewModel` 维护"已呈现路径集合"（`HashSet<string>`，OrdinalIgnoreCase 比较）：
  - `ResetWith` 时重建集合；
  - `AppendChunkFromScan` 在既有谓词过滤后，再按路径查重，命中则跳过并维护集合。
- 验收/测试：大目录扫描中点筛选，全列表无重复路径、`FilterStatsText` 与卡片数一致；取消筛选回全量视图亦无重复；去重逻辑可提纯为可测类并补单元测试。
- 来源：review-scope-vm round1

### cr/P1-3 [P1] 三段高亮死代码链（合并 vm/C-1 + views/C-2）

- 维度：C
- 文件：`ViewModels/MainViewModel.cs:290-300 / 2762-2786 / 2803-2814`（FileNamePrefix / FileNameTagSegment / FileNameSuffix 三属性 + 赋值 + 清空）、`Views/SingleImageView.xaml.cs:104-108`（订阅）、`Views/SingleImageView.xaml:92`（注释"三段高亮"）
- 问题：底部文件名栏移除后，FileNamePrefix / FileNameTagSegment / FileNameSuffix 三个属性已零消费，形成"属性定义—赋值—属性变更订阅—注释"整条死代码链。
- 改法：删除三属性定义及其赋值/清空代码；订阅列表移除三个 `nameof(...)`；`SingleImageView.xaml:92` 注释改为"显示名（剥离标签段）"口径。
- 验收/测试：build 通过（x:Bind 编译期强制可发现遗漏）；右栏文件名与 tooltip 刷新行为不回归。
- 来源：review-scope-vm + review-scope-views round1（同源合并：vm/C-1 与 views/C-2）

### cr/P1-4 [P1] PRD"回车进入单图"未实现

- 维度：A
- 文件：`MainWindow.xaml.cs:236-269`（OnPreviewKeyDown）、`Views/WaterfallView.xaml.cs`
- 问题：PRD 核心需求 4 明确含"双击或回车进入单图"，但 `VirtualKey.Enter` 全库零处理；叠加 cr/P1-6（卡片用 Tapped 导致 UIA 不可达）后，键盘用户完全没有进入单图的路径。
- 改法：`OnPreviewKeyDown` 的 `match is null` 分支仿照 Ctrl+A 的接管口径处理 Enter（条件：无修饰键 + 当前位于 Gallery 视图 + HasGallery）：取选中集首项进入单图（若当时无选中项，实现时拍板：无操作或取列表首项），复用 `RaiseCardDoubleTapped` 既有管线；若用户已自定义绑定该键则用户绑定优先、不接管。
- 验收/测试：图库中按 Enter 进入单图、Esc 返回后滚动位置保持；焦点在 TextBox 内按 Enter 不触发；与快捷键表无冲突时才接管；快捷键 help 面板同步口径。
- 来源：review-scope-views round1

### cr/P1-5 [P1] 卡片回收不释放缩略图位图与拖拽小图——内存无界增长

- 维度：B / E
- 文件：`Views/WaterfallView.xaml.cs:205-212`（OnElementClearing 仅取消加载）、`ViewModels/GalleryItemViewModel.cs:210-215 / 250-268`
- 问题：卡片回收时只调用 `CancelThumbnailLoad`；已加载的 `Thumbnail`（bucket 360 约 300KB+）与 `_dragVisual`（SoftwareBitmap 约 70KB）永久留在 VM 上，而 Items 全量常驻——滚动 N 张累积约 400KB×N，PRD 5 万张验收场景必然内存爆掉。
- 改法：`GalleryItemViewModel` 新增 `ReleaseVisuals()`（UI 线程调用，置 null 释放 `Thumbnail` 与 `_dragVisual`）；`OnElementClearing` 在取消加载后调用之；卡片重新 Realize 时走既有缩略图重载路径恢复。
- 验收/测试：5 万张目录滚动数分钟内存平稳（任务管理器/诊断日志观察无持续爬升）；回收后的卡片重新滚入视口时缩略图正常渐入。
- 来源：review-scope-views round1

### cr/P1-6 [P1] 卡片主交互用 Tapped 违反 RULE 硬约束

- 维度：A / B
- 文件：`Views/WaterfallView.xaml:45`
- 问题：RULE 明文"交互用 Click 不用 Tapped"（Tapped 对键盘/UIA 静默失效）；卡片 Border+Tapped 组合使 UIA Invoke 无法选中卡片，叠加 cr/P1-4 后键盘用户无进单图路径。
- 改法（两选项，默认执行选项 A）：
  - 选项 A（默认）：卡片外层换 `Button` + `CardButtonStyle`（透明底模板对齐 Ghost 系、CornerRadius 10、Background 沿用 CardBackground 绑定），Click 替代 Tapped；CanDrag / DragStarting / DoubleTapped / Pointer 系列事件照常挂接。
  - 选项 B：若实现时评估拖拽 + 双击组合与 Button 冲突而维持 Border，须用户显式豁免并回写 RULE。
- 验收/测试：UIA Invoke 可选中卡片（Accessibility Insights / UIA 验证工具）；Ctrl/Shift 选择不回归（`shift-click-test.ps1`）；拖拽、双击进单图、单击选择实机走查通过。
- 来源：review-scope-views round1

### cr/P1-7 [P1] EXIF 方向未计入扫描宽高比——竖拍卡片比例错

- 维度：B / A
- 文件：`Services/LibraryScanService.cs:185-319`（ReadImageDimensions@189-229 / **ReadJpegDimensions 主体@232-319**，review-full round2 修正锚点）、关联 `Services/ThumbnailService.cs:224-229`（缩略图已烘焙 EXIF 旋转）
- 问题：JPEG SOF 段读出的 w/h 未按 Orientation 旋转，Orientation 5-8 的竖拍照片 AspectRatio/卡片槽比例错误，与缩略图实际像素分叉（现代手机照片占比高），违背 PRD"缩略图保持原始宽高比"的验收。
- 改法：`ReadJpegDimensions` 在逐段跳过解析时识别首个 APP1(Exif) 段的 Orientation 标签（0x0112），值 5/6/7/8 时交换 w/h 再返回；PNG/GIF 无此问题不处理；索引数据可重建，自愈无兼容负担。
- 验收/测试：新增测试构造 APP1 Orientation=6 的最小 JPEG（按 T-SC8 手工字节法扩展），断言宽高交换后 AspectRatio 与缩略图一致；T-SC8 既有用例不回归。
- 来源：review-scope-services round1；锚点修正 review-full round2

### cr/P1-8 [P1] 自然排序双实现无 parity 测试

- 维度：C-orch
- 文件：`Models/GalleryItem.cs:85-140`（GalleryItemNaturalComparer / Tokenize）与 `Helpers/NaturalStringComparer.cs:13-95`
- 问题：同语义的自然排序独立双实现（D9 决策保留）分别决定瀑布流序与单图翻页序，无任何测试锁定行为一致，未来任一侧改动将静默分叉。
- 改法：tests 补 parity 用例（xunit Theory）：对 `img1 / img2 / img10 / img20 / a02 / a2 / 9999999999 / 混合段` 等输入断言两个比较器排序结果完全一致；实现代码不动。
- 验收/测试：parity 测试通过；人为改动任一 Tokenize 实现时测试变红（防分叉生效）。
- 来源：review-scope-services round1

---

### cr/P2-1 [P2] OpenLibraryRootAsync：ClearAllItemsAsync 在 try 外 + 启动恢复 fire-and-forget 失败静默

- 维度：B
- 文件：`ViewModels/MainViewModel.cs:556-573 / 614-660`、`App.xaml.cs:136`
- 问题：`ClearAllItemsAsync` 调用位于 try 块之外，一旦抛异常（如索引目录只读）直接逃逸；App 侧启动恢复是 fire-and-forget，失败完全静默无日志无状态提示。
- 改法：`ClearAllItemsAsync` 移入 try 内，或整体包 try-catch 写 `ScanStatusText` + `App.WriteDiagnosticLog`；App 侧 fire-and-forget 补 `ContinueWith` 记录异常。
- 验收/测试：把索引目录设为只读后启动——状态行出现失败文案、日志有记录、进程不崩溃、仍可再打开其它图库。
- 来源：review-scope-vm round1

### cr/P2-2 [P2] 死代码/注释漂移五处

- 维度：C
- 文件与改法：
  1. `ViewModels/SettingsViewModel.cs:57-62`：`AvailableCommands` / `AvailableCommandNames` 零引用，删除；
  2. `ViewModels/MainViewModel.cs:116-121`：双段 summary 注释内容重复，合并为一段；
  3. `ViewModels/MainViewModel.cs:1019`：`ApplyTagToSelectionAsync` 唯一调用方就在其内部调用链——内联或改 private；
  4. `ViewModels/MainViewModel.cs:1715`：`capturedName` 冗余变量删除；
  5. `ViewModels/TagSidebarViewModel.cs:57`：`GroupId` 注释改为"配置被外部修改的防御"口径（"未分组虚拟组"已删除，旧注释漂移）。
- 验收/测试：build 通过；设置页/快捷键打标功能回归正常。
- 来源：review-scope-vm round1

### cr/P2-3 [P2] 筛选状态机零测试，逻辑下沉 Core

- 维度：G
- 文件：`ViewModels/MainViewModel.cs:1543-1618`
- 问题：标签筛选状态机（无修饰单选 / Ctrl 加减选 / 无标签⇄标签互斥）纯逻辑却内嵌 VM，零自动化测试覆盖，仅靠实机走查。
- 改法：提取纯函数到 `Services/TagFilterState.cs`（`Toggle(current, untagged, tagName, ctrl)` 与 `Matches(tags, ...)`），MainViewModel 改为薄包装；补 xunit 用例：无修饰单选重置、唯一选中再点取消、Ctrl 加、Ctrl 减、无标签⇄标签双向清位、Matches 三分支。
- 验收/测试：新增单测全绿；实机三个入口（侧栏/工具栏/快捷键）筛选行为一致不回归。
- 来源：review-scope-vm round1

### cr/P2-4 [P2] SettingsPage 死方法 OnCommandSelectionChanged + SettingsViewModel.NotifyCommandSelectionChanged

- 维度：C
- 文件：`Views/SettingsPage.xaml.cs:80-83`、`ViewModels/SettingsViewModel.cs:139-146`
- 问题：SelectedItemImage 挂接已删除后，这两个通知方法成为死代码对。
- 改法：两方法删除（`OnSelectedItemPropertyChanged` 已覆盖通知需求）。
- 验收/测试：build 通过；命令切换时参数面板显隐正常。
- 来源：review-scope-views round1

### cr/P2-5 [P2] MainWindow OnRootGridSizeChanged 空方法 + XAML 死挂接

- 维度：C
- 文件：`MainWindow.xaml:15`、`MainWindow.xaml.cs:216-221`
- 问题：方法体为空，挂接无任何作用（最小尺寸逻辑已走 AppWindow.Changed）。
- 改法：删除挂接与空方法。
- 验收/测试：build 通过；窗口 resize 触发的重解码行为不变。
- 来源：review-scope-views round1

### cr/P2-6 [P2] RebuildInlinesInto 的 trim 参数恒为 false

- 维度：C
- 文件：`Views/SingleImageView.xaml.cs:254-276`
- 问题：唯一调用点恒传 `trim: false`，参数分支不可达。
- 改法：折叠为固定行为（None/Wrap），调用直达或逻辑内联。
- 验收/测试：右栏文件名多行展示不变。
- 来源：review-scope-views round1

### cr/P2-7 [P2] 侧栏文件头/XAML 头注释过时

- 维度：C
- 文件：`Views/TagSidebarControl.xaml.cs:4`、`Views/TagSidebarControl.xaml:21-22`
- 问题：注释仍写"不再读取修饰键"，与实现矛盾（现行为：无修饰=单选筛选、Ctrl=加减选）。
- 改法：对齐方法级口径——"无修饰=单选筛选、Ctrl=加减选"。
- 验收/测试：注释审阅通过，无行为变化。
- 来源：review-scope-views round1

### cr/P2-8 [P2] WrapPanel 与 TagSidebarConverters 寄居 TagSidebarControl.xaml.cs

- 维度：C
- 文件：`Views/TagSidebarControl.xaml.cs:23-91 / 296-555`
- 问题：两个可复用类型寄居在控件代码后置文件中，文件职责混杂超 500 行。
- 改法：拆分为 `Views/WrapPanel.cs` 与 `Views/TagSidebarConverters.cs`（Views 目录走 SDK 隐式通配，零 csproj 改动）。
- 验收/测试：build 通过，无行为变化。
- 来源：review-scope-views round1

### cr/P2-9 [P2] 跨文件常量三组缺锚点

- 维度：C-orch
- 文件与改法：
  1. `MainWindow.xaml.cs:90` 魔数 `+8` ↔ `MainWindow.xaml:326` InfoBar Margin `12,4`——提取 `InfoBarVerticalMargin = 8` 常量 + 锚注释；
  2. `MainWindow.xaml:130/146` 左栏 280/36——补"与 SingleImageView.SidebarExpandedWidth / CollapsedWidth 一致"锚注释；
  3. `WaterfallView.xaml:120` 冗余 `Height=48` 删除（RowDefinition 已定 48）+ 锚注释。
- 验收/测试：build 通过；布局像素级不变。
- 来源：review-scope-views round1

### cr/P2-10 [P2] MasonryLayout 零自动测试

- 维度：G
- 文件：`Views/MasonryLayout.cs`
- 问题：瀑布流布局核心算法完全无自动化测试，仅靠实机目测。
- 改法：布局核心提取为 Core 纯类型 `Helpers/MasonryPlanner`（`AppendItem(ratio)`→位置、`ComputeColumns` / `ComputeCardWidth` / `ExtentHeight`），MasonryLayout 委托调用；tests 补 T-MS1~T-MS4：尾部追加不重排（D15 决策）、列数变化全量重算、Count 回退重算、`max(2, ⌊w/240⌋)` 列数口径。
- 验收/测试：新单测全绿；实机瀑布流布局不回归。
- 来源：review-scope-views round1

### cr/P2-11 [P2] LibraryIndexService.RebuildAsync 扫描主体不在 Task.Run

- 维度：B
- 文件：`Services/LibraryIndexService.cs:160-202`、`Services/ILibraryIndexService.cs:17`
- 问题：接口 Invariants 声明"全部 Task.Run"，但 RebuildAsync 的扫描主体（await foreach 循环）在线程池委托外执行，调用方线程同步参与枚举，属声明与实现漂移。
- 改法（二选一，默认执行选项 A）：
  - 选项 A（默认）：`await foreach` 循环整体包 `await Task.Run(...)`；
  - 选项 B：两侧文档改为"调用方须后台上下文"。
- 验收/测试：T-IX5 通过；走查确认枚举不落在调用方线程。
- 来源：review-scope-services round1

### cr/P2-12 [P2] SettingsService.Load v1→v2 迁移回写不容错

- 维度：B
- 文件：`Services/SettingsService.cs:46-52`
- 问题：v1→v2 迁移后立即回写磁盘，回写抛异常（IO/权限/InvalidOperation）会使 Load 整体失败且发生在击键热路径上；`ValidateNoDuplicateBindings` / `ValidateBindings` 对 `Shortcuts` / `group.Tags` 为 null 无防御，损坏配置直接 NRE。
- 改法：迁移回写包 try-catch（IO / UnauthorizedAccess / InvalidOperation），失败静默返回已迁移的内存对象；两个 Validate 对 null 补空防御（null 视为空集合）。
- 验收/测试：测试构造"含重复绑定的 v1 配置"与 `"shortcuts": null 的 v1 配置`，Load 均返回可用对象不抛异常。
- 来源：review-scope-services round1

### cr/P2-13 [P2] 260 路径边界口径 > 应为 >=

- 维度：B
- 文件：`Services/TagFilenameService.cs:142`
- 问题：PRD 口径为"将达到 260 即阻止"，当前 `>` 判断放行了恰好 260 的路径。
- 改法：`>` 改 `>=`；T-TF5 补"恰好 260 被拒"断言。
- 验收/测试：新断言通过；T-TF5 / T-TG7 不回归。
- 来源：review-scope-services round1

### cr/P2-14 [P2] LibraryIndexService Dispose 与 RunCommand 竞态——lock 内不复查 _disposed 重建连接泄漏

- 维度：B
- 文件：`Services/LibraryIndexService.cs:221-252 / 329-330`
- 问题：RunCommand 在 lock 内重建连接前不复查 `_disposed`，Dispose 与并发命令交错时会在已 Dispose 后再建新连接，造成连接泄漏。
- 改法：RunCommand 两个重载在 lock 内首行检查 `_disposed`，为真抛 `ObjectDisposedException`。
- 验收/测试：新增测试：Dispose 后调用任一公共方法均抛 ObjectDisposedException。
- 来源：review-scope-services round1

### cr/P2-15 [P2] QueryByTags LIKE 转义测试缺 % 与 \ 字面用例

- 维度：G
- 文件：`tests/SimpleViewer.Tests/LibraryIndexServiceTests.cs:72-106`（T_IX_03）
- 问题：LIKE 子句的 `%` 与 `\` 转义逻辑无字面量用例锁定，转义回归会静默通过。
- 改法：增设 `a%b` 与 `a\b` 标签行 + 对照组（`axb` / `ab`），断言仅字面命中。
- 验收/测试：新断言通过；人为去掉 `\` 转义时测试变红。
- 来源：review-scope-services round1

### cr/P2-16 [P2] 标签名校验未拒绝文件系统非法字符/路径分隔符

- 维度：D / B
- 文件：`Services/TagFilenameService.cs:83-100`（ValidateTagName）、`Services/SettingsService.cs:174-184`（ValidateTagGroups 标签名校验段）
- 问题：两处校验只拒绝空白与方括号，不拒绝 `\ / : * ? " < > |` 等 `Path.GetInvalidFileNameChars()` 成员。用户可创建 `a:b`、`x\y` 类标签，打标时 Compose 合成的目标文件名含非法字符 → `File.Move` 抛 IOException 整批回执"重命名失败"（前置校验缺口，用户难定位）；含 `\` 标签理论上可拼出跨目录路径分量（低概率下文件被移出图库目录）。
- 改法：`ValidateTagName` 增补"含 `Path.GetInvalidFileNameChars()` 任一字符即拒绝"；`SettingsService.ValidateTagGroups` 对齐同口径（或复用 ValidateTagName 单一口径）；拒绝文案说明"不允许文件系统非法字符"。
- 验收/测试：TagFilenameServiceTests 补 `a\b`、`a/b`、`a:b`、`a*b` 均 Validate 失败；SettingsServiceTests 补"新建含 `\` 标签被拒"；既有用例不回归。
- 来源：review-full round2

### cr/P2-17 [P2] diff 触碰/新增文件内英文 doc 注释中文化

- 维度：F
- 文件与改法（逐处，改为中文并保留 API 名/代码引用）：
  1. `Views/SettingsPage.xaml.cs:17`（类级 "Keyboard shortcut settings UI hosted in a ContentDialog."）
  2. `Views/SettingsPage.xaml.cs:85`（TrySave 的 "Validates and saves bindings..."）
  3. `ViewModels/SettingsViewModel.cs:60`（"Command names for ComboBox (x:Bind friendly)."）与 `:138`（"Called from settings UI when command ComboBox selection changes."）
  4. `Helpers/ImageSourceHelper.cs:12`（新增文件类注释英文首句 "Maps LoadedImage from Core into WinUI ImageSource instances."）
- 依据：PRD 风险 5 拍板"存量英文文案本次迭代一并中文化"，触碰/新增文件属交付质量面。
- 验收/测试：注释审阅通过无行为变化；`git grep '^\s*///\s*[A-Za-z]'` 在上述文件零命中。
- 来源：review-full round2（open question 7 升格并入，K 节第 2 条同步并入本条）

---

## Spec deviations

状态：**全部已闭合（fixed）**，无 open 项。处置记录如下（对应回写动作见「K 节建议」）：

| 偏差 | 处置 | 留痕 |
|------|------|------|
| 批量移除标签能力收窄（移除走详情页 ✕ 逐图 / 同名收编） | 用户 2026-09-19 拍板"接受收窄" | K 类步骤回写 PRD 需求 5 |
| LastLibraryRoot 启动自动恢复 | 用户拍板"保留并补记文档" | K 类步骤回写 spec 兼容性说明 |
| 无标签筛选（untagged-filter-entry） | 已有敏捷留痕：`features/untagged-filter-entry` 下 prd/spec | 随 K 类一并回写主 PRD |
| 单图显示口径（剥离名 + tooltip） | 已拍板有留痕 | K 类统一回写 PRD/spec 消除文本漂移 |
| 成功回执静默 | 已拍板有留痕 | 同上 |
| 清空筛选按钮移除 | 已拍板有留痕 | 同上 |
| 未分组忽略 | 已拍板有留痕（`features/ungrouped-tags-ignore`） | 同上 |

## Open questions / 待拍板

以下未认定项不阻塞 fix-spec 执行，列请用户拍板：

1. 标签重命名部分失败后配置/文件分裂口径：失败 > 0 时是否保存配置，需产品拍板。
2. 选择状态机是否随 cr/P2-3 一并下沉 Core。
3. `_dragPayload` 拖拽取消残留清理（DragOver 中途取消/结束后 payload 复位）。
4. `CardDragFormat` 单窗口语义留档（数据格式仅本窗口使用，是否需要常量语义说明）。
5. 触屏 hover 替代方案（无指针悬停设备的卡片操作可达性）。
6. `ThumbnailService.MigrateCache` 单 bucket 取舍；以及 thumbcache 磁盘缓存无容量上限/清理策略（几十万张全量浏览可达数 GB，是否加 LRU 清理，与前者同族合并审议）。
7. 缩略图磁盘缓存键稳定性：键=sha1(规范化路径)，打标即改名 → 该图缓存永久失效（重开图库视口内重新解码一次；虚拟化下实际影响数十张）；是否改稳定键（目录+剥离名+大小）待拍板。
8. TagSpaces 多段方括号边缘行为：`a[x][y].jpg` 只解析尾段 `[y]`，`[x]` 并入基名不进聚合；单段为主流场景，互操作口径待确认（低优先）。
9. 打标批量失败无诊断日志落盘（仅 InfoBar 回执），是否补 `tag:apply` DiagnosticTrace 打点。

## 已豁免（用户确认不修）

- 无。本轮无用户豁免条目。

## 合并后 QA（manual_user）

以下为 C 类实机走查项，不阻塞 fix-spec-ready，合并后请用户验收：

1. 拖拽小缩略图手感与"打标 N 张"caption 表现。
2. Ctrl/Shift 选择与标签加减选手感（修饰键注入不可达，只能实机）。
3. 滚轮放大置底观感。
4. ∅（无标签）按钮激活配色与浅色主题表现。
5. 重命名失败回执 InfoBar 表现。
6. 5 万张实库性能口径（对应 cr/P1-5 验收）。

## K 节建议（下游执行时闭合）

1. PRD/spec 文档回写（消除文本漂移，对应 Spec deviations 四组）——**已闭合（2026-09-19 批 D）**：prd.md 与 spec.md 已按下列口径回写（修订标记统一为"2026-09-19 CR 修订"，spec 各 Step 追加短注不重写原文）：
   - PRD 需求 5 回写"批量移除标签收窄"口径（移除走详情页 ✕ 逐图 / 同名收编）；
   - spec 补记 LastLibraryRoot 启动自动恢复的兼容性说明；
   - 无标签筛选入口随 `features/untagged-filter-entry` 留痕回写主 PRD；
   - 单图显示口径（剥离名 + tooltip）/ 成功回执静默 / 清空筛选按钮移除 / 未分组忽略，统一回写 PRD/spec。
2. SettingsPage 类级英文注释中文化（F 维收尾顺手处理）。
3. `LibraryIndexService.RebuildAsync` 是否接线产线，由下游结合 open question 1（重命名部分失败口径）一并决定。
