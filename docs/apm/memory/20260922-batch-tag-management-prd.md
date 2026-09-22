---
date: 2026-09-23 00:45
title: 批量标签管理 PRD（两层语义重构 + 图库右栏 + 工具栏场景化）
keywords: 批量移除标签, 两层模型, 配置层, 事实层, 未定义标签, 收纳, 图库右栏, 批量打标, 工具栏分组, 删选中集, PRD, batch-tag-management
abstract: 批量标签管理迭代（两层语义+图库右栏+工具栏场景化+删选中集）全周期：六轮问答 PRD → spec-generate 四路探索 D1~D12 拍板 → spec-check-loop 一轮 Go → code-dev-loop 13 波次 **dev-ready**（分支 feature/batch-tag-management，base=f6bc6a1，HEAD=c2b3f71，118/118 测试，两次实机自检端到端）。待用户实机走查（A1~E1/双主题/大选中集/回归）与一项拍板：筛选按钮挪左簇后 flyout 盖左栏（备选：ShowAt 右移/按钮挪常显右簇/接受遮盖）。
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

user:
调用 code-dev-loop——execute-ready 确认，开工。

assistant:
code-dev-loop 启动（22:00）：建分支 feature/batch-tag-management（base=f6bc6a1）。初始 DAG（dag_version 1，11 波）：wave-0 并行 impl-core（Step 1 TagProjection+T_PR）∥ impl-delete-services（Step 7 服务层半：NoUI 批量回收站+T_FO、RemovePathsAsync+T_IX_12，两节点禁各自 build 防撞锁）→ verify-wave0（统一 build+test）→ cr-func-wave0 → 中段串行 impl-config-delete（Step 2）→ impl-undefined-zone（Step 3）→ impl-gallery-panel（Step 4）→ cr-func-mid → impl-catalog-toolbar（Step 5+6 合并）→ impl-delete-selection ∥ impl-doc-sync → verify-full → cr-func-final。串行原因：MainViewModel.cs 被 Step 2~7 全程触碰。

user:
（开发过程推进，跨日 00:01 收口）

assistant:
code-dev-loop **dev-ready**（13 提交链 3b0d169→c2b3f71，最终 build 0 警告 + 118/118，cr-func×3 全 func-ready，无 open spec_deviations——三条偏差均 closed：未定义区主滚动流/右栏宿主天然隐藏等价/D10 订正 SHFileOperationW）。关键曲折：①wave0 实编译证伪 spec D10 的 `UIOption.NoUI`（审查建议臆造成员）→ trivial 豁免改 SHFileOperationW P/Invoke 并订正 spec；②两轮实机自检（undefined-panel-verify + toolbar-catalog-verify，模式=受控 settings 备份还原+临时图库+UIA 断言+落盘事实核对）：未定义区/右栏并集/✕ 批量移除/批量目录打标（旧标签保留）/删选中集（确认→回收站→已选0）端到端全过，图库工具栏 D1 十一断言全过；③实机自检揪出并修掉：目录行 UIA Name 缺失（复杂模板不自动暴露，补 AutomationProperties.Name=TagName——注意 Name=组名 TagName=标签名是两个属性，首版绑错）、双 ContentDialog 单开冲突（UIA 交错触发 ShowAsync 抛异常，两个删除确认框加守卫按取消处理）；④还原踩踏两次（RULE 定向串行还原处置）；⑤wave4 子代理二分修掉 XamlCompiler 崩溃两真因（App.xaml 全局 Style TargetType=Border + build.ps1 Out-Null 吞 CS 错误）。RULE 新沉淀四条：Border 全局样式禁用/MSB3073 先跑裸 dotnet build/Border 无 automation peer/UIA 驱动优先 Invoke 按钮而非键盘注入（IME+快捷键表劫持）。**待用户**：实机走查清单（iteration-state notes）+ 一项拍板：筛选按钮挪左簇后 flyout 实测盖左栏 367px（PRD D8 分簇拍板 vs 上迭代「尾部弹出防盖左栏」权衡冲突，备选 ShowAt 右移弹出/按钮挪常显右簇/接受遮盖）；另观察项：一条 15s UI 心跳告警（与既往走查日志同款，疑误报/瞬时）。

user:
走查第一轮打回：「右侧工具栏，怎么在画布上方？应该和画布同级吧？就像左侧工具栏一样」。

assistant:
走查修复轮 1（652138f）：图库右栏从浮层遮盖改**布局列**——图库 Grid Row2 改 */Auto 双列（WaterfallHost Col0 + 右栏宿主 Col1），移除 ZIndex/HorizontalAlignment=Right；收展改变瀑布流可用宽度、经窗口 resize 同一路径触发重排。根因是 spec D5 沿用了单图右栏的「浮层遮盖」机制，但遮盖式铁律的本意是保护**单图画布**几何恒定（防重解码），瀑布流本来响应宽度重排（左栏收展就是如此），浮层只会藏住缩略图。口径三处同步：spec D5/PRD 需求 3 加修订标记、RULE 铁律②修正为「仅约束单图画布侧，图库区不适用（左栏本就是布局列、图库右栏 2026-09-23 拍板同为布局列）」。验证：build 0 警告 + 118/118 + undefined-panel-verify 全链路复验（未定义区/右栏收展/全选并集/✕移除端到端落盘事实不变）。教训：把「同款机制」从单图侧照搬到图库侧前，先核对铁律的适用域（画布 vs 重排流）。

user:
走查第二轮两问题：「右侧栏颜色不统一，感觉下面空了一块」「左侧栏的多选逻辑变了，之前是 ctrl 左键，现在直接左键就能多选了」（附截图：右栏上半段面板浅色、「＋添加标签」以下露出窗口底色深黑一大块）。

assistant:
走查修复轮 2（8e4ae49）：①右栏空块根因=GallerySelectionPanelHost 缺 Horizontal/VerticalContentAlignment=Stretch（TagSidebarHost/WaterfallHost 都有）——UserControl 按内容高度顶对齐、列剩余高度露窗口底色；浮层形态时被画布内容掩盖、布局列形态才暴露。修：宿主补双 Stretch + ExpandedPanel/CollapsedBar 加 VerticalAlignment=Stretch 双保险；左右栏背景键本同 LayerFillColorDefaultBrush，实测铺满后上下段 RGB 差=4、与左栏同色系（39,39,4x）。②多选回归：代码链路全查无改动（WaterfallView.xaml.cs 零 diff、HandleCardTapped/SelectSingleCard/ClearCardSelection/修饰键检测原样），UIA 无键盘路径连点两卡实测两次均「已选 1 张」单选重置正常——**非产品回归**，最可能是走查当时系统 Ctrl 处于卡键按下态（此前自动测试注入过 SendKeys '^a' 序列，若 keyup 丢失则 GetAsyncKeyState 对所有应用报 Ctrl down、普通点击全变 Ctrl+点击；当前系统态已验证正常）。RULE 补：注入含修饰键键序列必须收尾逐个补偿 keyup（keybd_event KEYEVENTF_KEYUP）。新坑沉淀：ContentControl 宿主缺 Content 双 Stretch 是「UserControl 顶对齐露底」的通用形态——新建宿主对照 TagSidebarHost 抄全属性。
