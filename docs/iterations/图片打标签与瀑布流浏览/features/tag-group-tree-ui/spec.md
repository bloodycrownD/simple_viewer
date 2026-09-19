---
date: 2026-09-19
agile_trace: true
---

# tag-group-tree-ui 实现规格（SPEC）

## 根因 / 方案摘要

第一轮交付（7f8ac83/4758c26/0e93ced）把"目录树"实现为组头可折叠 + 组内 chip 胶囊流式换行，被用户否决（「听不懂什么叫文件树UI吗？」，给出 Eagle 风格参考图）。返工（5511024/d78a711）将节点模板重做为**行式树节点**。两轮共享的机制层（展开状态记忆、术语更名、demo 同步）在第一轮已定型并保留。

核心设计约束：侧栏 VM 是"不可变快照 + 全量 Rebuild"模式（无 INPC），展开状态必须外置于 `TagSidebarViewModel` 的 `_collapsedGroupIds`（HashSet，按组 Id），切换展开 = 改集合 + 调 `MainViewModel.RebuildTagSidebar()`（已改 public），重建新 VM 对象后 x:Bind OneTime 自动重求值——零 INPC 引入，完全复用既有架构（主题/筛选/扫描节流均走同一路径，展开态天然跨 Rebuild 保留）。

## 变更点清单

| 提交 | 内容 |
|---|---|
| 7f8ac83 | 组头树形化：chevron（▸ U+25B8 / ▾ U+25BE）+ 整行 GroupHeaderButtonStyle + 展开状态记忆 |
| 4758c26 | 术语更名：徽章/对话框/tooltip/反馈文案/注释「多选/非互斥」→「兼容组」（标识符不动） |
| 0e93ced | demo 树形化与术语更名（第一轮形态） |
| 5511024 | 节点模板重做：chip 流式布局 → 行式树节点（缩进/连接线/右对齐计数/悬停浮现管理按钮），删除 ChipCoreButtonStyle 与 ChipBackground 等退役函数 |
| d78a711 | demo 同步行式树观感 |

## 详细改动说明

### 行模板结构（TagSidebarControl.xaml）

- 组模板 = StackPanel(0 间距)[组行 Button + 子行 ItemsControl(Visibility 绑 IsExpanded)]；子行 ItemsControl 默认垂直 StackPanel(0 间距保证连接线连续)，ItemContainerStyle 强制 ContentPresenter Stretch 撑满行宽。
- 组行（GroupHeaderButtonStyle，MinHeight 36、CornerRadius 6、透明底、hover=SubtleFillColorSecondaryBrush）：chevron（10px 固定宽防抖动）→ 组名 → 互斥/兼容徽章 → 弹性空位 → 右对齐纯数字计数 → 悬停浮现 ＋⇄✎✕。
- 标签行（TagTreeRowButtonStyle，MinHeight 34、CornerRadius 5）：缩进列（14px，内含 1px 竖向连接线 Border 居中、行高拉伸）→ 单选圆点（仅互斥组，7×7 Ellipse，RadioDot* 颜色函数沿用）→ 标签名 → 弹性空位 → 右对齐纯数字计数 → 悬停浮现 ✎✕。
- 激活行（IsFilterActive）：整行圆角叠加高亮 TagRowBackground（IsDarkTheme 双值）；模板内独立 HoverOverlay 分层，激活行悬停时高亮不丢。
- 点击语义不变：标签行 Click → OnChipClicked + Shift 检测（打标/批量/移除/筛选四路分流）；组行 Click → ToggleGroupExpansion。

### 悬停浮现（关键取舍）

行根 PointerEntered/PointerExited → code-behind `SetRowCommandsOpacity` 经 VisualTreeHelper 按 `Name=="RowCommands"` 检索容器 → **Opacity 0↔1**（非 Visibility）：保持按钮在 UIA 树中可命中（自动化/辅助功能路径不失效——项目 RULE 踩坑约定），且占位恒定行宽不跳动。实机验证 opacity-0 下 AXPress 可达。

### 展开状态机制

`TagSidebarViewModel._collapsedGroupIds`（默认空=全展开，会话内不落盘）；`ToggleGroupExpansion(id)` 切换 membership → `_owner.RebuildTagSidebar()`；`TagGroupViewModel.IsExpanded` 只读构造注入；Rebuild 尾部按现存组 Id 交集清理已删组残留。新建组天然展开（不在集合）。settings.json 不动。

### 术语更名（无行为）

徽章 ExclusiveBadge「互斥/兼容」；TagEditDialog 全部话术（切换确认、新建组复选描述）改为「互斥组（组内单选替换）/兼容组（组内叠加共存）」表述；TagGroup.cs/ITagService.cs 注释补「兼容组（非互斥组）」映射；`Exclusive`/`ToggleExclusive` 等标识符与 settings schema 保留。

### demo（交互基准）

.tag-row 行式树 + --row-hover/--row-active/--row-line CSS 变量 + row-cmds 悬停浮现；badge「兼容」；collapsedGroups Set 会话记忆；.chip 收窄为查看器浮层专用。README 补交互说明。

## 测试策略

### 测试用例

- 单测：数据层零改动，dotnet test 63/63 全绿（回归即验证）。
- 构建：scripts\build.ps1 通过（两轮均遇已知环境问题——XamlCompiler 间歇沉默崩溃/冷重建 CS0234——按 RULE 既定流程解除，非代码问题）。
- XAML BOM：全仓 7 个 XAML 断言 UTF-8 带 BOM。
- 实机走查（主代理 UIA + 像素）：树结构断言、折叠/展开、折叠态跨 3 次 Rebuild 保留、互斥替换链路（加→替换→移除，文件名全程核对）、管理按钮不误触发且 UIA 可命中、连接线/radio dot 像素级放大核查。
- 冒烟：viewer.exe -d test-library 启动存活、startup.log 无未处理异常。

## 风险与回滚方案

- 风险：①悬停浮现依赖鼠标悬停，触屏/纯键盘用户发现性下降（UIA 路径已保，键盘 Tab 仍可达）；②默认全展开是拍板默认值，用户可推翻改默认折叠；③侧栏组色相（hue）随树行化退役，瀑布流角标/筛选条仍有组色（跨视图色彩关联减弱）。
- 回滚：5 个提交均限 UI 工程 + demo + Core 注释，`git revert 5511024 d78a711 0e93ced 4758c26 7f8ac83`（逆序）可整体回退至 4264993，无数据迁移、无 schema 变更、无配置残留。
