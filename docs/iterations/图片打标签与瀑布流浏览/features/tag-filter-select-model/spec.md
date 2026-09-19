---
date: 2026-09-19
agile_trace: true
---

# tag-filter-select-model 实现规格（SPEC）

## 根因 / 方案摘要

标签筛选原为 toggle 累积（与卡片旧"默认多选"同源），卡片已改 Explorer 心智后标签侧未跟。改为：无修饰 = 单选重置（唯一选中再点 = 取消，保留用户依赖的二次点击取消）；Ctrl = 加/减选（原 toggle 收窄）。

## 变更点清单

| 提交 | 内容 |
|------|------|
| a6f9274 | TagSidebarControl.OnChipClicked 读 Ctrl（IsControlKeyDown 照抄 WaterfallView 模式，只判 Down——RULE 铁律）转发 HandleChipTappedAsync(chip, ctrl)；TagSidebarViewModel 转发签名加 ctrl；MainViewModel.HandleTagChipTappedAsync/ToggleTagFilterAsync 加 ctrl 参数三分支：ctrl→toggle 加减选；无修饰且唯一选中就是它→移除（取消）；无修饰其余→Clear+Add（单选重置）；demo onChipClick 同语义（ctrlKey/metaKey 传参） |

## 详细改动说明

- `ToggleTagFilterAsync(tagName, ctrl = false)` 默认参数保持既有内部调用兼容。
- 与无标签筛选互斥（入口清 IsUntaggedFilterActive）不变；CLI 直开无索引时筛选无处执行（ApplyTagFilterAsync 依赖 _indexService），行为同前。

## 测试策略

- 自动化：74/74 全绿、build 一次过。
- 实机走查（主代理，AXPress 语义路径）：单选 chip×1 → 点不同标签替换（仍×1）→ 点唯一选中取消回全量，三步通过。Ctrl 路径注入不可达（GetKeyState 线程可见性，与前轮同），代码与卡片 Ctrl 检测同构留用户实机。

## 风险与回滚方案

- 风险：单提交小改，无结构风险；"点已选标签取消"与图片"点已选卡保持"是刻意差异（筛选是状态可取消，选择集用于批量操作），已注释说明。
- 回滚：revert a6f9274 即回 toggle 累积。
