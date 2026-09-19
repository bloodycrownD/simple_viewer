---
date: 2026-09-19
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# tag-interaction-rework Feature PRD

## 背景与变更动机

原打标交互（选中图片/单图模式下点击左侧标签 = 打标）被用户判定"不好用"，要求重构为：

- **a** 点击左侧标签 = 筛选出该标签下所有图片（多标签 OR）；
- **b** 拖拽图片（含多选集）到标签树行 = 打标，像拖文件进文件夹；
- **c** 单图详情页右侧新增一栏：图片信息 + 图片标签 chips（✕ 移除、＋ 弹标签目录选择添加）。

## 范围说明（相对原需求）

- 点击语义反转：打标不再经由点击标签（保留快捷键打标通道 ApplyTagByShortcutAsync 不变）；Shift+点击移除入口取消（移除走详情页 ✕）；单图模式点标签**自动切回图库**（否则筛选发生在不可见视图，用户感知"没反应"；CLI 直开无图库时保持单图）。
- 拖拽：拖选中集内任一卡 = 整集打标；拖未选中卡 = 单卡；互斥组标签行 Drop 走既有 TagSemantics 替换语义；打标进行中拒绝 Drop。
- 详情页右栏：280 展开/36 折叠条（仿左栏先例，默认展开）；标签目录选择器走 ContentDialog 惯例（项目无 Flyout 先例）。
- demo 原型同步三项交互（HTML5 拖拽）。

## 影响模块与接口

| 模块 | 变更 |
|---|---|
| MainViewModel | HandleTagChipTappedAsync 简化为一律筛选（+单图自动回图库）；BeginCardDrag/ApplyTagToDraggedCardsAsync/_dragPayload；ApplyTagToPathsAsync（选中集管线重构出的路径集入口）；CurrentImageTags/信息行属性；IsInfoPanelCollapsed；RemoveTagFromSelectionAsync 删除 |
| WaterfallView | 卡片 Border CanDrag + DragStarting（DataPackage 写 SimpleViewer.CardPaths 标记） |
| TagSidebarControl | 标签行 AllowDrop + DragOver/DragLeave/Drop + DropOverlay 高亮层 |
| SingleImageView | 三列布局（图片区+右栏）；信息结构化行；chips（✕ 移除经 Tag 槽位）＋ 目录选择器入口 |
| TagCatalogDialog（新） | 两级行列表目录选择器（快照口径、已选禁点、点选即打标关闭） |
| demo | 筛选语义/HTML5 拖拽/查看器右栏同步 |

## 验收标准

- [x] 图库模式点击标签行 → 瀑布流按标签筛选，筛选条显示"组：标签"与"命中 N / 已发现 M 张"，再点取消；不触发打标/跳转（实机通过）。
- [x] 单图模式点击标签行 → 自动切回图库且筛选生效（实机通过：筛选"状态：已废弃"命中 0/10 空态文案正确）。
- [x] 右栏：信息四行（文件名三段高亮/大小/尺寸/序号）、当前图 chips、✕ 移除（文件名即时回退）、＋ 打开目录选择器点选添加（实机通过，同图改名链路不闪不重载）。
- [x] 拖拽管线代码链路完整（格式串两侧一致、事件接线齐全、互斥语义复用统一管线）；**真实拖拽手势的手感验证留待用户实机**——本机 SendInput 鼠标移动事件被系统丢弃（注入返回成功但光标不动），拖拽无法自动化注入（RULE 已记）。
- [x] demo 三项交互同步；71/71 单测绿。

## 测试用例

1. 实机（UIA）：筛选开/关（海=命中 1/10）、单图回图库筛选、右栏目录添加（海→photo_1[海].jpg）、chip 移除（恢复 photo_1.jpg）、组头折叠/展开、双击进单图、卡片点选（已选 1 张）。
2. 静态核对：DragStarting/DragOver/Drop 格式串与 Tag 槽位回查、BeginCardDrag 整集/单卡分支、ApplyTagToDraggedCardsAsync 未分组兜底。
3. 回归：打标互斥替换、角标刷新、竞态双保险（与 tagging-pipeline-fixes 联测）。
