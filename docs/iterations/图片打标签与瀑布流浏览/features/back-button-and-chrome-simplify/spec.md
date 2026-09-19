---
date: 2026-09-19
agile_trace: true
---

# back-button-and-chrome-simplify 实现规格（SPEC）

## 根因 / 方案摘要

- **状态栏**：单图内容与右栏同源重复（UpdateStatusText 同方法写两处）；图库模式显示残留文案（OnCurrentModeChanged 不清 StatusText）。整体删除组件，4 类独有信息迁 InfoBar（成功静默/失败弹口径），BottomChromeHeight 避让链随之整体移除。
- **返回按钮**：单图视图左上加画布层浮层；关键约束是左侧必须让出 chrome 层左栏宽度（否则被左栏遮盖点不到，与 b1063b0 右栏按钮被遮同构）。
- **清除选择按钮**：默认单选模型下"点击即单选"已覆盖清除场景，按钮撤销（29e3949 的选择语义保留）。

## 变更点清单

| 提交 | 内容 |
|------|------|
| 0725322 | 删状态栏：MainWindow.xaml 删 StatusBarRow+Row3；MainViewModel 删 StatusText 属性及 7 处写入——UpdateStatusText 拼接行删（右栏三字段写入保留）、RenameFilesAsync 回执迁 InfoBar（全成功静默/有失败弹 Warning 聚合数）、删除/移动/加载失败 4 处走 ShowInstantTagFeedback（注释说明非 tag 场景共用）；BottomChromeHeight 半条链整体移除（订阅/属性/ApplyChromeInsets 底部用法归零），TopChromeHeight 顶部链保留。过程发现 XamlCompiler 确定性崩溃 bug（MainAreaGrid 为 ChromeLayer 末子元素时 Pass1 沉默崩溃），以 MainInfoBar 后置规避并注释留痕（已入 RULE） |
| bbd3b5b | 返回按钮：SingleImageView 新增 BackToGalleryOverlay（GhostButtonStyle、Content="◀ 返回图库"、ToolTip 注明 Esc、Command=BackToGalleryCommand）；ApplyChromeInsets 扩展 `Margin=(left, TopChromeHeight, 0, 0)`，left 依 IsSidebarCollapsed（展开 280+12/折叠 36+12，常量与 MainWindow 左栏宽同步）；订阅 IsSidebarCollapsed 变化跟随移动；无图库 CanExecute 禁用灰态 |
| c014350 | 删清除选择按钮：状态行还原单 TextBlock；删 HasSelection/ClearSelection 命令/NotifyCanExecuteChanged 联动（GalleryStatusText 通知保留）；ClearCardSelection 本体保留（6 个存活调用点：重开图库/Esc/单选重置/范围重置/筛选切换×2） |
| 06b8811 | 补删单图底部文件名栏（用户复查反馈）：SingleImageView 删 FileNameOverlay 浮层，RebuildFileNameInlines 仅重建右栏 InfoFileNameText；demo 顶部 viewer-file 区（fileName+path）与对应 CSS 同步删除 |

## 详细改动说明

- InfoBar 迁移明细：RenameFilesAsync 三处 statusPrefix（重命名标签/删除标签/删除标签组）统一改为"failed==0 静默，否则 Warning + 成功/失败聚合数"；ShowInstantTagFeedback 从"即时 tag 反馈"扩为通用即时错误提示（doc 注释更新），DeleteAsync/MoveToFolderAsync（未配置+失败）/LoadCurrentAsync 失败 4 处接入。
- 避让链简化后：右栏展开/折叠条与文件名栏底部 Margin 恒 0（状态栏已不存在）；顶部避让（工具栏+InfoBar）机制不变。
- 返回按钮在图库模式随 SingleImageHost 隐藏，无需额外处理；放大置底后按钮是 ZIndex=1 浮层，放大溢出会盖住它（与右栏一致，属置底语义）。

## 测试策略

### 测试用例

- 自动化：`dotnet test` 71 项全绿（每 commit 验证 ×4）；build.ps1 通过（含 XamlCompiler bug 规避后的全量验证）；BOM 与禁用字符扫描通过。
- 实机走查（主代理，已完成）：CLI 直开单图——无状态栏、返回按钮禁用灰态且位置避开左栏/工具栏；FolderPicker 开图库——状态行无按钮纯文本；双击进单图——返回按钮启用；raw 鼠标路径点击"◀ 返回图库"——真实命中并回图库（选中保留）。
- 留用户实机：重命名失败回执与删除失败提示的 InfoBar 表现（失败路径需文件占用等条件，难自动化）。

## 风险与回滚方案

- 风险：①MainAreaGrid/MainInfoBar 子元素顺序是 XamlCompiler bug 规避约束，重排会复现崩溃（已注释+入 RULE）；②返回按钮与左栏宽度常量（280/36）双处维护，改左栏宽需同步。
- 回滚：三提交独立可 revert；回滚 0725322 需同步恢复 BottomChromeHeight 链与 StatusText 写入。
