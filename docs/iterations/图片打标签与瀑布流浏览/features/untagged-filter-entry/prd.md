---
date: 2026-09-19
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# untagged-filter-entry Feature PRD

## 背景与变更动机

用户反馈两项（参考 Billfish 标签面板标题栏的"无标签素材"筛选按钮）：

1. **「清空筛选」按钮用处不大**：二次点击标签即可取消筛选（多筛选时 chip ✕ 逐个移除），按钮占位。
2. **缺少"无标签图片"筛选**：整理图库时需要快速定位还没打标的图片（打标工作的入口场景）。

## 范围说明（相对原需求）

- 移除筛选条「清空筛选」按钮及命令；取消路径=二次点击标签 / chip ✕ / ∅ 二次点击。
- 侧栏"标签库"标题行新增 ∅（U+2205）icon 按钮：toggle 无标签筛选，与标签筛选**互斥**（点任何标签筛选自动退出无标签模式，反之激活无标签清空标签筛选——无标签图片按定义不含任何标签，OR 组合无意义）。
- 筛选条显示"无标签"chip（✕ 关闭）；激活态按钮强调色淡底高亮。

## 影响模块与接口

| 模块 | 变更 |
|------|------|
| Services/ILibraryIndexService + LibraryIndexService | 新增 QueryUntaggedAsync（tags IS NULL OR tags=''） |
| ViewModels/MainViewModel | IsUntaggedFilterActive + ToggleUntaggedFilter 命令 + HasAnyFilter 派生 + ApplyTagFilterAsync 三分流 + 互斥联动 + 无标签 chip；删 ClearTagFilters |
| Views/TagSidebarControl(.xaml) | 标题行三列加 ∅ 按钮（激活态转换器） |
| MainWindow.xaml | 删「清空筛选」按钮 |
| demo | 同步 ∅ 按钮/无标签 chip/移除清空筛选 |

## 验收标准

- [x] 点 ∅：瀑布流只显示无标签图片，筛选条"无标签"chip + 命中统计；再点取消回全量。
- [x] 互斥：无标签激活时点任一标签行 → 自动切换为标签筛选（无标签 chip 消失）；标签筛选激活时点 ∅ → 清空标签筛选进入无标签模式。
- [x] 「清空筛选」按钮移除，取消筛选路径保持可用（二次点击/chip ✕）。
- [x] 打开图库（重开目录）重置无标签筛选态。
- [x] 74 项 Core 单测全绿（含 3 项新增 QueryUntagged 用例），build 通过。

## 测试用例

1. 图库 10 张全无标签：点 ∅ → 命中 10/10 + "无标签"chip（实机通过）。
2. ∅ 激活时点"大水印"标签行 → "无标签"chip 消失、"负面标签：大水印"chip 出现、命中 0、空态提示（实机通过）。
3. 索引层单测 T_IX_08/09/10：无标签命中/有标签排除/清空标签后转命中。
