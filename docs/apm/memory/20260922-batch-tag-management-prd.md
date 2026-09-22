---
date: 2026-09-22 21:48
title: 批量标签管理 PRD（两层语义重构 + 图库右栏 + 工具栏场景化）
keywords: 批量移除标签, 两层模型, 配置层, 事实层, 未定义标签, 收纳, 图库右栏, 批量打标, 工具栏分组, 删选中集, PRD, batch-tag-management
abstract: 用户提出批量移除标签需求并洞察两层模型（配置层定义 vs 事实层文件名标签），六轮问答拍板全量口径后 PRD 落盘并经 spec-generate 四路探索出技术规格：12 项拍板（D1~D12：绑定拒绝前移 VM、连锁删除走 RunTagOperationAsync(remove)、TagProjection 下沉 Core、右栏宿主 ContentAreaGrid 浮层、Ctrl+A 按现状仅图库等）、9 Step、T-PR/T-FO/T-IX 用例族。spec 待用户确认，确认后走 code-dev-loop（分支 feature/batch-tag-management，base=feature/tag-filter-tree@4d68321）。
---

user:
（需求原文浓缩）需要批量移除标签的能力。洞察：左栏标签应定义为"可显示标签簇"而非图片真正的标签（否则无法解释计数为 0 仍显示）——两层分离，删除标签不应连锁图片标签。方案：图库加右栏显示选中图片标签（参考详情页），✕ 批量移除。另：工具栏按钮按场景应隐藏而非禁用。随后六轮问答拍板细节。

assistant:
理解确认与六轮拍板（对话内完成）：①两层模型与代码事实一致（TagGroup 配置层/文件名事实层）；②配置组重命名保持连锁、未定义区不做重命名只保留删除（连锁真删）+收纳，UI 用 tag 流式；③未定义区点击弹菜单（删除/收纳）；④右栏常驻、空态支持批量添加；⑤工具栏左右分组。探索后 AskQuestion 四项补拍板：删除组=只删配置、图库删除按钮=删选中集（新能力，现状是删背后隐藏单图）、单图模式筛选入口隐藏、右栏=浮层遮盖+收展。PRD 落盘 docs\iterations\图片打标签与瀑布流浏览\features\batch-tag-management\prd.md（dependency 指向父 PRD）：5 条核心需求、A1-E1 共 15 条 GWT 验收、显式推翻三项 2026-09-19 历史拍板（未分组忽略/批量移除收窄/删除连锁）。探索关键发现：_latestTagCounts 全量聚合已含未定义标签计数（服务层零改动）；批量 remove 管线现成只缺 UI；TagCatalogDialog 需批量变体；工具栏无模式可见性机制需新建。待用户确认 PRD。

user:
（上下文压缩前）更新记忆。

assistant:
PRD 已提交（27da0fc，分支 feature/tag-filter-tree）。当前会话全局状态备查：①tag-filter-tree 迭代 dev-ready + 七轮用户走查修复全部收口（面板宽度/滚动条遮挡/✕焦点框/按钮位置盖左栏/⋯三点菜单/选择按钮），HEAD=12cf426 后又有 27da0fc，分支未合并 master；②batch-tag-management PRD 待用户最终确认，确认后下一步= spec-generate（技术规格：未定义区/图库右栏/工具栏显隐/删选中集，Core 可测逻辑=两层语义分流与未定义集合计算）；③走查方法论已入 RULE（UI 交付前实机自检脚本模式、三个隐形层）。

user:
（上下文压缩恢复、记忆核对后）调用 spec-generate——PRD 视为确认，进入技术规格阶段。

assistant:
spec-generate 启动（21:17）：已读 PRD 与父 PRD（dependency），派四路探索——A 配置层 CRUD 与连锁/计数管线、B 图库右栏宿主与选中集/批量打标、C 工具栏模式切换与删除管线、D 测试构建约束与历史决策对齐。spec 将落盘 features\batch-tag-management\spec.md。

user:
（四路探索报告返回，spec 撰写落盘）

assistant:
探索关键结论：①绑定引用拒绝现状在 TagService.DeleteTagAsync（文件管线前置），删定义纯化后必须前移 VM，否则 ValidateBindings 会以「引用的标签不存在」错误文案拒绝悬空绑定；②分批 25 的是 RunTagOperationAsync（打标管线）而 RenameFilesAsync（编辑管线）不分批——未定义区连锁删除拍板「QueryByTagsAsync 取候选 + RunTagOperationAsync(remove:true)」，回执口径才与批量打标一致；③未定义集合/并集计算现状内联 TagSidebarViewModel.Rebuild（UI 层不可测），下沉新文件 Services\TagProjection.cs（T-PR 用例族）；④图库右栏宿主定 ContentAreaGrid 图库 Grid 右缘浮层（随 GalleryVisibility 单图天然隐藏、免 TopChromeHeight 避让、规避 XamlCompiler 顺序坑）；⑤PRD 风险节「Ctrl+A 单图仍全选」与现状不符（接管条件本含 CurrentMode==Gallery），按现状拍板 D11 并回写修正。spec 落盘 features\batch-tag-management\spec.md：D1~D12 拍板、9 Step（core-projection→config-delete-rework→undefined-zone→gallery-panel→catalog-batch→toolbar-mode→delete-selection→doc-sync→verify-full）、T-PR1~8/T-FO1~3/T-IX13 + 现有 T_TG_05/T_ST_05 口径调整、7 风险、不变清单与文档同步点（父 PRD 三处回写、ungrouped-tags-ignore 部分推翻标注——精确到聚合忽略，角标与右栏 chips 口径不推翻）。分支计划 feature/batch-tag-management，base=feature/tag-filter-tree@4d68321（依赖其 TagFilterState 树联动）。待用户确认 spec。

user:
（未直接确认，先走质量闸门）调用 spec-check-loop 审查 spec。

assistant:
spec-check-loop 启动：iteration-state 切换为 batch-tag-management 文档审查循环（review_round 1，待首轮审查；上限 5 轮），派 readonly 审查子代理对照代码库审查（重点：D1~D12 与代码现状的硬冲突、Step/用例对 PRD GWT A1~E1 的覆盖、行号锚点抽查、PRD↔SPEC 一致性）。

user:
（首轮审查报告返回）

assistant:
第 1 轮审查结论 **Go**（0 P0 / 1 P1 / 6 P2）：D1~D12 承重锚点逐条与代码核实相符（含关键边界确认——所有改标签路径汇入 SyncRenamedItemsAsync→ReplaceGalleryItemState 同一漏斗，D6 收口挂点天然覆盖单图 ✕ 场景）；15 条 GWT 全有映射。唯一 P1 实锤：批量删除若照抄单图的 UIOption.OnlyErrorDialogs，失败弹 Shell 错误框而非抛异常（批量卡死 UI、T_FO_02 挂死）——拍板批量变体逐文件 UIOption.NoUI。因 must-fix 仅 1 条措辞级补丁走 **trivial 豁免**（主代理直接 doc-fix，P2 顺手全闭合）：D10 NoUI 拍板、测试命名对齐惯例（T_PR_01/T_FO_01/T_IX_12 下划线两位——现有最高 T_IX_11 且连字符式不合惯例）、不变清单补齐（ThemeResource 键核对高险点/PUA·DockPanel/PRD 范围守护）、D7 补三组 x:Bind 函数绑定消费面、D6 触发粒度=操作收口一次（防 O(N²)）、Step 9 补 A4 重命名走查、D2 补删组遇绑定引用整体拒绝。iteration-state 记 review_round 2、execute-ready 待用户确认。**等用户确认后走 code-dev-loop（分支 feature/batch-tag-management）。**
