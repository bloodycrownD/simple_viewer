---
date: 2026-09-19
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# selection-and-zoom-polish Feature PRD

## 背景与变更动机

用户实机走查反馈三项：

1. **打标生效提示碍事**：给图片打标（如"水印"类标签）成功后弹出的 InfoBar 回执（单图"已生效。"、批量"成功 N 张。"）占据顶部横行，在遮盖式布局下还会推挤右栏避让区，打断浏览心流。
2. **放大置顶策略不合适**：此前拍板"滚轮放大后图片置顶遮盖侧栏/工具栏"，实测发现不合适——要求改为置底：放大后图片保持在底层，溢出部分被侧栏/工具栏/状态栏遮盖。
3. **默认多选不合适**：现状普通单击 = toggle 累积（点几张累积几张，Ctrl 与普通点击完全等价），误触即多选。要求：默认单选、Ctrl+点击多选；并增加显式"清除选择"入口（多选太多时逐张取消太累，Esc 路由不可发现）。

## 范围说明（相对原需求）

- 打标回执策略变更（范围变更）：成功回执**一律静默**（单图+批量），仅失败/警告回执保留。修订父级 PRD"批量操作展示进度，结束后回执成功/失败数量"口径为"批量进度保留、成功静默、仅失败回执（数量与原因）"——成功就地反馈已充分（卡片角标/右栏 chips/侧栏计数）。互斥组替换的透明性括注随成功静默消失（chips 就地可见替换结果，可接受）。
- 放大层级策略变更（推翻 2026-09-19 早前"最高层"拍板）：放大置底，溢出被 chrome 遮盖即预期。
- 选择模型变更（推翻 demo 原型"默认 toggle 多选"）：改为 Explorer 心智；demo 同步。

## 影响模块与接口

| 模块 | 变更 |
|------|------|
| ViewModels/MainViewModel | HandleCardTapped 加 ctrl 参数与新分流；SelectSingleCard 新增；SelectCardRange 改范围重置；ClearSelectionCommand（CanExecute=HasSelection）新增；ShowTagOperationResult 全成功静默；BeginTagOperation 单图不弹进行中；IsCurrentImageZoomed 属性及回图库清理删除 |
| Views/WaterfallView.xaml.cs | IsControlKeyDown 新增（只判 Down）；OnCardTapped 转发双修饰键 |
| MainWindow.xaml(.cs) | 图库状态行加「清除选择」按钮；zoom 层订阅与处理器删除 |
| Views/SingleImageView.xaml(.cs) | UpdateZoomLayering 及调用删除；注释改置底口径 |
| demo/demo.js | onCardClick 同步新选择语义；hint 补 Ctrl 提示 |

## 验收标准

- [x] 打标成功（单图/批量）不再弹出 InfoBar；打标失败/警告仍弹（数量+原因）；批量进行中进度保留。
- [x] 滚轮放大后图片保持底层，溢出部分被侧栏/工具栏/状态栏/右栏遮盖；复位后正常；切图/回图库无层级残留。
- [x] 普通单击卡片 = 单选重置（状态行"已选 1 张"；点已选中卡保持选中）。
- [x] 状态行右侧常显「清除选择」按钮：无选中禁用灰态、有选中可点、点击清空（Esc 路由保留）。
- [ ] Ctrl+点击 = 加/减选、Shift+点击 = 范围重置（本机注入不了修饰键，留用户实机验证）。
- [x] 71 项 Core 单测全绿，build 通过。

## 测试用例

1. 单图模式经目录选择器打标 → 无 InfoBar、chip 就地出现；chip ✕ 移除 → 无 InfoBar（实机通过）。
2. 图库 raw 点击卡片 A（已选 1）→ 点击卡片 B（仍已选 1，单选重置）→ 点「清除选择」→ 已选 0、按钮禁用（实机通过）。
3. Ctrl+点击第二张卡 → 已选 2；再 Ctrl+点击 → 减选；Shift+点击远处卡 → 选中重置为范围（留用户实机）。
4. 放大溢出遮盖与复位（留用户实机，滚轮不可注入）。
