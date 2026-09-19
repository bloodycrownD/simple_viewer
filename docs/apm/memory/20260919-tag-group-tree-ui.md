---
date: 2026-09-19 12:10
title: 标签库 UI 重构为目录树（tag-group-tree-ui）敏捷任务
keywords: 标签组, 互斥组, 兼容组, 目录树, TagSidebarControl, 展开折叠, 概念更名, Eagle风格, 行式树节点
abstract: 迭代内敏捷 feature：标签库 UI 重构为 Eagle 风格目录树（行式树节点：缩进/连接线/右对齐计数/悬停浮现管理按钮），概念更名"非互斥/多选"→"兼容组"。第一轮做成"组头折叠+chip 流"被用户否决后返工为真目录树。数据层零改动。实机走查全通过，prd/spec 已留痕，待用户确认。
---

user:
重新设计标签库 UI。新增概念「标签组」，分为两种：互斥组与兼容组。互斥组内部标签相互互斥只能存在一个，兼容组内部标签可共存，组间标签相互兼容。一期不支持组嵌套。UI 采用目录树形结构，点击标签组可展开内部标签。

assistant:
按 agile-dev 流程判定：feature，敏捷名称 tag-group-tree-ui，所属迭代「图片打标签与瀑布流浏览」，分支继续用 feature/tagging-waterfall（迭代未合并 master，不另切嵌套分支）。并行派两个只读探索子代理（UI 层 + 数据/逻辑层），结论：

- 数据层零改动：`TagGroup.Exclusive: bool`（Models\TagGroup.cs:16）天然表达互斥/兼容（false=兼容组），互斥语义收敛在纯函数 `TagSemantics.Apply`（Services\ITagService.cs:64-108，仅剔除同组标签、组外一律保留=组间天然兼容），无嵌套结构。settings.json v2 schema 不动。
- 现状 UI：TagSidebarControl = ScrollViewer + ItemsRepeater 平铺组列表（组头 7 列 + chip WrapPanel 流式换行），VM 是不可变快照 + MainViewModel.RebuildTagSidebar() 全量重建（扫描节流/编辑/筛选/主题切换都触发，无 INPC）。demo 原型同为平铺，树形无原型可对照。
- 关键设计决策：① 展开状态走"collapsed 集合 + Rebuild 全量重建"模式（点组头 → 切换 HashSet membership → 触发 Rebuild → 新 VM 对象 + x:Bind OneTime 重求值，零 INPC 引入，完全复用现有架构）；② 默认全展开、点击组头折叠（保持与现状平铺一致的信息可见性，可用性优先；用户可推翻）；③ 组头整行改为 Button 承担点击展开，4 个管理按钮（＋⇄✎✕）嵌套其内（嵌套 Button 点击不冒泡）；④ chevron 用 BMP 字符 ▸/▾（禁 FontIcon/PUA）；⑤ 概念更名仅动 UI 文案层（徽章"多选"→"兼容"、TagEditDialog 话术、tooltip、TagGroup 注释），标识符 `Exclusive`/`ToggleExclusive` 保留保数据兼容；⑥ demo 同步树形化保持交互基准一致；⑦ 展开状态不落盘（会话内记忆）。
- 风险清单：XamlCompiler 对新码点需 build 验证；嵌套按钮误触发需实机走查；未分组虚拟组（__ungrouped__ 末位）同享展开/折叠但管理按钮仍隐藏。

user:
第一轮实现完成后走查前，用户发参考图怒斥：「听不懂什么叫文件树UI吗？」——参考图是 Eagle 风格标签树：每个标签是独立行式树节点（缩进+竖向连接线+右对齐计数+行悬停高亮），不是胶囊 chip。

assistant:
承认理解偏差：第一轮（提交 7f8ac83/4758c26/0e93ced）做成了"组头可折叠 + 组内 chip 胶囊流式换行"——只是给旧布局加了折叠，不是目录树。返工（5511024/d78a711）重做节点模板：组行=chevron+组名+互斥/兼容徽章+右对齐纯数字计数+悬停浮现＋⇄✎✕；标签行=缩进列（1px 竖向连接线贯穿子行）+[互斥组]单选圆点+标签名+右对齐计数+悬停浮现✎✕；激活行整行圆角高亮（IsFilterActive）；悬停浮现用 Opacity 0↔1（非 Visibility，保 UIA 可命中）；chip 专属样式与颜色函数退役删除。展开状态机制（collapsed 集合+全量 Rebuild）第一轮已正确，保留复用。demo 同步行式树观感。

user:
（主代理实机走查）

assistant:
按 RULE 实机 UI 测试方法论走查全通过（临时注入测试组配置，测完还原 settings.json）：①UIA 断言组行/标签行独立 Button、折叠后子行消失 chevron ▸、展开恢复；②打标链路：点"待筛选"→ photo_1[风景 待筛选].jpg，同组点"已选中"互斥替换 → photo_1[风景 已选中].jpg，再点移除 → photo_1[风景].jpg（TagSpaces 协议全程核对）；③折叠态跨 3 次打标 Rebuild 保留；④标签行 ✎ 打开重命名对话框且组未误折叠，opacity-0 按钮 AXPress 可命中；⑤像素级放大核查连接线连续贯穿、radio dot 渲染；⑥63/63 单测绿、build 过、BOM 断言过。注意：-d 直开单图模式不扫描目录（PRD 拍板行为），树行计数为 0 属预期非回归。留痕：features/tag-group-tree-ui/prd.md + spec.md。待用户确认；可推翻的默认值：默认全展开（可改默认折叠）、展开态不落盘、管理按钮悬停浮现。
