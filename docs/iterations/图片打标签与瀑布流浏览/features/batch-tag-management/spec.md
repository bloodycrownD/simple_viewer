---
date: 2026-09-22
---

# 批量标签管理（两层语义 + 图库右栏 + 工具栏场景化）技术规格（SPEC）

## 需求来源

- PRD：`docs\iterations\图片打标签与瀑布流浏览\features\batch-tag-management\prd.md`（2026-09-22，已确认）
- 父 PRD：`docs\iterations\图片打标签与瀑布流浏览\prd.md`（dependency）
- 探索：2026-09-22 四路子代理探索报告（配置层 CRUD 与连锁管线 / 图库右栏与选中集 / 工具栏与删除管线 / 测试构建与历史决策），本 spec 变更点均可追溯到报告证据（文件路径:行号）。
- 分支：`feature/batch-tag-management`，**base = `feature/tag-filter-tree` @ `4d68321`**（依赖其 TagFilterState 树联动与筛选面板成果；tag-filter-tree 未合并 master，本迭代不得 rebase 到 master）。

## 设计目标

1. 两层语义分流：配置层操作（删定义）0 文件改名；事实层操作（未定义区删除、右栏 ✕、图库删除选中集）才是数据变更，均有确认与回执。
2. 批量移除直达：图库选中集 → 右栏 chip ✕ 一键批量移除，管线与回执口径与批量打标完全一致（分批 25、就地同步、成功静默/部分失败 InfoBar）。
3. 工具栏按模式显隐，删除按钮语义与模式一致（图库=删选中集，单图=Delete 快捷键删当前图）。
4. Core 可测逻辑先行：未定义集合投影、选中集并集计数、批量回收站删除、批量索引删除全部下沉 Core。

## 总体方案

语义分流的落点是**把「候选集来源」与「执行管线」正交组合**：

| 操作 | 候选集来源 | 执行管线 | 是否改文件 |
|---|---|---|---|
| 删除标签/组定义 | —（纯配置） | `SaveSettingsAndRebuildSidebar` | 否 |
| 重命名标签（不变） | `QueryByTagsAsync` | `RenameFilesAsync` → `RenameTagAsync` | 是（连锁，现状保留） |
| 未定义区「删除」（连锁真删） | `QueryByTagsAsync([tag])` | `RunTagOperationAsync(remove:true)` | 是 |
| 未定义区「收纳」 | —（纯配置） | `ExecuteAddTag` + `ValidateTagGroups` | 否 |
| 右栏 chip ✕ 批量移除 | 选中集路径（内存） | `RunTagOperationAsync(remove:true)` | 是 |
| 右栏「＋」批量添加 | 选中集路径（内存） | `RunTagOperationAsync(remove:false)` | 是 |
| 图库删除选中集 | 选中集路径（内存） | 新批量回收站删除 + 索引批量删 | 是（删文件） |

数据流增量：

- **未定义集合** = `_latestTagCounts`（索引全表聚合，天然含未定义标签，`LibraryIndexService.TagCountsCore:479`）键集 − 配置组标签集（OrdinalIgnoreCase）。计算下沉 Core 新文件 `Services\TagProjection.cs`，`TagSidebarViewModel.Rebuild`（:123，已有 `configuredNames` 集合）改为消费投影结果。**服务层（索引/TagService）零改动成立**。
- **选中集并集** = `foreach _selectedCards → vm.Item.Tags` 内存枚举 → Core 投影纯函数（并集 + 选中集内计数）。挂点两个：`OnSelectedCardCountChanged`（:2733）与打标/移除收口（`ReplaceGalleryItemState` :2452 之后——`UpdateFrom` 不通知 `Item`，需主动触发重算）。

## 拍板决策（D1~D12）

- **D1 Core 投影文件**：新建 `Services\TagProjection.cs`（纯 POCO + 静态函数，Core 无 MVVM 包约束）：`ComputeUndefinedTags(counts, configGroups)` 与 `ComputeSelectionTagUnion(tagSequences)`。`Services\**` 通配自动编入 Core（SimpleViewer.Core.csproj:12-18），零 csproj 改动。
- **D2 配置删除纯化**：`ExecuteDeleteTagAsync`（:2083）/`ExecuteDeleteGroupAsync`（:2127）重写为纯配置删除——**快捷键绑定引用拒绝前移**（删定义前读 `_settingsService.Load().Shortcuts` 判 `ApplyTag + TagId`；**删除组时组内任一标签被引用即整体拒绝**——现状 `DeleteGroupAsync` 无绑定校验，不前置将落进 ValidateBindings 错文案；拒绝文案统一沿用 `TagService.DeleteTagAsync:76-80` 现文案，否则 `ValidateBindings` 会在 Save 时以「引用的标签不存在」错误文案拒绝，口径不符 A2）；保留 `TagFilterState.RemoveTagReferences` 摘树调用与 `ApplyTagFilter` 刷新。`TagService.DeleteTagAsync`/`DeleteGroupAsync` 连锁语义迁走后删除（绑定拒绝谓词随迁）；`RenameFilesAsync`（:2181）收窄为重命名专用（唯一残留调用方 `ExecuteRenameTagAsync`）。
- **D3 未定义区连锁删除管线**：新方法 `RemoveTagFromLibraryAsync(tagName)`——`QueryByTagsAsync([tagName])` 取候选（继承「无索引→拒绝」现状口径）→ 候选转 GalleryItem（`FindPresentedItemByPath`/`TryBuildCandidateFromPath`，同 `ApplyTagToPathsAsync:1236` 模式）→ `RunTagOperationAsync(remove:true)` → `ShowTagOperationResult` → `RefreshTagDataAsync`。回执/进度/就地同步与批量打标完全一致（PRD B1），**不走不分批的 RenameFilesAsync**。
- **D4 收纳 = 目标组建同名定义**：复用 `ExecuteAddTag`（:1984）+ `SettingsService.ValidateTagGroups` 组内/跨组重名拒绝（B2/B3 天然覆盖），0 文件改名。目标组选择走新 ContentDialog（宿主注入模式，同 `ShowTagEditorAsync` 先例）。
- **D5 图库右栏宿主**：`MainWindow.xaml` 的 `ContentAreaGrid`（:240）内图库 Grid（`GalleryVisibility` 显隐那层）右缘叠加浮层（`Canvas.ZIndex=1`、`HorizontalAlignment=Right`）——随 GalleryVisibility 单图模式天然隐藏、位于工具栏行之下无需 TopChromeHeight 避让、不在 ChromeLayer 根不受 XamlCompiler 顺序坑影响。新控件 `Views\GallerySelectionPanelControl.xaml(.cs)`，双 Border 展开 280 / 折叠 36（仿 `SingleImageView` InfoPanelOverlay/CollapsedBar :56-263；宽度常量入 `SingleImageView.xaml.cs:44-47` 同族锚点注释）。遮盖式布局铁律：不占布局槽、不改瀑布流几何。**（2026-09-23 用户走查修订：图库右栏改为布局列**——图库 Grid Row2 改 `*`/Auto 双列，右栏与画布同级、像左栏一样占布局槽，收展改变瀑布流可用宽度、经窗口 resize 同一路径触发重排；遮盖式铁律仅约束单图画布侧（防重解码），瀑布流本就响应宽度重排，浮层遮盖会藏住缩略图被用户打回。）
- **D6 并集重算策略**：全量重算（O(选中集 × 平均标签数) 内存字典运算），挂点两个：`OnSelectedCardCountChanged`（:2733）与**操作收口**（`RunTagOperationAsync`/`RenameFilesAsync` 完成后触发一次——所有改标签路径（批量打标/单图右栏 ✕/重命名/未定义区连锁删）汇入 `SyncRenamedItemsAsync`→`ReplaceGalleryItemState`（:2452，`UpdateFrom` 不通知 `Item`）同一漏斗，挂收口天然全覆盖；**不在 ReplaceGalleryItemState 内部逐文件触发**，避免 N 次重算放大 O(N²)）。容忍 `SelectSingleCard`/`SelectCardRange` 先清后加的 0→n 中间态通知（重算幂等，中间态闪变可接受）。本期不做防抖/差量化（数万张选中预计 <100ms，实机走查验证，超预期再议）。
- **D7 批量目录三态**：`TagCatalogEntry` 的 `IsApplied` 二值改三态——`AppliedToAll`（选中集全部已含 → 禁点）/`AppliedToSome`（部分含 → 可点，再点幂等替换）/`None`。`TagCatalogDialog` 新增批量构造变体（快照 = 配置组 + 选中集并集 + 选中数），XAML 的 `Not(IsApplied)` 绑定、`AppliedSuffix` 与 `RadioDotFill`/`RadioDotStroke`/`TagRowNameForeground` 三组 x:Bind 函数绑定同步改签名（编译器强制，无实施歧义）；单图口径构造保留不动（单图只产生 AppliedToAll/None 两态）。
- **D8 工具栏左右簇**：`ToolBarRow`（MainWindow.xaml:39-188）内 ScrollViewer 下改 Grid 双列（左簇左对齐 / 右簇右对齐），窄窗口横向滚动保留。模式显隐走新派生可见性属性（`GalleryOnlyControlsVisibility`/`SingleOnlyControlsVisibility`）挂 `OnCurrentModeChanged`（:2624）通知链。「选择」按钮维持 no-op 口径（无图库/空呈现集点击空转，现状行为即 PRD「不响应」）。
- **D9 图库删除选中集**：新命令 `DeleteSelectionCommand`（图库模式按钮绑定；空选中 no-op——PRD D3）；快捷键分流在 `MainWindow.DispatchShortcut` 的 `DeleteImage` 分支（:420-426）按 `CurrentMode` 分派（Gallery→新命令，Single→现状 `DeleteAsync`）。删除顺带裁剪 `_imageFiles` 中已删路径并校正 `_currentIndex`（防回单图撞 FileNotFound——`LoadCurrentAsync` 重试兜底条件含 `_isTagOperationRunning`，删除管线不置该标志故不能依赖兜底）。
- **D10 批量删除服务**：`IFileOperationService`/`FileOperationService`（Services\，Core 可测）新增 `DeleteToRecycleBin(IReadOnlyList<string> paths)` 批量变体，返回 `BatchOperationResult`（逐文件聚合失败）。**批量变体逐文件走 `SHFileOperationW` P/Invoke**（`FOF_SILENT|FOF_NOCONFIRMATION|FOF_ALLOWUNDO|FOF_NOERRORUI`：无任何对话框、失败返回非零错误码供逐文件聚合、ALLOWUNDO=进回收站）——2026-09-22 wave-0 实编译证实 `Microsoft.VisualBasic.FileIO.UIOption` **没有 NoUI 成员**（仅 AllDialogs/OnlyErrorDialogs，spec 原文系审查建议臆造、已订正），现状单图路径的 `OnlyErrorDialogs` 失败时弹 Shell 错误对话框，批量照抄会卡死 UI 与 T_FO_02 测试；单图路径维持 VB 实现不动。`LibraryIndexService` 新增 `RemovePathsAsync(IReadOnlyList<string>)` 单事务批量删行（仿 `UpsertChunkAsync:73-83`，库内 `DeletePathsCore:203` 对账批量删可资参照）。
- **D11 Ctrl+A 口径修正（PRD 偏差）**：PRD 风险节「单图模式按 Ctrl+A 仍全选图库呈现集」与现状不符——Ctrl+A 接管条件本就含 `CurrentMode==Gallery`（MainWindow.xaml.cs:345-351），单图模式不触发。**按现状拍板：Ctrl+A 维持仅图库生效**，Step 8 回写 PRD 修正该句。「快捷键保持全模式可用」仅指快捷键表（TryMatch）内命令（Delete/翻页等），不含 Ctrl+A/Enter 接管。
- **D12 拼写与大小写口径**：未定义 chip 与右栏并集 chip 的显示拼写取**计数键/文件名侧拼写**（`_latestTagCounts` 字典键 / `GalleryItem.Tags` 原文）；大小写判定全链 OrdinalIgnoreCase（与 TagSemantics/FindGroupByTagName 同口径）。

## 最终项目结构（增量）

```
Services\TagProjection.cs                        # 新：未定义集合 + 选中集并集 Core 纯函数
Services\IFileOperationService.cs                # 改：+ DeleteToRecycleBin(paths) 批量
Services\FileOperationService.cs                 # 改：实现批量 + BatchOperationResult 聚合
Services\ILibraryIndexService.cs                 # 改：+ RemovePathsAsync(paths)
Services\LibraryIndexService.cs                  # 改：单事务批量删行
Services\TagService.cs                           # 改：删 DeleteTagAsync/DeleteGroupAsync（连锁语义迁走）
Services\ITagService.cs                          # 改：同步接口
ViewModels\MainViewModel.cs                      # 改：删除语义重构/未定义区管线/右栏状态/目录批量入口/删除选中集/工具栏可见性
ViewModels\TagSidebarViewModel.cs                # 改：Rebuild 消费 TagProjection、未定义 chip VM 与命令
Views\TagSidebarControl.xaml(.cs)                # 改：未定义区（WrapPanel chip 流式 + 菜单 + 限高滚动）
Views\GallerySelectionPanelControl.xaml(.cs)     # 新：图库右栏（280/36 收展、并集 chip、空态、＋入口）
Views\TagCatalogDialog.xaml(.cs)                 # 改：批量三态变体
Views\TagEditDialog.xaml.cs                      # 改：删除确认文案新口径
MainWindow.xaml(.cs)                             # 改：右栏宿主、工具栏左右簇、新对话框宿主、快捷键分流
App.xaml                                         # 改：圆角矩形 chip 新样式（含计数位）
tests\SimpleViewer.Tests\TagProjectionTests.cs   # 新：T_PR_01~08
tests\SimpleViewer.Tests\FileOperationServiceTests.cs # 新：T_FO_01~03
tests\SimpleViewer.Tests\LibraryIndexServiceTests.cs  # 改：T_IX_12
tests\SimpleViewer.Tests\TagServiceTests.cs      # 改：T_TG_05 口径迁移
docs\iterations\...\prd.md（父）                 # 改：三处回写修订标记
docs\iterations\...\features\ungrouped-tags-ignore\ # 改：标注部分推翻
```

## 变更点清单（文件级，证据 = 探索报告行号）

| 文件 | 变更 | 关键现状锚点 |
|---|---|---|
| `Services\TagProjection.cs` | 新建：`ComputeUndefinedTags(IReadOnlyDictionary<string,int>, IReadOnlyList<TagGroup>)` → `List<(string Name,int Count)>`（计数降序、同计数按名Ordinal）；`ComputeSelectionTagUnion(IEnumerable<IReadOnlyList<string>>)` → 并集 + 各标签选中集内计数 | 消费方 Rebuild 现有 configuredNames :132 |
| `ViewModels\MainViewModel.cs` | ① `ExecuteDeleteTagAsync:2083`/`ExecuteDeleteGroupAsync:2127` 纯化（绑定拒绝前移 + RemoveTagReferences 保留 + Save）；② 新 `RemoveTagFromLibraryAsync`（D3）；③ 新收纳执行 `AbsorbUndefinedTagAsync(tagName, groupId)`（D4）；④ 并集状态 `SelectionTagUnion`（ObservableCollection）+ `RecomputeSelectionTagUnion()` 双挂点（D6）；⑤ `ApplyTagToPathsAsync:1236` 参数化 `remove`（回执标题 remove 分支「移除标签「X」」）；⑥ 新 `DeleteSelectionCommand`（D9）；⑦ 工具栏可见性派生属性（D8） | _latestTagCounts:124、OnSelectedCardCountChanged:2733、ReplaceGalleryItemState:2452、DeleteAsync:2518 |
| `ViewModels\TagSidebarViewModel.cs` | `Rebuild:123` 接 TagProjection 产出 `UndefinedTags`（新 VM 类型 UndefinedTagChipVm：Name/Count/删除收纳命令）；头注释 :5-8 口径更新 | CreateChipEditCommands:201 模式 |
| `Views\TagSidebarControl.xaml(.cs)` | 未定义区插入滚动区尾、「＋ 新建标签组」(:271-276) 之前：区标题「未定义标签」+ WrapPanel chip 流 + chip 主点击弹 MenuFlyout（✕ 删除 / ⇱ 收纳）+ 主滚动流（随左栏整体滚动，无嵌套子滚动）+ 空集合整区 Collapsed（E1） | 组模板/行模板零触碰（tag-group-tree-ui 拍板守护） |
| `Views\GallerySelectionPanelControl.xaml(.cs)` | 新建：标题行「已选 N 张」+ ▶ 收起；并集 chip = 圆角矩形（新样式）+ 标签名 + 计数 + ✕；空态文案「选择图片后可批量移除标签」；「＋ 添加标签」按钮；折叠 36 窄条 ◀ | SingleImageView.xaml:56-263 双 Border 模式 |
| `ViewModels\MainViewModel.cs`（右栏状态） | 新 `IsGallerySelectionPanelCollapsed`（仿 IsInfoPanelCollapsed:847）+ 派生可见性（Gallery 模式限定） | OnCurrentModeChanged:2624 |
| `Views\TagCatalogDialog.xaml(.cs)` | 批量构造变体（快照重载 + 三态 IsApplied + AppliedToSome 视觉「部分」后缀 + 批量点击事件）+ Title/说明文案分支 | applied 判定 :87-98、Not(IsApplied) xaml:91 |
| `MainWindow.xaml.cs` | `ShowTagCatalogDialogAsync:543` 批量分支接线；新宿主：未定义区删除确认（影响张数+不可逆）、收纳目标组选择、删除选中集确认（批量文案）；`DispatchShortcut:385` DeleteImage 模式分流 | ConfirmDeleteAsync:568 注入模式 |
| `MainWindow.xaml` | ContentAreaGrid 图库 Grid 加 GallerySelectionPanelControl 浮层；ToolBarRow 左右簇重排 + Visibility 绑定；**MainInfoBar 保持 MainAreaGrid 之后**（:388-394 铁律） | |
| `App.xaml` | 新样式 `SelectionChipStyle`（圆角矩形 CornerRadius≈4、计数 TextBlock、✕ GhostIconButton 复用） | GhostButtonStyle:16 族 |
| `Services\TagService.cs` + `ITagService.cs` | 删除 `DeleteTagAsync:70`/`DeleteGroupAsync:86`；绑定谓词去留见 D2（拒绝逻辑前移 VM 后，App.xaml.cs:96-100 注入的谓词可一并清理） | |
| `tests\...` | 见测试策略 | TagServiceTests 的 T_TG_05(:118) 与 T_ST_05(:289) 调整（SettingsServiceTests.cs:174 另有同号 T_ST_05，勿混淆） |

## 详细实现步骤

- Step 1 — phase-core-projection — blocking: yes — qa: auto：新建 `Services\TagProjection.cs`（两个纯函数族 + POCO 结果类型）与 `tests\SimpleViewer.Tests\TagProjectionTests.cs`（T_PR_01~08）；`dotnet test` 全绿。
- Step 2 — phase-config-delete-rework — blocking: yes — qa: auto：`ExecuteDeleteTagAsync`/`ExecuteDeleteGroupAsync` 纯化（D2 全项：绑定拒绝前移、RemoveTagReferences、Save、ApplyTagFilter）；`TagEditDialog.BuildDescription:140-143` 删除文案新口径；删除 `TagService.DeleteTagAsync`/`DeleteGroupAsync` + 接口同步；`TagServiceTests` T_TG_05 改为锁定 `RemoveTagAsync` 按名移除语义、T_ST_05 口径注记迁移；build + test 全绿。
- Step 3 — phase-undefined-zone — blocking: yes — qa: auto + manual_user：`TagSidebarViewModel.Rebuild` 接投影产出未定义集 + chip VM/命令；`TagSidebarControl` 未定义区 UI（D5 之外的左栏侧全部：WrapPanel/菜单/限高/空隐藏）；`MainViewModel.RemoveTagFromLibraryAsync`（D3）与 `AbsorbUndefinedTagAsync`（D4）；MainWindow 新增两个对话框宿主（删除确认含影响张数与不可逆提示、收纳目标组选择含重名拒绝提示）。
- Step 4 — phase-gallery-panel — blocking: yes — qa: manual_user：`GallerySelectionPanelControl` 新控件 + MainViewModel 并集状态与双挂点重算（D6）+ `ApplyTagToPathsAsync` remove 参数化 + `App.xaml` 圆角矩形 chip 样式；MainWindow 挂载浮层（D5）。
- Step 5 — phase-catalog-batch — blocking: yes — qa: manual_user：`TagCatalogDialog` 批量三态变体（D7）+ 宿主接线 + 空选中「＋」提示先选择图片（C4）；**单图目录路径回归验证**（共享控件，改坏单图即打回）。
- Step 6 — phase-toolbar-mode — blocking: yes — qa: manual_user：工具栏左右簇重排 + 模式可见性派生属性（D8）；「选择」按钮 no-op 口径代码注释明确。
- Step 7 — phase-delete-selection — blocking: yes — qa: auto + manual_user：`FileOperationService.DeleteToRecycleBin` 批量（D10：逐文件 `SHFileOperationW` 无 UI）+ `FileOperationServiceTests`（T_FO_01~03）；`LibraryIndexService.RemovePathsAsync` + T_IX_12；`DeleteSelectionCommand`（确认→批量删除→锁内 `_galleryItems` 移除→索引批量删→`ScanStatusText` 刷新→`ApplyTagFilter()`（内含选中清空/命中数/侧栏重建）→`_imageFiles` 裁剪）；`DispatchShortcut` DeleteImage 分流；防重入闸（仿 `_isTagOperationRunning`）。
- Step 8 — phase-doc-sync — blocking: no — qa: auto：父 PRD 三处回写（:46 忽略口径、:62 删除连锁、:65-66 批量移除收窄，追加修订标记不重写原文，cr-fix-spec K 节模式）；`ungrouped-tags-ignore\prd.md`/`spec.md` 头部标注部分推翻（**精确到「聚合忽略」——右栏 chips ✕ 显示无组标签与卡片角标口径均不推翻**）；batch prd.md 风险节 Ctrl+A 句修正（D11）；代码注释清理（TagSidebarViewModel:5-8、TagSidebarControl.xaml:17-18、RemoveCurrentImageTagAsync:1405 注释、WrapPanel.cs:14）。
- Step 9 — phase-verify-full — blocking: yes — qa: auto + manual_user：`scripts\build.ps1` + `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj` 全绿门禁；实机走查清单（manual_user）：A1/A3 删除定义 0 改名+未定义区出现、A4 重命名标签连锁与筛选树引用改名联动回归、B1/B2/B3 未定义区三操作、C1~C4 右栏、D1~D4 工具栏与删除、E1 空区隐藏、双主题、大选中集（Ctrl+A 全选）体感、回归（单图目录打标、单图 Delete、拖拽打标、Esc 三态、Ctrl+A、筛选面板打开时快捷键抑制）。

## 测试策略

构建后跑 `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj`（勿裸 restore，见 RULE）。UI 行为（Step 3~7 的 manual_user 项）走实机走查（filter-panel-verify.ps1 模式 + 像素采样断言主题）。

### 测试用例（新前缀 T_PR / T_FO，T_IX 续号 12——下划线两位零填充对齐既有惯例；已核对未与 13 个占用缩写冲突）

- T_PR_01 — blocking: yes — 未定义集合 = counts 键集 − 配置组标签集（OrdinalIgnoreCase 差集，配置组大小写变体正确排除）
- T_PR_02 — blocking: yes — 计数保留计数键值；排序 = 计数降序、同计数按名 Ordinal
- T_PR_03 — blocking: yes — 全部标签已配置 / counts 空 → 空列表（E1 数据层）
- T_PR_04 — blocking: yes — 拼写口径：同名多拼写以计数键（文件名侧首个聚合拼写）为准，不取配置侧拼写
- T_PR_05 — blocking: yes — 选中集并集计算 + 各标签选中集内计数（如 30 张中 18 张含 Z → 「Z 18」）
- T_PR_06 — blocking: yes — 并集含未定义标签（配置组外标签不滤除——C2 数据层）
- T_PR_07 — blocking: yes — 空选中 → 空并集
- T_PR_08 — blocking: yes — 并集大小写合并口径（OrdinalIgnoreCase 同名合并计数，拼写取首见）
- T_FO_01 — blocking: yes — 批量回收站删除：N 文件成功、返回成功计数、文件不存在于原路径
- T_FO_02 — blocking: yes — 部分失败聚合（锁定句柄文件返回非零错误码进 Failures、其余成功不回滚）
- T_FO_03 — blocking: yes — 空路径列表 → no-op 成功
- T_IX_12 — blocking: yes — RemovePathsAsync 单事务批量删行，查询确认全删
- 现有调整：T_TG_05（DeleteTagAsync 连锁 → RemoveTagAsync 按名移除口径）；T_ST_05（TagServiceTests.cs:289，绑定拒绝文件不动 → 注记拒绝点迁 VM，服务层锁定 ValidateBindings 悬空拒绝不变；与 SettingsServiceTests.cs:174 同号用例无关，勿混淆）

映射：T_PR_01~04→Step 1/3；T_PR_05~08→Step 1/4；T_FO→Step 7；T_IX_12→Step 7；GWT 验收 A1/A2/A3/A4→Step 2/9（A2 拒绝为 VM 层，走查+代码审查覆盖）；B1~B3→Step 3/9；C1~C4→Step 4/5/9；D1~D4→Step 6/7/9；E1→Step 3/9。

## 风险与回滚方案

- **R1 大选中集并集重算**：Ctrl+A 数万张时全量重算 O(N×T)，预计 <100ms 但未实证——Step 9 实机走查含此场景；超预期抖动再议防抖（本期显式不做，D6）。
- **R2 左右栏同屏宽度**：左 280 + 右 280 + 最小窗宽 800 → 中间内容区约 240 逻辑 px——Step 9 实机走查窄窗口形态；缓解：右栏可收 36；必要时后续提 MinWidth（本期不动）。
- **R3 TagCatalogDialog 三态改造回归单图目录**：共享控件，Step 5 验收含单图路径回归；批量变体走独立构造函数降低耦合。
- **R4 删除选中集与扫描交错的索引复活**：扫描攒批窗口迟到 `UpsertChunkAsync` 会把已删文件写回索引——**既有口径**（单图删除同在，重开图库 `ClearAllItemsAsync` 兜底），本期不修，Step 8 文档记边界。
- **R5 工具栏 XAML 重排视觉回归**：RULE 铁律「XAML 模板改动不可免检」——Step 6 交付前实机走查（视觉 + 双主题）。
- **R6 快捷键 Delete 分流回归**：单图 Delete 现状不能坏（D4 验收）+ 图库空选中 no-op（D3 验收）；DispatchShortcut 改动集中一处。
- **R7 废弃 DeleteTagAsync 的测试红窗**：Step 2 同提交内改测试，不留中间红态。
- 回滚：分支 `feature/batch-tag-management` 逐 Step 提交，任一 Step 可单独 revert；Core 新文件纯增量、无数据迁移、无 settings schema 变化（组配置结构不变）。

## 不变清单（既有拍板守护）

- 遮盖式布局铁律：右栏/未定义区一切 UI 不改画布与瀑布流几何。
  > **修订（2026-09-23）**：图库右栏已改为布局列（D5 同日走查修订）——收展改变瀑布流可用宽度、经窗口 resize 同路径触发重排；本条铁律仅约束单图画布侧（遮盖式防重解码）。未定义区位于左栏主滚动流内、不改瀑布流几何的口径不变。
- 配置组区维持文件树风格（tag-group-tree-ui 拍板；chip 流式仅限未定义区）。
- 卡片角标维持忽略无组标签口径（本 PRD 不推翻）。
- 单图右栏（详情页）胶囊样式与行为维持现状。
- 重命名标签连锁 + `RenameTagReferences` 树联动（需求 1「保持现状」）。
- 互斥替换语义（TagSemantics）、untagged ∅ 入口、快捷键表 TryMatch 优先、批量回执口径（2026-09-19 拍板）。
- XAML 硬约束全家桶：禁 FontIcon/SymbolIcon、禁 U+00AB/U+00BB 与 PUA 字形、不用 DockPanel、**新引用 ThemeResource 键必须核对 WinUI 3 真实存在（`GallerySelectionPanelControl` 全新 XAML 控件为高险点，编译期不校验，启动即崩先例）**、中文 XAML UTF-8 BOM、Click 不用 Tapped、修饰键只判 Down、MainInfoBar 在 MainAreaGrid 之后、自绘按钮 UseSystemFocusVisuals=false、ScrollViewer overlay 右 Padding 让位。
- PRD 不包含范围守护：未定义标签不参与筛选、不作拖拽打标目标、不做重命名（改名场景走配置组连锁改名）；不做批量收纳；单图模式右栏胶囊维持现状。
- 构建约束：build.ps1 唯一入口、还原竞态按 RULE 定向串行还原、重建前 taskkill。

## Context Bundle

```yaml
iteration_name: batch-tag-management
requirement_path: docs/iterations/图片打标签与瀑布流浏览/features/batch-tag-management/prd.md
spec_path: docs/iterations/图片打标签与瀑布流浏览/features/batch-tag-management/spec.md
base_branch: feature/tag-filter-tree @ 4d68321
explore_summary: 四路探索（配置层CRUD与连锁管线/图库右栏与选中集/工具栏与删除管线/测试构建与历史决策）；关键发现：绑定拒绝需前移VM、两条批量管线抉择走RunTagOperationAsync(remove)、未定义/并集计算下沉Core新文件、右栏宿主定ContentAreaGrid图库Grid浮层、Ctrl+A现状仅图库（PRD误述）
impact_files: [Services/TagProjection.cs(新), Services/FileOperationService.cs, Services/LibraryIndexService.cs, Services/TagService.cs, ViewModels/MainViewModel.cs, ViewModels/TagSidebarViewModel.cs, Views/TagSidebarControl.xaml(.cs), Views/GallerySelectionPanelControl.xaml(.cs)(新), Views/TagCatalogDialog.xaml(.cs), Views/TagEditDialog.xaml.cs, MainWindow.xaml(.cs), App.xaml, tests/(TagProjectionTests新,FileOperationServiceTests新,LibraryIndexServiceTests,TagServiceTests)]
constraints: [遮盖式布局铁律, tag-group-tree-ui文件树拍板, 角标忽略无组标签口径, XAML硬约束全家桶, Core白名单无MVVM包, build.ps1唯一入口, MainInfoBar顺序铁律, T前缀占用清单]
  # 修订（2026-09-23）：遮盖式布局铁律仅约束单图画布侧；图库右栏已改为布局列（走查拍板）——收展改变瀑布流可用宽度、经 resize 同路径重排
blocking_steps: [1,2,3,4,5,6,7,9]
```
