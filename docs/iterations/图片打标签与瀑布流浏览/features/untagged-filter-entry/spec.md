---
date: 2026-09-19
agile_trace: true
---

# untagged-filter-entry 实现规格（SPEC）

## 根因 / 方案摘要

无标签筛选是全新能力（索引层 tags 空串判定 + VM 三分流管线）；「清空筛选」按钮移除是 UI 精简。核心设计：无标签态与标签筛选集**互斥**（无标签图片按定义不含任何标签），`HasAnyFilter` 派生统一驱动筛选条可见性与扫描渐进过滤。

## 变更点清单

| 提交 | 内容 |
|------|------|
| 77bcdd8 | 索引层 `QueryUntaggedAsync`（接口+实现，`WHERE tags IS NULL OR tags=''`，复用 ReadItem+自然排序尾部）；T_IX_08/09/10 用例（无标签命中+有标签排除、空库/全打标返回空、UpdateTagsAsync 清空后转命中再打标退出）。QueryByTagsCore 空集=全量语义不动 |
| b2fa29f | VM 管线：`IsUntaggedFilterActive`（ObservableProperty，OnChanged 补发 FilterBarVisibility/FilterStatsText）+ `ToggleUntaggedFilter` 命令（toggle+防重入+单图先回图库对齐标签行先例）；`HasAnyFilter` 替代 IsTagFilterActive（挂 FilterBarVisibility 与 AppendChunkFromScan 扫描过滤）；`MatchesTagFilter` 分流（无标签=Tags.Count==0）；`ApplyTagFilterAsync` 三分流；`ToggleTagFilterAsync` 开头退出无标签态（互斥）；`RebuildFilterChips` 插"无标签"chip（✕ 绑 ToggleUntaggedFilterCommand，`FilterChipText` 空组名特判只显"无标签"，chip 配色中性灰特判防 HueOfName("") 落红色）；打开图库重置；删 ClearTagFiltersAsync 与按钮；侧栏标题行三列 ∅ 按钮（U+2205，GhostIconButtonStyle，UntaggedButtonBackground/Foreground 转换器激活=强调色淡底） |
| c064cd1 | demo 同步：state.untagged + filteredImages 过滤 + 标签 toggle 互斥清位 + sidebar-head ∅ 按钮 + renderFilterBar 无标签 chip + 移除 #clearFilterBtn + .untagged-btn.on 激活态样式 |

## 详细改动说明

- tags 存储=空格分隔左右补空格、无标签为空串（ComposeTagsValue）；`IS NULL` 为防御双保险（ReadItem 已有 IsDBNull）。
- 实现微调两处（对齐先例/规避缺陷）：∅ 未激活背景用 `TransparentBrush` 而非 null（Foreground 设 null 会失去画刷不可见，TagRowBackground 先例）；无标签 chip 底/边/字色空组名特判中性灰（否则 HueOfName("") 落 hue 0 红色）。
- 主题切换无滞留风险：MainWindow.ApplyTheme 重建侧栏控件，标题按钮随重建重算。

## 测试策略

### 测试用例

- 自动化：`dotnet test` 74/74 全绿（71 基线+3 新增）；build×3 通过；`node --check demo.js` 通过。
- 实机走查（主代理）：∅ 激活 → 命中 10/10 + "无标签"chip + tooltip 正常、「清空筛选」按钮确认消失；∅ 激活下点"大水印"标签行 → 互斥切换为标签筛选（"负面标签：大水印"chip、命中 0、空态提示）。走查中误触标签行 ✕ 弹出删除确认框，取消路径正常（顺带验证对话框无回归）。
- 留用户实机：∅ 按钮激活配色观感、浅色主题下表现。

## 风险与回滚方案

- 风险：①"无标签"与标签筛选互斥是拍板语义，若用户期望 AND/OR 组合需重设计（无意义场景，风险低）；②侧栏"无标签"入口不显示无标签图片计数（Billfish 对照亦无，TagCountsAsync 不产出该口径——如需后续加）。
- 回滚：三提交独立可 revert；revert b2fa29f 需同步恢复 ClearTagFilters 按钮与 IsTagFilterActive。
