---
date: 2026-09-19
agile_trace: true
---

> **2026-09-22 部分推翻**：本特性『聚合类 UI 完全忽略无组标签』的拍板已被 features/batch-tag-management 推翻——左栏新增【未定义标签】区展示无组标签。**未推翻项**：单图右栏 chips 显示无组标签可 ✕（本文件验收项）与卡片角标不计入无组标签，口径均维持。

# ungrouped-tags-ignore 实现规格（SPEC）

## 根因 / 方案摘要

"未分组"虚拟组（spec Step 9 原始设计）+ "曾见即留"（4679a12）把文件名中的无组标签（存量脏数据）当一等公民在侧栏展示。用户拍板推翻：**聚合类 UI 完全忽略无组标签**；右栏 chips 保留显示与 ✕ 移除（唯一清理出口，有意偏差 demo）；互斥语义层（组外标签保留）与 Services/索引层零改动。

## 变更点清单

| 提交 | 内容 |
|------|------|
| f87767b | 侧栏/VM/控件清理：TagSidebarViewModel 删 UngroupedGroupId/Name 常量、_knownUngroupedTags、Rebuild 虚拟组段、ForgetUngroupedTag、CreateUngroupedChipEditCommands/BuildUngroupedTagRequest、isUngrouped/UngroupedHue/NoCommands（TagChipCommands 改非空 ICommand、OwnerGroup 改非空 TagGroup）；MainViewModel 删拖拽 ownerGroup 兜底（防御性 null return）、Execute*TagAsync 的 group-null 死分支（含 2 处 ForgetUngroupedTag 调用）、RebuildFilterChips 未分组回退；FindGroupByTagName 兜底**保留**（注释：chips ✕ 清理出口/remove 按名不消费组/同名标签收编）；TagSidebarControl.xaml/.cs 删 NotUngroupedToVisibility/CommandToVisibility/FilterHue 灰蓝/IsUngroupedHue 等分支（x:Bind 编译期校验强制清零） |
| 9440d8c | 卡片角标过滤（对齐 demo badgesHtml）：Badges/BadgeLine 仅显示 _tagHues 命中（配置组内）标签，"+N"按过滤后剩余数；删 UngroupedBadgeHue=200；角标 hue 滞后注释随口径更新 |

## 详细改动说明

- **数据流不变**：LibraryIndexService.TagCountsCore 仍全表聚合（索引层不管组）；侧栏 Rebuild 忽略非配置名即可。配置组内新建同名标签 → _tagHues/配置名命中 → 自然"收编"（无需代码）。
- **打标入口全部携带配置组**（快捷键 FindTagById/目录选择器/拖拽 ownerGroup），不可能产出无组标签；拖拽兜底删除后防御性 null 直接忽略。
- **右栏 chips**：CurrentImageTags 仍为文件名全量解析（含无组），✕ 经 FindGroupByTagName 兜底组进 toggle 管线，remove 分支 `TagService.RemoveTagAsync` 按名落盘（OrdinalIgnoreCase）——与组无关，现状即可用，零新开发。

## 测试策略

### 测试用例

- 自动化：`dotnet test` 71 项全绿（每 commit 验证）；涉及"组外"的 2 项语义测试（T_TG_09 / TagSemantics_Exclusive）零改动通过；build.ps1 通过；全局残留搜索（Ungrouped*/ForgetUngroupedTag 等）清零。
- 实机走查（主代理）：造 `photo_99[神秘标签].jpg` → 图库模式侧栏无"未分组"组（三个配置组正常）；CLI 直开该文件 → 右栏 chips 显示"神秘标签" → ✕ → 磁盘文件名变 photo_99.jpg（落盘实证）；走查产物已清理。

## 风险与回滚方案

- 风险：①无组标签批量清理出口收窄（原侧栏 ✕ 可全库移除，现仅右栏逐图 ✕）——用户已接受，且同名收编是归组正道；②文档口径痕迹（spec Step 9 虚拟组条款、tag-group-tree-ui prd"未分组行为不变"承诺）与旧实现记载不符，以本敏捷 PRD 的 overturn 记录为准。
- 回滚：两提交独立可 revert（revert f87767b 需同步恢复 TagSidebarControl 的 x:Bind 分支）。
