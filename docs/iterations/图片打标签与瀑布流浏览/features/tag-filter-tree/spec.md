---
date: 2026-09-21
---

# 复杂标签筛选器（条件树）技术规格（SPEC）

## 需求来源

非标准 PRD 输入：用户口述 + 交互设计稿 `demo\filter.html`（+ `demo\filter.css` / `demo\filter.js`，已三轮反馈收敛、用户确认"差不多"），演进记录见 `docs\apm\memory\20260921-filter-builder-ui.md`。**设计稿即 UI 验收基准**，本 SPEC 与设计稿冲突处以设计稿为准。

需求一句话：在现有"点击标签 = 多标签 OR 筛选"之上，提供纯标签的 **in（包含任一）/ not in（不包含任一）** 条件与 **and / or 嵌套条件组**（组 = 卡片、卡片嵌套卡片、最深 3 层）的复杂筛选器；实时生效、实时命中数、人话表达式；左栏点击标签 = 往根组快捷追加一条 in 条件（复杂筛选是简单筛选的超集）。无标签筛选保留左栏 ∅ 既有独立入口，高级筛选器不重复支持。

## 设计目标

1. 表达力：任意"标签 in/not in 集合"经 and/or 嵌套组合（≤3 层），如 `(含[风景 街拍] 或 含[星标]) 且 不含[已修]`。
2. 单一求值器：扫描追加块过滤与筛选应用共用同一条件树求值函数，**废除现有"索引 SQL ↔ 内存谓词"双轨等价维护**（索引层零改动）。
3. 实时性：面板内每次编辑即时反映到命中数/图墙/筛选条（无"应用"按钮），10 万级图库不卡顿（性能口径：筛选首屏 ≤2s）。
4. 兼容既有体验：左栏 ∅ 无标签入口与互斥语义不变；筛选条/侧栏高亮/计数等既有联动形态延续（内容变为条件树表达）。

## 总体方案

### D1 求值层：单一内存求值器（拍板）

- 现状 `ApplyTagFilterAsync` 走索引 SQL（`QueryByTagsAsync` 仅 OR，无法表达 not in/嵌套）。**本需求改为对内存全量暂存 `_galleryItems` 做条件树谓词过滤**，与 demo `evalNode` 同构；扫描中 `WaterfallViewModel.AppendChunkFromScan` 的追加块过滤换接同一谓词（现状机制照旧，仅换实现）。
- 性能依据：10 万项 × 小树（≤ 数十节点）求值为每项 O(条件数 × 平均标签数) 的 OrdinalIgnoreCase 比较，实测口径毫秒级，远低于 ≤2s 验收线；如极端不达标，预留"树→SQL 编译"升级路径（见风险节），但**不作为本期实现**。
- 索引层（`ILibraryIndexService` / `LibraryIndexService`）**零改动**：`QueryByTagsAsync` 保留原样继续服务 `RenameFilesAsync`（编辑候选集）；`QueryUntaggedAsync` 保留（untagged 分支同步改内存谓词后不再被筛选调用，方法暂留）。
- 命中结果排序：对齐现有筛选分支口径，内存过滤后按 `GalleryItem.SortKey` 自然序排序再 `ResetFrom`。
- 附带修复（顺带收口的既有口径差）：SQLite LIKE（ASCII 不敏感/非 ASCII 敏感）与内存 `OrdinalIgnoreCase` 对英文标签大小写口径不一致——树化后筛选全走内存谓词，该差异自然消失。

### D2 条件树模型（Core，改 `Services\TagFilterState.cs`）

纯 POCO + 静态函数（Core 无 CommunityToolkit.Mvvm / WinAppSDK，禁 ObservableObject）：

```csharp
public enum FilterOp { And, Or }
public enum FilterMatcher { In, NotIn }              // 包含任一 / 不包含任一
public abstract class FilterNode { }                  // 判别用 pattern matching
public sealed class FilterGroupNode : FilterNode {
    public FilterOp Op;                               // 根组默认 And
    public List<FilterNode> Children = [];
}
public sealed class FilterConditionNode : FilterNode {
    public FilterMatcher Matcher = FilterMatcher.In;
    public List<string> Values = [];                  // 标签名（非 TagDefinition.Id，见 D3）
}
public static class TagFilterState {                  // 保留类名与文件，替换内部函数
    public const int MaxDepth = 3;                    // 状态层强制（含根组），非仅 UI 层
    public static bool Evaluate(FilterNode node, IReadOnlyList<string> itemTags);   // OrdinalIgnoreCase
    public static List<ExprSegment> BuildExpression(FilterNode root);               // 人话表达式结构化段
    public static HashSet<string> CollectReferencedTags(FilterNode root);           // 侧栏高亮判定
    // 树编辑纯函数（全部返回新树或原位改后返回 bool，含深度校验，拒绝超深）：
    // AddCondition / AddGroup / RemoveNode / SetOp / SetMatcher / ToggleValue / Clear
    // QuickAdd(root, tagName)：demo addQuickCond 同构——已存在同值 in 条件则忽略返回 false；
    //   根组 Op=Or 且已有单值 in 条件则合并进该行；否则根组追加单值 in 条件
    // RemoveTagReferences(root, tagName) / RenameTagReferences(root, oldName, newName)：标签删除/重命名联动
    // ToggleUntagged 保留（untagged 独立位与树互斥语义不变）
}
```

求值语义（与 demo `evalCond`/`evalNode` 逐条对齐）：

- 条件 `In`：项标签与 Values **任一**命中（OrdinalIgnoreCase）；
- 条件 `NotIn`：项标签与 Values **无一**命中；
- **空 Values 条件 = 恒真（未启用）**；空组 = 恒真；根组无有效条件 = 无筛选（全量）；
- 组 `And` = 所有子节点真；`Or` = 任一子节点真。

人话表达式段（`ExprSegment`，UI 侧据此渲染筛选条 chips，demo `exprParts`/`exprChips` 同构）：

- `CondSegment { NodeId, Matcher, Values, Negated }`——文本如 `标签：风景 / 街拍`，否定段 UI 渲染红色 + 「非」前缀，✕ 删除该节点；
- `OpSegment { And|Or }`——「且/或」；`ParenSegment { Open|Close }`——非根多部件组包括号。

### D3 条件值身份：标签名（拍板）

事实源是文件名（只有标签名，无 Id）；无组标签已被侧栏忽略（2026-09-19 拍板）。条件值统一存**标签名字符串**，比较 OrdinalIgnoreCase 与全链一致；值选择器候选仅列配置组标签（与 demo 一致）；树求值对"文件名里存在但不在配置组"的历史标签照常按名匹配（不做配置组过滤，避免求值层依赖配置）。

### D4 状态归属与互斥（MainViewModel）

- `_activeFilterTags : HashSet<string>` + 树内值双状态**收敛为** `_filterRoot : FilterGroupNode`（会话态、不落盘，与现状口径一致；重开图库清空）；
- `IsUntaggedFilterActive` 保留：与树**互斥**——任一方向激活时清另一方（现状语义）；
- `_activeFilterTags` 的"实例引用稳定"约定废弃（该约定服务于侧栏高亮持有同一 HashSet；树化后高亮改为 `RebuildTagSidebar` 时以 `CollectReferencedTags` 快照传入，循既有全量重建惯例）；
- 树为不可变编辑模型：每次编辑产生新树（或深拷贝改后替换引用），编辑后统一走 `ApplyFilterAsync` → 重建 chips/侧栏——与 demo `refreshAll` 同构，UI 侧无增量状态同步负担。

### D5 UI 宿主：工具栏按钮 + Flyout 非模态面板（拍板，风险节含降级）

- 工具栏（GhostButtonStyle 文本按钮行）新增「筛选」按钮：文本字符图标（如 `⧩ 筛选 ▾`）+ 条件数徽章（有条件时高亮），`Button.Flyout` 挂面板——满足"条件实时生效、点外关闭、Esc 关闭"的非模态形态。项目弹层全是 ContentDialog 模态（Flyout 零先例），为此：
  - 主题：Flyout 内容控件根在 Opening/打开前设 `RequestedTheme = RootGrid.RequestedTheme`（照 `ApplyDialogTheme` 同款坑位处理）；代码侧颜色一律 `TagSidebarConverters.IsDarkTheme` 双值；接入 `RefreshThemeDependentVisuals` 重建链；
  - Esc：Flyout 自带 Esc 关闭；面板打开期间 `_shortcutsEnabled` 不全局禁用（非模态），但需核对与全局快捷键（翻页等）共存——面板获焦时快捷键不应触发图库操作。
- 面板本体 `Views\TagFilterPanelControl.xaml(.cs)`（新文件，UI 工程自动编入）：**组=卡片全层统一**（含最外层根组）、卡片嵌套卡片、嵌套层背景交替 + 左侧 accent 竖线、组头「满足以下 [全部/任一] 条件」下拉 + 非根组 ✕、条件行 `[包含任一/不包含任一下拉] + 标签 chips（✕ 移除）+ ＋标签 + ✕`、组底 `＋ 条件` / `＋ 条件组`（深度 <3 才显示组按钮，UI 与状态层双保险）、面板底表达式预览 + 命中数 + `清空条件` + `完成`。
- 渲染模式：循项目"全量 Rebuild 不可变快照"惯例（TagSidebarControl 两层嵌套 ItemsRepeater/ItemsControl 先例 + 面板规模小），**条件树用 code-behind 递归构造或嵌套 ItemsRepeater 均可，以实现简单为准**；值选择「＋ 标签」二级浮层首选 `Button.Flyout`（分组勾选列表 + 各标签计数，demo popover 同构），若嵌套 Flyout 在 WinAppSDK 1.6 出主题/命中问题，**降级为行内展开分组勾选区**（风险预案 R1，不阻塞形态）。
- 筛选条（MainWindow.xaml L198-286 现区域）：OR 徽章移除（语义已被表达式段覆盖）；chips 换为 `BuildExpression` 段序列渲染（条件段 ✕ 删节点、且/或/括号、否定红）；保留 `FilterStatsText` 与「无标签」chip（untagged 激活时）；「清空」入口恢复（面板内 `清空条件`；筛选条右侧 `清空`——demo 形态，推翻 untagged-filter-entry 期"移除清空按钮"的旧拍板，因表达式复杂后逐条删除不现实）。
- 左栏：chip 点击改 `QuickAdd`（无修饰=追加 in 条件；已引用则忽略——demo 拍板语义，Ctrl 修饰不再特殊处理）；`IsFilterActive` 高亮 = `CollectReferencedTags` 含该标签；∅ 按钮行为不变。

### D6 持久化：不落盘（拍板）

筛选是临时浏览意图，与现状口径一致（会话态、重开图库清空）；demo 的 localStorage 持久化是原型便利，**不进主工程**。如未来需要，走 AppSettings Version=3 迁移（SettingsService 已有 v1→v2 先例），本期不做。

## 最终项目结构

```
Services\TagFilterState.cs                # 改造：条件树模型 + 求值 + 表达式 + 编辑 + QuickAdd + 树标签联动（Core）
ViewModels\MainViewModel.cs               # 改造：_filterRoot 状态收敛、ApplyFilterAsync 内存求值、表达式 chips、左栏 QuickAdd、删/改名标签树联动
ViewModels\WaterfallViewModel.cs          # 微改：AppendChunkFromScan 谓词换树求值（经 owner 薄包装）
ViewModels\TagSidebarViewModel.cs         # 改造：Rebuild 高亮参数 = CollectReferencedTags 快照
ViewModels\TagFilterPanelViewModel.cs     # 新增：面板编辑状态 + 段/快照构建（UI 工程，可用 MVVM）
Views\TagFilterPanelControl.xaml(.cs)     # 新增：Flyout 面板（卡片嵌套条件树 UI）
Views\TagSidebarConverters.cs             # 扩展：否定红/且或括号段/层级卡片配色等双值转换
MainWindow.xaml(.cs)                      # 改造：工具栏筛选按钮 + Flyout、筛选条表达式 chips、Esc/快捷键共存
tests\SimpleViewer.Tests\TagFilterStateTests.cs  # 重写：旧 T_TF_S 语义废弃，新 T_FT 系列
demo\filter.html / filter.css / filter.js # 设计稿（验收基准，随本迭代入库）
```

csproj **零改动**（Services\ 自动进 Core；新 Views/ViewModels 自动进 UI 工程）。

## 变更点清单

| 文件 | 变更 | 性质 |
|---|---|---|
| `Services\TagFilterState.cs` | 删 Toggle/Matches 标签分支（untagged 位保留），加条件树全套纯函数 | 重构（Core 可测） |
| `ViewModels\MainViewModel.cs` | 状态/应用/联动全链换树；`OrBadgeVisibility` 删；`FilterChips` 换表达式段 | 重构 |
| `ViewModels\WaterfallViewModel.cs` | `MatchesTagFilter` 换树谓词 | 微改 |
| `ViewModels\TagSidebarViewModel.cs` | `Rebuild` 高亮口径 | 小改 |
| `Views\TagSidebarControl.xaml.cs` | chip 点击语义（Ctrl 分支删） | 小改 |
| `MainWindow.xaml` / `.xaml.cs` | 筛选按钮/Flyout/筛选条/Esc | 新增+改造 |
| 新 `Views\TagFilterPanelControl.*` / `ViewModels\TagFilterPanelViewModel.cs` | 面板本体 | 新增 |
| `Views\TagSidebarConverters.cs` | 新转换函数 | 扩展 |
| `tests\...\TagFilterStateTests.cs` | 重写 + 新增 | 测试 |
| `docs\apm\RULE.md` | Flyout 若成为新先例，记主题处理模式 | 文档 |

**既有契约变更（显式推翻，均有用户拍板依据）**：① 左栏点击三分支语义（tag-filter-select-model spec + T_TF_S01~07）→ QuickAdd；② 筛选条"无清空按钮"（untagged-filter-entry 拍板）→ 恢复清空（demo 形态）；③ OR 徽章 → 表达式段。**不变**：untagged ∅ 入口与互斥、筛选不落盘、索引层、文件名协议、遮盖式布局（Flyout 浮层不占布局槽）。

## 详细实现步骤

- Step 1 — phase-filter-tree-core — blocking: yes — qa: auto：重构 `Services\TagFilterState.cs`：按 D2 实现模型、`Evaluate`、`BuildExpression`、`CollectReferencedTags`、树编辑纯函数（含 MaxDepth=3 拒绝超深）、`QuickAdd`（demo addQuickCond 三分支同构）、`RemoveTagReferences`/`RenameTagReferences`、保留 `ToggleUntagged`。
- Step 2 — phase-filter-tree-core — blocking: yes — qa: auto：重写 `TagFilterStateTests.cs`：删旧 T_TF_S01~07 语义，新 T_FT 系列（见测试用例）；类头中文 summary 注明出处（本 spec Step）。
- Step 3 — phase-apply-pipeline — blocking: yes — qa: auto：`MainViewModel` 状态收敛与求值管线：`_filterRoot` 替换 `_activeFilterTags`；`ApplyFilterAsync` 单一内存求值（untagged/树/全量三分支全内存化，结果按 SortKey 排序）；`MatchesTagFilter` 换树谓词（`WaterfallViewModel.AppendChunkFromScan` 接线随之）；`HasAnyFilter`/`FilterStatsText` 适配；重开图库清树；删/改名标签走 `Remove/RenameTagReferences` + 重应用。
- Step 4 — phase-quick-entry — blocking: yes — qa: manual_user：左栏与筛选条接入：chip 点击 = `QuickAdd`（Ctrl 分支移除）；`Rebuild` 高亮 = `CollectReferencedTags` 快照；筛选条 chips = 表达式段渲染（条件 ✕ 删节点、否定红、且/或/括号、无标签 chip 保留、清空按钮恢复）；实机走查：左栏追加/取消、筛选条单删、untagged 互斥。
- Step 5 — phase-filter-panel — blocking: yes — qa: manual_user：工具栏「筛选」按钮（徽章/高亮）+ `TagFilterPanelControl` 面板：组卡片嵌套（含根）、组头下拉、条件行、＋条件/＋条件组（深度 <3）、表达式预览 + 命中数 + 清空 + 完成；值选择二级浮层（首选 Flyout，不稳降级行内展开）；主题双值 + Flyout RequestedTheme 对齐；全量 Rebuild 渲染。
- Step 6 — phase-panel-integration — blocking: yes — qa: manual_user：非模态共存与实时性：Esc 关面板、面板打开期间全局快捷键不触发图库操作（对齐 `_shortcutsEnabled` 机制改造为"面板聚焦时局部抑制"）、扫描进行中面板编辑实时反映（追加块经同一谓词）、单图模式下打开面板行为（回图库，对齐现状 `HandleTagChipTappedAsync` 先切回逻辑）。
- Step 7 — phase-validate — blocking: yes — qa: auto：全量验证：`scripts\build.ps1` 构建通过 + `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj` 全绿（构建后跑，勿裸 restore）。
- Step 8 — phase-validate — blocking: no — qa: manual_user：用户实机走查（对照 demo\filter.html 形态还原度、双主题截图走查（visual-band-check.py 模式）、10 万级图库性能体感、拖拽打标等既有回归）。

依赖：Step 1→2→3→4 / 5（3 完成后 4、5 可并行）→6→7→8。

## 测试策略

测试模式循 `TagFilterStateTests` 先例：纯函数直接断言、中文注释解释语义、collection expression 构造数据；前缀取 **T_FT_**（T_TF_ 已被 TagFilenameServiceTests 占用、T_TF_S 属旧语义）。

### 测试用例

- T-FT1 — blocking: yes — In 多值任一命中（→Step 1）
- T-FT2 — blocking: yes — NotIn 仅当与值集无一命中才通过（→Step 1）
- T-FT3 — blocking: yes — 空 Values 条件恒真（未启用）（→Step 1）
- T-FT4 — blocking: yes — 空组恒真；根无有效条件 = 无筛选（→Step 1）
- T-FT5 — blocking: yes — 嵌套 `(In A/B Or In C) And NotIn D` 组合求值（→Step 1）
- T-FT6 — blocking: yes — 三层嵌套边界求值正确（→Step 1）
- T-FT7 — blocking: yes — 标签比较 OrdinalIgnoreCase（→Step 1）
- T-FT8 — blocking: yes — BuildExpression 段序列：且/或/括号包裹规则（非根多部件）、否定段标记（→Step 1）
- T-FT9 — blocking: yes — 树编辑：增删条件/组、切 Op/Matcher、ToggleValue、Clear（→Step 1）
- T-FT10 — blocking: yes — MaxDepth：向第 3 层组内加组被拒绝（→Step 1）
- T-FT11 — blocking: yes — QuickAdd 三分支：已存在同值 in 忽略 / 根 Or 且有单值 in 行合并 / 否则追加（→Step 1）
- T-FT12 — blocking: yes — CollectReferencedTags 含嵌套组内全部值（→Step 1）
- T-FT13 — blocking: yes — RemoveTagReferences / RenameTagReferences 树联动（→Step 1）
- T-FT14 — blocking: yes — ToggleUntagged 保留语义 + 与树互斥的清理约定（→Step 1）
- （Step 3 管线为 UI 工程逻辑，不进单测；Step 7 以全量测试绿为门禁）

XAML/UI 不做单测（项目铁律：XAML 布局不可单测，必须实机走查）。

## 风险与回滚方案

| # | 风险 | 缓解 | 回滚 |
|---|---|---|---|
| R1 | **Flyout 零先例**：主题（popup 层不认 RootGrid.RequestedTheme，ContentDialog 已实证）与二级嵌套浮层（＋标签值选择）在 WinAppSDK 1.6 行为未知 | RequestedTheme 对齐照 ApplyDialogTheme 模式；代码色全走 IsDarkTheme 双值；值选择浮层降级行内展开（不阻塞形态）；Step 5 实机走查为 blocking 门禁 | 面板宿主整体降级为 ContentDialog 模态版（复用全部树逻辑，仅宿主换壳） |
| R2 | 左栏语义变更打破既有习惯与契约 | 用户三轮收敛明确拍板（demo 即此语义）；spec 显式列推翻清单 | git revert 该 feature 分支；旧语义测试与 spec 仍在历史可恢复 |
| R3 | 内存求值性能（10 万级实时生效） | 量级估算毫秒级；Step 8 用户实机体感 + 预留树→SQL 编译升级路径（LIKE/NOT LIKE + 括号，EscapeLikePattern 已具备），本期不实现 | 求值器是纯函数，替换实现不动 UI |
| R4 | XamlCompiler 间歇崩溃 / 新 XAML 丢 BOM | 一律 build.ps1（内置重试）；新 XAML 落盘带 UTF-8 BOM；改 MainWindow.xaml 保持 MainInfoBar 在 MainAreaGrid 之后 | 构建节既有流程 |
| R5 | 2958 行 MainViewModel 改造波及隐性依赖（单图翻页环绕、GalleryStatusText 等派生属性） | Step 3 保留全部公开派生属性签名（HasAnyFilter/FilterStatsText 等），仅换内部实现；`git grep _activeFilterTags` 全部 10 处逐一迁移 | 分支提交，逐 Step 可 revert |
| R6 | 扫描中面板实时性与索引渐进写入的时间差（现状即有：ResetFrom 只见已写入部分，靠追加块谓词补齐） | 机制照旧，仅换谓词实现；命中数口径保持 `_waterfall.Items.Count / _galleryItems.Count` | 不新增风险面 |

回滚总策略：feature 分支开发（建议 `feature/tag-filter-tree`），Step 1-3 纯 Core/逻辑层可独立合并；UI 步骤（4-6）一个提交组，可整体 revert 不伤 Core 成果。

## Context Bundle

```yaml
iteration_name: tag-filter-tree
requirement_path: demo/filter.html（设计稿）+ docs/apm/memory/20260921-filter-builder-ui.md（演进记录，非标准 PRD）
spec_path: docs/iterations/图片打标签与瀑布流浏览/features/tag-filter-tree/spec.md
explore_summary: 四路探索（筛选数据流/索引层/UI约束/测试构建）；TagFilterState 已在 Core；双求值轨（SQL OR+内存谓词）；弹层全 ContentDialog 模态、Flyout 零先例；左栏点击契约与 demo QuickAdd 冲突（7 测试需重写）；索引层可零改动（QueryByTagsAsync 被 RenameFilesAsync 复用）
impact_files: [Services\TagFilterState.cs, ViewModels\MainViewModel.cs, ViewModels\WaterfallViewModel.cs, ViewModels\TagSidebarViewModel.cs, Views\TagSidebarControl.xaml.cs, Views\TagSidebarConverters.cs, MainWindow.xaml, MainWindow.xaml.cs, 新 Views\TagFilterPanelControl.xaml(.cs), 新 ViewModels\TagFilterPanelViewModel.cs, tests\SimpleViewer.Tests\TagFilterStateTests.cs]
constraints: [Core 无 MVVM/WinAppSDK 包（纯 POCO+静态函数）, XAML 硬约束（文本字符图标/Click/UTF-8 BOM/无 DockPanel/MainInfoBar 顺序）, build.ps1 唯一构建入口, tests 只引用 Core, 遮盖式布局（Flyout 不占布局槽）, 主题 IsDarkTheme 双值+弹层 RequestedTheme 对齐, 10 万级筛选 ≤2s, 筛选不落盘]
blocking_steps: [Step 1, Step 2, Step 3, Step 4, Step 5, Step 6, Step 7]
```
