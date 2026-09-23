# CR Fix Spec: batch-tag-management 首轮评审修复（diff 模式，tag 双 feature）

## 元信息

- repo: `D:\Dev\Python\simple_viewer`
- base_sha: `b8b2511`（master）
- head_sha: `233dc19`（feature/batch-tag-management）
- prd_path: `docs/iterations/图片打标签与瀑布流浏览/features/batch-tag-management/prd.md`（只读参考）
- spec_path:
  - `docs/iterations/图片打标签与瀑布流浏览/features/batch-tag-management/spec.md`（只读参考）
  - `docs/iterations/图片打标签与瀑布流浏览/features/tag-filter-tree/spec.md`（只读参考）
- review_round: 2（round1 四路评审 → round2 spec-fix 补 DEV 回写条目 + review-full 终校验；终校验新增 full/B-1、full/B-2 两条增补，主代理 trivial 豁免直接闭合）
- dag_version: 4
- 状态: fix-spec-ready（2026-09-23 主代理宣布：末轮 full 校验后 P0=P1=P2=0 未写入项、无 open spec_deviations）
- 行号说明: 以下文件行号已对照 233dc19 工作树逐一核实；与评审原始记录有小幅偏移处按实际行号落盘（问题认定与改法方向不变）。

---

## Must-fix（按 P0 → P1 → P2）

本轮 P0 = 0；P1 = 3；P2 = 13；合计 16 条（round1 十四条 + review-full 终校验增补 full/B-1、full/B-2）。

### vm/B-1 [P1] 删除选中集与打标管线无互斥闸，可并发改文件

- 维度: B（竞态/防重入）
- 文件: `ViewModels/MainViewModel.cs:2912`（`DeleteSelectionAsync`，守卫@:2914 只查 `_isDeleteSelectionRunning`）、`:1292`（`ApplyTagToPathsAsync` 守卫只查 `_isTagOperationRunning`）、`:1668`（`RemoveTagFromSelectionAsync`，守卫@:1670 同）、`:1150`（`ApplyTagByShortcutAsync`，守卫@:1152 同）、`:1258`（`ApplyTagToDraggedCardsAsync`，入口消费 `_dragPayload` 后进入打标管线）
- 问题: 批量打标进行中（`_isTagOperationRunning=true`，非模态）工具栏「删除」可触发 `DeleteSelectionAsync`——两条并发改文件管线（TagService 改名 vs SHFileOperationW 回收站删除）作用于同一选中集。交错后果：`SyncRenamedItemsAsync` 的 File.Exists 双探测读到旧不在/新在但即将被删 → MigrateCache+ReplaceGalleryItemState 把卡片/索引更新到已进回收站的路径（幽灵卡片，重开图库才对账）；反向则删除管线对已改名文件失败聚合，回执口径混乱。spec D9 只规定了删除自身防重入，未覆盖与打标互斥。
- 改法: 双向检查——① `DeleteSelectionAsync` 开头守卫加 `if (_isTagOperationRunning) return;`；② `ApplyTagToPathsAsync`、`RemoveTagFromSelectionAsync`、`ApplyTagByShortcutAsync`、`ApplyTagToDraggedCardsAsync` 入口守卫将 `_isTagOperationRunning` 扩为 `_isTagOperationRunning || _isDeleteSelectionRunning`；注释说明双向互斥语义。
- 验收/测试: 打标进行中点删除按钮 no-op（走查断言）；删除进行中触发打标/快捷键打标 no-op；正常路径 118 项测试全绿不回归。
- 来源: review-vm round1

### qa/B-1 [P1] 12 个验证脚本 settings.json 备份还原存在「崩溃残留→好备份被毁」窗口

- 维度: B
- 文件: `scripts/` 下 12 个脚本（备份段行号已按 233dc19 核实）——
  - 先删后拷式（`if (Test-Path $backup) { Remove-Item $backup -Force }` 再 `Copy-Item`）: `filter-panel-verify.ps1:20-21`、`toolbar-catalog-verify.ps1:20-21`、`undefined-panel-verify.ps1:22-23`、`walkthrough2-verify.ps1:21-22`、`single-strip-verify.ps1:19-20`
  - `Copy-Item -Force` 直接覆盖式: `zoom-path-verify.ps1:22-23`、`probe-strip-text.ps1:6-7`、`repro-freeze.ps1:27-28`、`repro-freeze2.ps1:7-8`、`repro-freeze3.ps1:6-7`、`repro-freeze4.ps1:5-6`、`repro-freeze5.ps1:6-7`
- 问题: 上次脚本中途崩溃（finally 未执行，残留 .bak-* 与已被污染的 settings.json）后再次运行，两种变体都会把被污染的 settings 当作新备份源，唯一好备份被删/覆盖，finally 还原的是坏文件——用户真实 shortcuts/tagGroups/LastLibraryRoot 永久丢失。该模式已被 RULE 转正为「UI 交付前实机自检」方法论，本迭代 verify-full 期间实际发生过一次还原踩踏。
- 改法: 12 个脚本统一在备份段前置守卫——检测到残留备份时先 `Move-Item $backup $settings -Force` 还原并输出 STALE-BACKUP-RESTORED 标记，再重新备份；或检测到残留即中止提示人工确认。注意脚本为 PowerShell 5.1 需 UTF-8 BOM（RULE）。
- 验收/测试: 手工制造残留场景（放一个假 .bak + 污染 settings）跑任一脚本，确认先还原后备份；正常路径行为不变。
- 来源: review-qa round1

### full/B-1 [P1] 互斥闸漏单图打标管线根入口（vm/B-1 增补）
- 维度: B（竞态/防重入）
- 文件: `ViewModels/MainViewModel.cs:1357-1364`（`ToggleTagOnCurrentImageAsync`，守卫@:1359 只查 `_isTagOperationRunning`）
- 问题: 删除确认后 `DeleteToRecycleBin` 执行期间非模态，双击选中卡片可经 `OpenImageAsSingle`（:619，无守卫）进单图，单图右栏 ✕（`RemoveCurrentImageTagAsync`@:1437）、单图目录点选（`ApplyCatalogTagAsync`@:1722）直达 `ToggleTagOnCurrentImageAsync`——与 SHFileOperationW 删除对同一文件并发改名，交错后果与 vm/B-1（失败聚合/幽灵路径）同构。快捷键路径会被 vm/B-1 改法②拦住，但其余单图入口漏网；下游按 vm/B-1 原清单执行后，验收「删除进行中触发打标 no-op」在单图路径不成立。与 open_questions #5（是否抑制导航）无耦合——管线根部守卫无条件正确。
- 改法: `:1359` 守卫扩为 `_isTagOperationRunning || _isDeleteSelectionRunning`（一行）；随 vm/B-1 一并实施。
- 验收/测试: 并入 vm/B-1 验收——删除进行中单图右栏 ✕ / 目录点选 no-op；正常路径 118 项测试全绿。
- 来源: review-full round2（遗漏扫描）

### svc/C-1 [P2] ImageLoaderService.cs MigrateCache 注释缩进不一致

- 维度: C（质量）
- 文件: `Services/ImageLoaderService.cs:154-157`（核实：方法开头四行中文注释，评审记录为三行 :154-156，实际 154-157 四行均为 12 空格缩进；包围代码——if 块与 `lock` 语句——8 空格）
- 问题: 方法开头四行中文注释缩进 12 空格，包围代码（lock 语句）8 空格，编辑残留。
- 改法: 四行注释缩进恢复 8 空格。
- 验收/测试: 视觉核对；build 0 警告。
- 来源: review-svc round1

### vm/B-2 [P2] 未定义区命令与右栏 ✕ 为 fire-and-forget，管线级异常被 `_ =` 静默吞掉

- 维度: B（异常路径）
- 文件: `ViewModels/TagSidebarViewModel.cs:248-249`（`_ = _owner.RemoveTagFromLibraryAsync(tagName)` / `_ = _owner.AbsorbUndefinedTagAsync(tagName)`）；根因 `MainViewModel.cs:1486`（`RemoveTagFromLibraryAsync` 的 await `ConfirmUndefinedDeleteAsync` 与 `QueryByTagsAsync` 在 try 之外）、`:1668`（`RemoveTagFromSelectionAsync` → `ApplyTagToPathsAsync` 管线级异常）、`:1560`（`AbsorbUndefinedTagAsync` 的 `PickAbsorbGroupAsync` 裸露）
- 问题: `_ = task` 丢弃后异常进 UnobservedTaskException——无 InfoBar 回执、仅延迟落全局 UnobservedTaskException 日志（App.xaml.cs:40-44），用户视角「点了没反应」；与 [RelayCommand] async 路径（异常回 UI 全局 handler）两通道行为不一致。
- 改法: 在 MainViewModel 公共方法体内最外层包 try/catch(Exception)——覆盖 `RemoveTagFromLibraryAsync` / `AbsorbUndefinedTagAsync`(string) / `RemoveTagFromSelectionAsync` / `ApplyTagToDraggedCardsAsync`，catch 内 `ShowInstantTagFeedback(InfoBarSeverity.Error, 操作名, ex.Message)`；TagSidebarViewModel 调用点保持不动。覆盖清单增补见 full/B-2（目录对话框链路）。
- 验收/测试: 临时注入抛错路径（或代码审查证明 try 全覆盖），确认 InfoBar 出错误回执而非静默。
- 来源: review-vm round1

### full/B-2 [P2] 目录对话框点选同为 fire-and-forget，vm/B-2 覆盖面漏此链路（增补）
- 维度: B（异常路径）
- 文件: `Views/TagCatalogDialog.xaml.cs:257-259`（`_ = _viewModel.ApplyCatalogTagToSelectionAsync(...)` / `_ = _viewModel.ApplyCatalogTagAsync(...)`）；两条下游链路（`ApplyTagToPathsAsync`@MainViewModel.cs:1319-1328 / `ToggleTagOnCurrentImageAsync`@:1402-1423）均 try/finally 无 catch，`RunTagOperationAsync`（:1766）内部亦不吞异常
- 问题: 与 vm/B-2 同类——图库右栏「＋批量目录」与单图目录点选两条管线级异常进 UnobservedTaskException，无 InfoBar 回执，用户「点了没反应」；round1 四路均未覆盖。
- 改法: vm/B-2 的方法体 try/catch 覆盖清单扩至 `ApplyCatalogTagAsync` 与 `ApplyCatalogTagToSelectionAsync`（或在 `ApplyTagToPathsAsync`/`ToggleTagOnCurrentImageAsync` 管线根部统一包 try/catch，一并覆盖 vm/B-2 原四方法，二选一以改动面小者为准）。
- 验收/测试: 并入 vm/B-2 验收——注入抛错路径出 InfoBar 错误回执。
- 来源: review-full round2（遗漏扫描）

### vm/B-3 [P2] 未定义区连锁删除候选转换 O(N×M) 线性扫描，大标签场景 UI 线程长时间卡死

- 维度: B（性能边界）
- 文件: `ViewModels/MainViewModel.cs:1520-1526`（foreach queried → `FindPresentedItemByPath` 线性扫描；该方法定义@:1906，评审记录 :1990 为偏差，已核实修正）
- 问题: 候选集来自 `QueryByTagsAsync` 全库命中（本 feature 目标场景恰是误打大标签，可达数万~十万级），转换循环在 UI 线程做 命中数×呈现集 次字符串比较，分钟级冻结风险。
- 改法: 循环前对 `_waterfall.Items` 预建 `Dictionary<string, GalleryItem>`（StringComparer.OrdinalIgnoreCase）一次 O(M)，循环内 O(1) 查找，总 O(N+M)；常规场景零行为变化。
- 验收/测试: 代码审查 + 现有测试全绿；可选走查大标签场景确认无卡顿。
- 来源: review-vm round1

### ui/B-1 [P2] 批量目录与收纳对话框 ShowAsync 无单开守卫（同暴露面覆盖不一致）

- 维度: B
- 文件: `MainWindow.xaml.cs:599-622`（`ShowSelectionTagCatalogDialogAsync`，ShowAsync@:616）、`:728-795`（`PickAbsorbGroupAsync`，ShowAsync@:793）
- 问题: 本 diff 新增四个 ContentDialog 宿主中两个删除确认框有守卫（:665-672、:705-712 try/catch 按取消处理），批量目录与收纳对话框没有——已有对话框打开时 UIA 交错 Invoke 右栏「＋」或未定义区菜单项 → ShowAsync 抛异常冒泡（正是本仓库实机自检实锤过的场景）。
- 改法: 两处 `await dialog.ShowAsync()` 补同款 try/catch：批量目录吞异常按关闭处理；收纳对话框 catch 返回 (null, null) 等价取消，finally 内 `_shortcutsEnabled` 复位已有。
- 验收/测试: UIA 交错触发不复现异常（参照既有守卫的实机验证模式）。
- 来源: review-ui round1

### ui/C-1 [P2] MainWindow.xaml 内联 Flyout.FlyoutPresenterStyle 死代码双信号源

- 维度: C
- 文件: `MainWindow.xaml:137-148`
- 问题: XAML 内联 FlyoutPresenterStyle（MinWidth/MaxWidth=700）永不生效——OnFilterFlyoutOpening 每次用 BuildFilterFlyoutStyle 整体覆盖；双信号源改一处易漏。
- 改法: 删除 XAML 的 `<Flyout.FlyoutPresenterStyle>` 块，注释指向 BuildFilterFlyoutStyle（单源）。
- 验收/测试: 筛选 flyout 打开宽度行为不变（filter-panel-verify 复跑）。
- 来源: review-ui round1

### ui/C-2 [P2] TagEditKind.DeleteTag/DeleteGroup 枚举注释仍是旧连锁语义

- 维度: C（文档滞后）
- 文件: `ViewModels/TagSidebarViewModel.cs:40-44`
- 问题: doc 注释仍写「从引用它的图片文件名移除该标签」「级联移除组内全部标签」，与本迭代新口径（仅删定义、标签落入未定义区）矛盾；TagEditDialog.BuildDescription 文案已改而枚举注释漏改。
- 改法: 两条 summary 改为「仅移除定义（0 文件改名），文件上的标签保留并落入未定义标签区」口径。
- 验收/测试: 文本核对。
- 来源: review-ui round1

### ui/C-3 [P2] GallerySelectionPanel 宽度常量+XAML 双轨

- 维度: C
- 文件: `Views/GallerySelectionPanelControl.xaml.cs:41-44`（注释宣称单源，:43-44 构造函数内统一赋值）vs `.xaml:25`（`Width="280"`）、`.xaml:163`（`Width="36"`）
- 问题: 注释宣称「XAML Border 不写死、收敛单源」，实际两 Border 仍写死 Width，值当前一致但改值时两处同步靠人记。
- 改法: 删除 XAML 两处 Width（构造函数在 InitializeComponent 后统一应用，首次布局前生效），使单源声明成立。
- 验收/测试: 右栏 280/36 收展形态像素不变（walkthrough2-verify 复跑）。
- 来源: review-ui round1

### ui/H-1 [P2] Border 上的死 AutomationId（无 automation peer，UIA 不可见）

- 维度: H（可达性）
- 文件: `Views/GallerySelectionPanelControl.xaml:28`
- 问题: `AutomationProperties.AutomationId="GallerySelectionPanel"` 挂 ExpandedPanel Border 上——WinUI Border/Grid 无 automation peer（RULE 实锤），死锚点且诱导后续脚本按它查找。
- 改法: 删除该 AutomationId；注释注明右栏锚点由收起按钮 + SelectionPanelTitleText 承担。
- 验收/测试: 现有脚本（用 GallerySelectionPanelCollapse 定位）不受影响。
- 来源: review-ui round1

### ui/H-2 [P2] 并集 chip 与 ✕ 移除按钮无 UIA Name，读屏/自动化无法区分

- 维度: H
- 文件: `Views/GallerySelectionPanelControl.xaml:116-142`（chip 本体 Button@:116 与内嵌 ✕ 按钮@:131-140 均无 AutomationProperties.Name）；对照先例 `Views/TagCatalogDialog.xaml:99`（已补 `AutomationProperties.Name="{x:Bind TagName}"`，评审记录 :100 为偏差，已核实修正）
- 问题: chip 本体 Button（StackPanel 内容）UIA Name 为空，内嵌 ✕ 按钮恒名「✕」——多 chip 并存无法区分移除目标；对照本迭代 TagCatalogDialog.xaml:99 已补 AutomationProperties.Name=TagName 的先例。
- 改法: ✕ 按钮补 AutomationProperties.Name（x:Bind 函数生成「移除标签 {name}」）；chip 本体补「{name} {count}」类 Name。
- 验收/测试: UIA 树枚举可区分各 chip 与其移除按钮。
- 来源: review-ui round1

### qa/A-2 [P2] PRD/spec 三处「遮盖式布局铁律」旧口径残留，与走查拍板自相矛盾

- 维度: A（文档同步未兑全）
- 文件: `docs/iterations/图片打标签与瀑布流浏览/features/batch-tag-management/prd.md:121`（约束节）；同目录 `spec.md:145`（不变清单）、`:164`（Context Bundle constraints）
- 问题: 三处仍写「图库右栏为浮层遮盖，收展只改变遮盖范围，不改变瀑布流几何」，与同文件已修订的 prd:76 / spec:46（2026-09-23 走查拍板布局列）自相矛盾——后续迭代按约束节/不变清单实施会把布局列改回浮层。
- 改法: 三处追加修订标注，对齐 RULE 铁律②新口径（仅约束单图画布侧；图库右栏为布局列，收展改变瀑布流可用宽度、经 resize 同路径重排）。顺手：`docs/.iteration-state.yaml:12` 的 spec_summary「ContentAreaGrid 浮层 280/36」字样同步刷新。
- 验收/测试: 文本核对三处一致。
- 来源: review-qa round1

### qa/C-1 [P2] 12 个脚本硬编码本机绝对路径

- 维度: C
- 文件: `scripts/` 全部 12 个新脚本（exe 路径 12 处，行号已核实: `filter-panel-verify.ps1:43`、`toolbar-catalog-verify.ps1:83`、`undefined-panel-verify.ps1:72`、`walkthrough2-verify.ps1:81`、`single-strip-verify.ps1:63`、`zoom-path-verify.ps1:66`、`probe-strip-text.ps1:19`、`repro-freeze.ps1:39`、`repro-freeze2.ps1:19`、`repro-freeze3.ps1:19`、`repro-freeze4.ps1:20`、`repro-freeze5.ps1:22`；均指向 `D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe`；截图目录 1 处 `filter-panel-verify.ps1:32`，评审记录 :31 为偏差，已核实修正）
- 问题: 仓库克隆到其它机器/路径即全族失效，与 RULE 把该模式转正为实机自检方法论矛盾。
- 改法: 以 `$PSScriptRoot` 锚定仓库根推导 exe 与输出目录（`Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'`）。
- 验收/测试: 脚本从任意工作目录运行可定位 exe（本机复跑任一脚本）。
- 来源: review-qa round1

### qa/A-3 [P2] tag-filter-tree spec 四处文字回写（消除与实现的字面矛盾，回写已拍板为既成决策）

- 维度: A（文档同步）
- 文件: docs/iterations/图片打标签与瀑布流浏览/features/tag-filter-tree/spec.md（D4 一处、D5 两处、筛选应用口径一处）
- 问题: 四处 spec 文字与既成实现矛盾（实现功能等价、demo 同构或代码注释已留档），后续迭代按字面实施会走回头路：①D4「树为不可变编辑模型：每次编辑产生新树（或深拷贝改后替换引用）」vs 实现为会话内可变树+编辑后全量重建渲染快照（TagFilterState.cs 头注释已声明该约定）；②D5「组头/条件行下拉」形态 vs 实现两态切换按钮组（规避 Flyout popup 层内 ComboBox 二级下拉主题失控，属 spec R1 同族风险规避）；③D5「值选择首选 Button.Flyout 二级浮层」vs 实现直接采用 R1 降级形态（行内展开勾选区）；④ApplyTagFilter 的 PresentsExactly 跳过重置（命中序列与当前呈现一致时跳过 ResetFrom+清选中，纯增强）未记录，后续评审者按「ApplyTagFilter 必清选中」旧口径会误判。
- 改法: 对 spec.md 四处追加修订标注（沿用本仓库「追加修订标记不重写原文」惯例，参照 batch-tag-management/spec.md D5 修订样式）：①D4 表述改「可变树 + 编辑后全量重建（与 demo refreshAll 同构）」；②D5 记录两态切换按钮为既成决策及理由；③D5 记录值选择行内展开为既成降级决策；④筛选应用口径补 PresentsExactly 跳过重置备注——回写锚点定 D1（spec:27 附近「内存过滤后按 SortKey 排序再 ResetFrom」处；spec 无独立「筛选应用」节）。
- 验收/测试: 四处修订标注就位，与实现口径一致；不改动其它章节。
- 来源: review-svc/review-ui/review-vm round1（DEV-CR-1~4，用户确认按回写处置）

---

## Spec deviations

状态: **fixed（4 条，随本 fix-spec qa/A-3 回写闭合）**——用户已确认按「回写 spec 文字」处置，不再阻塞。四条内容保留原描述（DEV-CR-1 可变树+全量重建 / DEV-CR-2 两态按钮 / DEV-CR-3 行内展开降级 / DEV-CR-4 PresentsExactly），处置动作见 must-fix qa/A-3。

- **DEV-CR-1**: tag-filter-tree spec D4「树为不可变编辑模型」vs 实现「会话内可变树+编辑后全量重建」（与 demo refreshAll 同构，TagFilterState.cs 头注释已声明）→ 建议 spec D4 表述改「可变树+全量重建」。
- **DEV-CR-2**: tag-filter-tree spec D5「组头/条件行下拉」vs 实现两态切换按钮（规避 Flyout 内 ComboBox 主题失控，代码注释留档）→ 建议回写 spec 记录既成决策。
- **DEV-CR-3**: tag-filter-tree spec D5「值选择首选 Button.Flyout 二级浮层」vs 实现直接采用 R1 降级形态（行内展开勾选区）→ 建议回写 spec 记录降级为既成决策。
- **DEV-CR-4**: ApplyTagFilter 的 PresentsExactly 跳过重置（命中序列与当前呈现一致时跳过 ResetFrom+清选中，走查修复轮引入的纯增强）→ 建议 spec 修订备注补记，防后续评审按旧口径误判。

## Open questions / 待拍板

评审提出但未认定，不阻塞 fix-spec 执行：

1. SoftwareBitmap 生命周期（LRU 逐出不 Close，依赖 GC——建议大图库实机内存峰值验证后定论）。
2. EXIF 90°/270° 下 DecodedWidth/Height 与位图实际宽高可能互换（base 版同口径非回归，建议竖拍图实机抽查）。
3. FromLoadedImageAsync 的 DecodedPixelData 兜底分支已无生产者（可后续迭代清理）。
4. SHFileOperationW 不支持超长路径（接口注释未声明边界）。
5. 删除进行中进入单图模式（有 FileNotFound 兜底，待商榷是否抑制导航）。
6. ApplyTagFilter 尾部无条件 RebuildTagSidebar→SettingsService.Load 磁盘 IO 频率（面板实时编辑使频率升高，可议会话内快照）。
7. 「已选 N 张」状态行与右栏标题双信号（走查未打回，留用户拍板）。
8. ChromeBackgroundBrush 无 HighContrast 字典（建议高对比度冒烟或补 Default 兜底）。
9. T_FO 测试对真实回收站的累积残留（前缀可识别，CI 长期性待商榷）。
10. 脚本断言不回传退出码（转 CI 门禁前需补）。
11. T_FT1~14 命名风格与 T_PR_01 族不一致（口味问题）。

## 已豁免（用户确认不修）

- 暂无。

## 合并后 QA（manual_user）

- 走查尾款五项（iteration-state notes 已挂账）：浅色主题 ChromeBackgroundBrush 初值实测、未定义区连锁删除/收纳实机端到端、回归族（拖拽打标懒生成/单图目录/单图 Delete/Esc/Ctrl+A）、大选中集（Ctrl+A 数万张）体感、窄窗口 280+280 形态。
- 本轮评审新增实机缺口：收纳进组 B2/B3 路径零脚本覆盖（重名拒绝 B3 未验）、空选中点＋提示 C4 未验、A4 重命名连锁+筛选树引用联动回归、拖拽打标必须用户实机复测（SendInput 移动注入在本机被丢弃无法自动化）、筛选面板深树形态（3 层嵌套+矮窗口）部分覆盖。

## K 节建议（下游执行时闭合）

- `Views/WaterfallView.xaml.cs:169-176` OnCardDragStarting 方法级注释残留旧结论（「同步禁用 GetDeferral」）与 :200 新注释/实现自相矛盾——顺手修正。
- G 测试缺口：TrimImageFilesAfterDeletion（`MainViewModel.cs:3003-3030`，四种边界纯逻辑）建议下沉 Core 补测；WaterfallViewModel.PresentsExactly 可选下沉；FileOperationService 文件不存在/空白路径分支、TagProjection 重复标签口径显式锁定、TagFilterState 空值条件产段+QuickAdd 嵌套子组合并、LibraryIndexService RemovePathsAsync 空列表分支。
- 脚本模板重复（12 脚本各自复制 W* P/Invoke 类）——若转长期工具建议抽公共 ps1（低优先）。
- repro-freeze3~5 依赖 dotnet-stack 全局工具未在脚本头声明。
- FilterAddValueButton AutomationId 多条件行重复（单条件脚本未暴露，多条件脚本需按行加后缀）。
