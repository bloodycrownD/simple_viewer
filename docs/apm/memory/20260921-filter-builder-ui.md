---
date: 2026-09-21 23:40
title: 复杂筛选器：设计稿三轮收敛 → spec → code-dev-loop dev-ready（tag-filter-tree）
keywords: 筛选器, 条件构建器, in, not in, and, or, 嵌套组, 卡片嵌套, filter.html, demo, TagFilterState, 人话表达式, spec, tag-filter-tree, code-dev-loop, dev-ready
abstract: 复杂筛选器全流程闭环：demo 设计稿三轮收敛（纯标签 in/not in + and/or 卡片嵌套）→ spec（8 Step、14 用例）→ code-dev-loop 9 波次 dev-ready（分支 feature/tag-filter-tree，b8b2511→302e648，107 测试全绿）。实现含条件树 Core、内存求值管线、Flyout 非模态面板、快捷键局部抑制、扫描竞态修补。open deviation 一项（两态切换按钮组 vs demo select 下拉）留用户实机走查拍板；Step 8 走查清单在 spec 与 iteration-state。
---

user:
需要一个复杂的筛选器，支持 in 和 not in、and 和 or，先在 demo html 上设计一个筛选器看 UI 设计。（随后按 APM 恢复记忆继续）

assistant:
恢复记忆后明确上下文：现有筛选是"点击标签 = 多标签 OR"（TagFilterState 已下沉 Core），交互原型约定在 demo\、改交互先对照原型。设计方案选了 Notion/Linear 式条件构建器，而不是全局 Match all/any 切换（表达力弱）或逐行连接词（语义易混）：

- 条件行 = [字段▾][操作符▾][值]：标签（包含任一 in / 不包含任一 not in，多选 chips）、无标签、文件名（包含/不包含，演示字段扩展性）；
- 嵌套条件组 = 组头「满足以下 [全部/任一] 条件」，最深 3 层 → 任意布尔组合；
- 实时生效（无应用按钮）：命中数、图墙、人话表达式全实时；空值条件恒真（构造中不会瞬间清空图墙）；
- 人话表达式双呈现：面板底纯文本 `(含(风景/街拍) 或 含(星标)) 且 不含(已修)` + 筛选条 chips（且/或/括号，否定=红 chip 带"非"字）；
- 设计主张：复杂筛选器是简单点击筛选的超集——左栏点标签 = 往根组追加 in 条件，两套筛选合一。

产出 demo/filter.html + filter.css + filter.js（纯前端、离线、双主题、140 张模拟渐变卡片、localStorage 持久化）。

user:
（自测过程中的发现与修复）

assistant:
浏览器 + 视觉模型双验收，修了 4 个真实 bug：
1. `.flyout/.popover/.wall-empty` 设 display:flex 后覆盖 HTML hidden 属性 → 全局 `[hidden]{display:none!important}`;
2. localStorage 恢复筛选树后 uid 计数器重置、新条件 id 与恢复节点撞车（findNode 命中错误节点、表达式乱套）→ loadState 时整树重编 id；
3. 筛选条「编辑」按钮：openPanel 后事件冒泡到 document 委托被判"面板外点击"立即关闭 → 外部关闭检查排除触发器；
4. popover 勾选标签整块重绘闪烁 → 改增量更新 cb 勾选态；renderPanel 保持滚动位置。

视觉模型验收两轮（亮/暗）：嵌套组层级、红色否定语义、chips 可读性均过；按反馈微调：嵌套组左边框 3px accent 竖线、"＋条件组"弱化 11px、text-2 对比度提升。

验证结论：`(含(风景/街拍) 或 含(星标)) 且 不含(已修)` → 命中 59/140 与图墙卡片数一致；筛选条括号结构 DOM 核对无误（视觉模型说过"星标在括号外"是误读）。

user:
下一步？

assistant:
用户实机看 UI（双击 demo\filter.html 即可，离线可用）。认可后走 prd-generate/spec-generate：TagFilterState 扩展为条件树（group{op,children}/cond{field,matcher,values}），求值与 demo 的 evalNode 同构下沉 Core；WinUI 实现注意 XAML 硬约束（图标用文本字符、Click 不用 Tapped、中文 XAML 带 BOM）。demo README 已补「复杂筛选器设计稿」一节。

user:
看 UI 后两轮反馈：①"支持嵌套条件组就行吧？现在有并行多个条件组，会让人疑惑第一层并行的条件组之间的关系"；②（我先做了显式连接词方案）"回滚，你没懂我意思，你看现在有多级卡片，我的意思是，只保留卡片嵌套就好了，最外层现在不是卡片，这很奇怪"（附截图）。

assistant:
两轮反馈才对准真意：用户要的是**组=卡片全层统一**——原设计最外层根组是裸组头（无边框）、下面嵌套组却是卡片，视觉层级不对称很奇怪；中间我做过的"并行元素间显式 ─且─/─或─ 连接词"方案被否（回滚掉）。最终改动：renderGroup 去掉 isRoot 特殊分支，所有组（含根）统一输出 .f-group-box 卡片（根组仍无删除按钮）；嵌套层背景色交替（bg-2 → panel → bg-2）+ 左侧 3px accent 竖线区分层级；flyout body padding 收紧；两个下拉框 min-width 88px 统一宽度。验证：构造两层嵌套状态（localStorage 注入 → loadState → renderPanel 真实渲染路径）→ 根组卡片化、嵌套卡片正常、表达式与命中自洽；视觉模型验收通过。教训：用户说"关系疑惑"时先确认指视觉结构还是逻辑语义，这轮我猜了逻辑显式化方向，实际痛点是视觉一致性。

user:
（本轮收尾）

assistant:
README 嵌套条件组行更新为"组一律渲染为卡片（含最外层根组），卡片嵌套卡片"。注意：驱动测试脚本不能拿位置索引/类名前缀定位面板元素（面板每次 refreshAll 全量重绘、行序会偏移）——要么每步重查 DOM，要么直接 localStorage 注入状态树再 reload 走 loadState 渲染路径（后者最干净）。

user:
第三轮反馈：标签/无标签/文件名字段下拉全部移除，不要这么复杂——高级筛选器只支持标签的添加和移除（in/not in）；"不包含所有标签不就是无标签吗"，且左栏本身已有无标签筛选能力，高级筛选器不用重复支持。

assistant:
按反馈砍掉字段体系：newCond 去掉 field/text，条件行只剩 [包含任一▾/不包含任一▾] + 标签 chips；FIELDS 删除（仅留 MATCHERS 常量）、evalCond/condText/chipLabel 只留标签分支、change 的 field 分支与 input（name-text）监听删除、countInFilter/addQuickCond 的 field 判断清理、CSS 的 .f-input/.f-static 删除。**loadState 加旧状态清洗**：localStorage 里带 field=name/untagged 的旧条件直接过滤掉（兼容已看过旧版的浏览器）。验证：注入含旧字段条件的状态树 → 清洗正确（表达式只剩标签条件）、面板无字段下拉、卡片嵌套正常，视觉模型验收通过。设计结论：高级筛选器 = 纯标签 in/not in + and/or 卡片嵌套；无标签走左栏既有按钮。

user:
UI 确认（"差不多"），要求生成 spec。

assistant:
spec-generate 四路并行探索完成（筛选状态与数据流 / 数据与索引层 / UI 呈现与 XAML 约束 / 测试与构建），关键现状：①TagFilterState 已在 Core（Services\ 白名单自动编入），纯静态 Toggle/ToggleUntagged/Matches（扁平 OR + untagged 位），7 测试 T_TF_S01~07 锁定；②双求值轨=索引 SQL（QueryByTagsAsync LIKE OR，被 RenameFilesAsync 复用不可破坏）+ 扫描追加块内存谓词；③项目弹层全部 ContentDialog 模态（ApplyDialogTheme + _shortcutsEnabled），Flyout 零先例、工具栏无筛选按钮（需新增）；④左栏点击契约（单选重置/二次取消/Ctrl 加减选）与 demo 的 QuickAdd 语义冲突，7 条测试需重写；⑤筛选不落盘（会话态，重开图库清空）；⑥条件树放 Services\ 零 csproj 改动，Core 无 MVVM 包须纯模型+静态函数；⑦性能口径 10 万级筛选首屏 ≤2s；SQLite LIKE 与 OrdinalIgnoreCase 对英文标签有大小写口径差。spec 拍板方向：单一内存求值器（弃双轨等价维护、索引层零改动）、条件值存标签名、左栏语义按 demo QuickAdd、untagged 保留独立互斥、Flyout 宿主 + 二级值选择浮层（不稳降级内联展开）、不落盘、深度 ≤3 状态层强制。

user:
（spec 落盘，待确认）

assistant:
spec 已落盘 `docs\iterations\图片打标签与瀑布流浏览\features\tag-filter-tree\spec.md`（需求来源=设计稿+口述，非标准 PRD，已在正文注明）。8 个 Step（core 树模型→测试重写→求值管线→左栏/筛选条→面板 UI→非模态集成→全量验证→实机走查）、14 条 T_FT 测试用例、6 项风险（R1 Flyout 零先例为最大，降级链=值选择内联展开→面板换 ContentDialog 壳）、显式推翻清单（左栏三分支语义/无清空按钮拍板/OR 徽章）与不变清单（untagged 入口/不落盘/索引层/遮盖布局）。待用户确认 spec 后进实现（建议分支 feature/tag-filter-tree）。

user:
确认 spec，发起 code-dev-loop 实现。

assistant:
code-dev-loop 完成，**dev-ready**（分支 feature/tag-filter-tree，base b8b2511 → HEAD 302e648，9 波次 DAG 全 done、双 cr-func 均 func-ready）：①impl-core：TagFilterState 条件树（Evaluate/BuildExpression/编辑原语/QuickAdd/联动/MaxDepth=3）+ 测试重写 T_FT1~14（107 测试全绿）；②impl-pipeline：_filterRoot 状态收敛、ApplyTagFilter 三分支全内存化（SortKey 排序，索引层零改动）、筛选条表达式 chips+清空按钮、左栏 QuickAdd 终态；③impl-entry（trivial 主代理）：修饰键收口；④impl-panel：TagFilterPanelControl 卡片嵌套面板+工具栏⧩筛选按钮+Flyout+主题对齐，值选择走 R1 预案行内展开（嵌套 Flyout 会触发父级 light-dismiss）；⑤impl-integration：快捷键局部抑制（_filterFlyoutOpen）+单图入口对齐+**发现并修补真实竞态**（扫描中 _galleryItems 后台 Add 与 UI 筛选枚举的 List 版本冲突崩溃→锁内快照）。每波次 build.ps1+test 107 绿，终检死代码/XAML 硬约束/Core 边界/csproj 零改动全过。**open deviation 一项**：组头/条件行"全部/任一""包含任一/不包含任一"实现为两态切换按钮组而非 demo 的 select 下拉（语义等价，动机=Flyout 内二级 popup 主题风险），留用户实机走查拍板（还原点集中 BuildTwoStateSwitch/MakeSwitchOption）。Step 8 用户走查清单：demo 形态还原度、双主题、快捷键抑制体感、扫描中面板实时性、10 万级体感、拖拽打标回归。RULE.md Flyout 先例待实机双主题实证后落录。

user:
实机走查打回："你测试了吗？筛选宽度完全不够，而且一添加条件就刷新页面"（附截图：面板挤成竖排窄条）。

assistant:
两个都是真问题，已修复并实机验证（c92f9a8）：①**面板宽度**：根因是 WinUI Flyout 默认 FlyoutThemeMaxWidth=456（generic.xaml 实锤）把 UserControl Width=620 硬夹成 456 逻辑宽——Width 设了也没用；修法=Flyout.FlyoutPresenterStyle BasedOn DefaultFlyoutPresenterStyle（键已核实存在）覆盖 Min/MaxWidth=660（620+左右 FlyoutContentPadding 16×2+边框），UIA 实测 990 物理=660 逻辑@150% ✓；②**添加条件即"刷新页面"**：每次编辑无条件 ResetFrom 全量重建卡片 VM（缩略图重载）整墙闪跳+清选中集，加空条件（未选值恒真）也闪；修法=WaterfallViewModel.PresentsExactly 逐项路径比对，ApplyTagFilter 命中序列与当前呈现一致时跳过重置与清选。教训：dev-loop 的 verify 只覆盖编译+Core 单测，UI 实机层（spec qa:manual_user）没验就交付——**以后 UI 改动交付前至少用 UIA+截屏（scripts/filter-panel-verify.ps1 模式：AppActivate 绕前台锁开面板、UIA 量 bounds、CopyFromScreen 采证）做一轮实机自检**。走查方法论新坑：①code-behind 构造按钮 UIA Name 为空，定位用 AutomationId 或截图目检；②`-d` 是单图模式不触发扫描（复现图库要走 LastLibraryRoot 自动恢复：备份改 settings.json 启动后还原）；③SetForegroundWindow 有前台锁，WScript.Shell AppActivate 稳。遗留：用户走查日志有两条"UI 无响应≥15s"心跳告警（扫 F:\Pictures 245 张 + thumb:decode 后），疑似缩略图解码期 UI 忙碌，待用户确认是否可感卡顿再定是否修。

