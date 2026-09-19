---
date: 2026-09-19
agile_trace: true
---

# tag-interaction-rework 实现规格（SPEC）

## 根因 / 方案摘要

用户否决"点击标签打标"交互（打标与筛选混淆、单图/选中集多分支心智负担）。重构为**筛选/打标入口分离**：点击=纯筛选（自动回图库）、拖拽=打标（选中集整集/单卡）、详情页右栏=单图标签管理。前置依赖 tagging-pipeline-fixes 的"同图改名"链路（右栏打标不闪不重载）。

## 变更点清单

| 提交 | 内容 |
|---|---|
| 1dd249f | 点击一律筛选 + 单图自动回图库（HasGallery 守卫对齐 TryRouteEscape）；Shift 检测链路移除；RemoveTagFromSelectionAsync 删除；ToolTip/注释更新 |
| b591b2e | 卡片 CanDrag+DragStarting（Tag 槽位回查 → BeginCardDrag 整集/单卡 → DataPackage 标记）；树行 AllowDrop+三事件+DropOverlay；ApplyTagToSelectionAsync 重构委托 ApplyTagToPathsAsync |
| 7ae4b7f | SingleImageView 三列布局 + 信息行 + CurrentImageTags chips + TagCatalogDialog 目录选择器 |
| 60bdb7c | demo 同步（筛选语义/HTML5 拖拽/查看器右栏） |
| 6cf7e76（部分） | 走查修复：OpenTagCatalogCommand 漏加 OnHasImageChanged 刷新点（＋按钮永久禁用） |

## 详细改动说明

- **拖拽 payload**：同进程以 VM 侧 `_dragPayload` 为准（DataPackage 只写格式标记 + 计数文本，避免序列化）；目标侧 DataView.Contains 校验（外部拖入天然拒绝）+ IsTagOperationRunning 拒绝。WinUI 3 DragStartingEventArgs 无 UWP 的 DragUIOverride/AcceptedOperation（winmd 反射核实），拖拽视觉为系统默认卡片快照。
- **Drop 高亮**：TagTreeRowButtonStyle 模板新增 DropOverlay Border 层，code-behind 视觉树按名回查（对齐 RowCommands 先例），颜色 IsDarkTheme 双值。
- **右栏布局**：放 SingleImageView 内部（宿主 SingleVisibility 整体切换 → 图库模式天然不可见，不动 MainWindow 全局布局）；解码尺寸源=ImageHost.SizeChanged，右栏收展自动重解码。IsInfoPanelCollapsed + ToggleInfoPanelCommand 对照左栏先例。
- **目录选择器**：构造时 GetTagCatalogSnapshot 快照（配置组×当前标签集 OrdinalIgnoreCase 判已选）；已选行禁点+"✓ 已有"；点选 → TagApplied → 宿主 Hide() + fire-and-forget ApplyCatalogTagAsync（toggle 管线添加方向）。
- **CurrentImageTags**：UpdateFileNameSegments 同源重建（与文件名分段/状态行永不分裂）；chip ✕ 经 Tag 槽位 → RemoveCurrentImageTagAsync（FindGroupByTagName 解析所属组，未命中=未分组兼容组兜底）。

## 测试策略

### 测试用例

见 prd.md 验收标准与测试用例节（实机 UIA 全项通过；拖拽真实手势因环境注入限制留待用户验证）。

## 风险与回滚方案

- 风险：①触摸屏拖拽未验证（系统长按协商，项目鼠标为主）；②"点击=筛选"后批量移除标签无入口（移除=详情页 ✕ 单图；批量移除待后续按需加）；③筛选切换清空选中集（新语义下选中集不再保留跨筛选）。
- 回滚：revert 1dd249f/b591b2e/7ae4b7f/60bdb7c + 6cf7e76；无 schema/配置变更。
