---
date: 2026-09-19
agile_trace: true
---

# selection-and-zoom-polish 实现规格（SPEC）

## 根因 / 方案摘要

三项独立交互打磨（用户实测反馈）：

1. **成功回执静默**：所有打标入口成功都会弹 InfoBar（单图"已生效。"、批量"成功 N 张。"），遮盖式布局下弹出占顶部横行且推挤右栏避让区（TopChromeHeight 联动）。改为全成功分支不弹并关闭残留回执；失败/警告（PRD 验收项）与批量进度保留。
2. **放大置底**：拆除 IsCurrentImageZoomed 全链（视图 ZIndex + 宿主 ZIndex + 回图库清理），放大后 ImageHost 恒为 ZIndex=0，溢出被 chrome 层与右栏/文件名栏浮层遮盖。推翻同日早前"最高层"拍板。
3. **选择模型 Explorer 心智**：现状无修饰点击 = toggle 累积（Ctrl 与普通等价）是"默认多选"根源。改为：无修饰 = 单选重置、Ctrl = toggle 加/减选、Shift = 范围重置（原为纯加选不清旧）；状态行加显式「清除选择」按钮。

## 变更点清单

| 提交 | 内容 |
|------|------|
| 29e3949 | 选择模型 + 清除按钮：`HandleCardTapped(vm, ctrl, shift)` 新分流（ctrl→ToggleCardSelection / shift+锚点→SelectCardRange 重置 / else→SelectSingleCard）；`SelectCardRange` 先 Clear 再选范围；`IsControlKeyDown`（照抄 IsShiftKeyDown，只判 Down——RULE 铁律）；状态行 Grid 两列加「清除选择」GhostButton（CanExecute=HasSelection，OnSelectedCardCountChanged 联动 NotifyCanExecuteChanged）；demo onCardClick 同步 |
| 8874d93 | 成功回执静默：`ShowTagOperationResult` failed==0 分支清明细 + `IsTagFeedbackOpen=false`（不弹且关残留）；`exclusiveHint` 参数删除；`BeginTagOperation` 仅 showProgress=true 置 open（单图"正在处理…"一闪消灭）；3 处 ShowInstantTagFeedback 警告保留 |
| 9c7daba | 放大置底：删 SingleImageView.UpdateZoomLayering 及 ZoomAt/ResetZoom 调用、MainViewModel._isCurrentImageZoomed 及 OnCurrentModeChanged 清理、MainWindow 订阅与处理器（连带 using）；两处 XAML 注释改置底口径；全库 grep 零残留 |

## 详细改动说明

- **回执**：全成功静默后，批量打标的完成信号由就地视觉反馈承载（角标整集变化、侧栏计数、右栏 chips）；失败回执（Warning/Error + BuildFailureDetails 明细上限 20 条）不变；批量进行中进度条（PRD 验收）不变。附带收益：成功不弹则 TopChromeHeight 不被回执行推高，右栏浮层避让区不抖。
- **放大置底**：`_zoomOwnerPath`/`OnCurrentImageRenamed`/`EnsureFullResolutionAsync`/切图 ResetZoom 复位等缩放交互态与层级无关，全部保留。溢出区域指针命中 chrome（该区域滚轮不再缩放、侧栏可点）属置底预期语义。
- **选择模型**：锚点 `_lastClickedCard` 仍随每次点击更新；范围不可解析（锚点/目标不在呈现集）退化 toggle；Ctrl+A 全选、Esc 三态路由保留；拖拽"选中集内=整集"语义在新模型下自洽（默认单选时拖选中卡=拖 1 张，Ctrl 加选后拖任一选中卡=整集）。

## 测试策略

### 测试用例

- 自动化：`dotnet test` 71 项基线全绿（每 commit 验证）；build.ps1 通过。
- 实机走查（已完成）：目录选择器打标成功无 InfoBar、chip 就地出现；chip ✕ 移除无 InfoBar；FolderPicker 开图库后 raw 点击卡片 A → "已选 1 张" → 点击卡片 B → 仍"已选 1 张"（单选重置）→ raw 点击「清除选择」→ "共 10 张"、按钮禁用。
- 留用户实机：Ctrl+点击加减选、Shift 范围重置（本机修饰键注入后 GetKeyStateForCurrentThread 读不到——CUA 分批注入的修饰键不被目标线程消息泵处理，与拖拽/滚轮同列环境限制）；滚轮放大置底观感。选择分流代码已人工审查（与拍板语义一致）。

## 风险与回滚方案

- 风险：①批量成功无回执后，极慢批量（数百张）的完成信号只剩视觉变化——可接受（进度条本身显示完成）；②demo 与 WinUI 行为已同步但历史注释较多，防止下轮被旧注释误导（本轮已同步三处）。
- 回滚：三个提交相互独立，可单独 revert；回滚 29e3949 需同步回 demo。
