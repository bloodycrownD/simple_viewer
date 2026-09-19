---
date: 2026-09-19
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# tag-group-tree-ui Feature PRD

## 背景与变更动机

父级 PRD 已交付"互斥/非互斥标签组"数据模型与平铺式左侧标签栏（组头 + 组内 chip 胶囊流式换行）。用户提出两项变更：

1. **概念更名**：标签组分为「互斥组」与「兼容组」两种——互斥组内标签相互互斥（一张图片同组只能存在一个），兼容组内标签可共存；组与组之间标签相互兼容，可共存。该语义与既有"互斥/非互斥"模型一一对应（兼容组 = 非互斥组），属术语统一，非语义变更；一期不支持组嵌套（现状亦无嵌套结构）。
2. **标签库 UI 重构为目录树**：用户给出 Eagle 风格标签树参考图，明确要求"文件树 UI"——每个标签是独立的行式树节点（缩进 + 竖向连接线 + 右对齐计数），点击标签组行展开/折叠内部标签；第一轮交付的"组头可折叠 + chip 流式布局"被用户否决后返工为真目录树。

## 范围说明（相对原需求）

- 数据层（Models/Services/settings.json v2 schema）**零改动**：`TagGroup.Exclusive: bool` 天然表达互斥/兼容（false=兼容组），互斥语义纯函数 `TagSemantics.Apply` 不变，标识符（`Exclusive`、`TagEditKind.ToggleExclusive`）保留以保数据兼容。
- UI 层（UI 工程）：
  - 侧栏节点模板从 chip 胶囊流式换行改为**行式树节点**（父行：chevron + 组名 + 互斥/兼容徽章 + 右对齐计数；子行：缩进 + 竖向连接线 + [互斥组] 单选圆点 + 标签名 + 右对齐计数）。
  - 点击组行整行切换展开/折叠；默认全展开；展开状态会话内记忆（跨 Rebuild 保留，不落盘）。
  - 组行管理按钮（＋⇄✎✕）与标签行编辑按钮（✎✕）改为**悬停浮现**（Opacity 0↔1，不脱离 UIA 树），保持行面干净。
  - 术语「多选/非互斥」在用户可见文案与注释中统一更名「兼容组」。
- demo 交互原型同步树形化与术语更名。
- 不变：打标/互斥替换/筛选/批量/快捷键语义与入口、TagEditDialog 流程、顶部筛选条、瀑布流角标、未分组虚拟组行为（同享展开/折叠，管理按钮本就隐藏）。

## 影响模块与接口

| 模块 | 变更 |
|---|---|
| Views\TagSidebarControl.xaml(.cs) | 节点模板重做（行式树）、chevron/连接线/行背景等 x:Bind 双值函数、悬停浮现事件处理；chip 专属颜色函数与 ChipCoreButtonStyle 退役删除 |
| ViewModels\TagSidebarViewModel.cs | 新增 `_collapsedGroupIds` + `ToggleGroupExpansion`（触发 MainViewModel.RebuildTagSidebar）；`TagGroupViewModel.IsExpanded` |
| ViewModels\MainViewModel.cs | `RebuildTagSidebar` 由 private 改 public；打标反馈文案微调 |
| App.xaml | 新增 GroupHeaderButtonStyle / TagTreeRowButtonStyle |
| Views\TagEditDialog、Models\TagGroup、Services\ITagService | 术语更名（文案/注释，无行为） |
| demo\（index.html/demo.css/demo.js/README.md） | 侧栏树形化 + 术语更名 |

## 验收标准

- [x] 标签库呈两级行式目录树：组行（chevron ▾/▸ + 组名 + 互斥/兼容徽章 + 右对齐计数）、标签行（缩进 + 连续竖向连接线 + 互斥组单选圆点 + 标签名 + 右对齐计数）。
- [x] 点击组行整行展开/折叠内部标签行；默认全展开；展开状态经打标/计数刷新等 Rebuild 后仍保留。
- [x] 悬停行浮现管理/编辑按钮；点击这些按钮不误触发展开/折叠；opacity-0 状态下 UIA（自动化/辅助功能）仍可命中。
- [x] 互斥语义经树行入口回归：加「待筛选」→ 同组点「已选中」自动替换（文件名 `photo_1[风景 已选中].jpg`）；再点移除恢复 `photo_1[风景].jpg`。
- [x] 用户可见文案不再出现「多选/非互斥」，统一「互斥组/兼容组」。
- [x] 既有 63 个单测全绿（数据层零改动回归）。

## 测试用例

1. UIA 树断言：组行/标签行为独立 Button 元素，折叠后子行从 UIA 树消失、chevron ▸，展开恢复 ▾（实机通过）。
2. 打标链路：单图模式下点标签行 → 文件按 TagSpaces 协议重命名、互斥组替换、回执文案正确（实机通过）。
3. 管理按钮隔离：标签行 ✎ 打开重命名对话框且组未折叠、状态不受影响（实机通过）。
4. 折叠态持久：折叠主题组后连续 3 次打标触发 Rebuild，折叠态保持（实机通过）。
5. 像素级核查：缩进区竖向连接线连续贯穿子行、互斥组 radio dot 渲染（放大截图通过）。
6. 单测回归：dotnet test 63/63；build.ps1 通过；XAML BOM 断言通过（子代理 + 主代理双重执行）。
