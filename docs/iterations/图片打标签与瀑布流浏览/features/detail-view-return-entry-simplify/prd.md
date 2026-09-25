---
date: 2026-09-25
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# detail-view-return-entry-simplify Feature PRD

## 背景与变更动机

用户走查反馈（原话）：**「留左上一个返回图库就够、不需要右上那个」**。

单图态此前存在两个「返回图库」入口，绑定同一 `ViewModel.BackToGalleryCommand`（CanExecute=HasGallery）：

| 入口 | 位置 | 处置 |
|------|------|------|
| A | 顶部信息横条首元素（`Views\SingleImageView.xaml:95`，Content="◀ 返回图库"，ToolTip「返回图库（Esc）」） | **保留**（唯一化后的返回入口） |
| B | 工具栏右簇单图组首元素（原 `MainWindow.xaml:167-170`，Content="返回图库"，无 x:Name/AutomationId/ToolTip） | **删除** |

删除 B 必须同删其前置竖线（原 `MainWindow.xaml:171-176` 的 `Rectangle`，Width=1/Height=16/Margin=6,0）：
该竖线是「返回图库 | 翻页」的分组分界，不同删会在单图组首留下悬空竖线。

## 范围说明（相对原需求）

- **推翻** `features\back-button-and-chrome-simplify` 的既有验收口径：其 prd 第 34 行原载「工具栏"返回图库"与 Esc 保留」——本次明确撤销该条（工具栏入口删除），Esc 路由与横条按钮保留。
- `features\batch-tag-management` 的工具栏右簇清单随之**失效**：其 prd 第 87 行「**右簇**：返回图库、上一张、下一张、左旋、右旋（仅单图模式显示）+ 设置、主题、全屏」与第 112 行 D1 表（含「返回图库」）不再成立；右簇单图组现为 上一张/下一张/左旋/右旋。
- **不动** `MainViewModel.SingleOnlyControlsVisibility` 本体：单图组（含组尾分界竖线）仍由它整体显隐（`MainWindow.xaml:171` 与图库组的 `:77` 两处消费不变），只是组内少一个按钮；仅同步其 XML doc 口径。
- **不动** Esc 路由（`MainViewModel.TryRouteEscape`）、`BackToGalleryCommand` 的 CanExecute 灰态语义。工具栏无 CommandBar（Border+ScrollViewer+StackPanel）、`Models\ViewerCommand.cs` 无 BackToGallery 成员、无 KeyTip/AccessKey——不存在需同步的快捷键或键位提示。

## 影响模块与接口

| 模块 | 变更 |
|------|------|
| MainWindow.xaml | 删单图组首「返回图库」按钮 + 其前置分界竖线；右簇元素清单注释与单图组分组注释由「三段」改「二段（翻页 \| 旋转）」 |
| ViewModels/MainViewModel.cs | `SingleOnlyControlsVisibility` 仅 XML doc 更新：去掉「返回图库」、注明返回入口唯一化 + 该属性仍是该组整体显隐开关勿误删 |
| scripts/toolbar-catalog-verify.ps1 | 单图态探针由「返回图库」改「上一张」（FindBtnByName 精确名匹配）；S 段新增 `AssertBtn '返回图库' $false`；回图库注入改走横条「◀ 返回图库」 |
| scripts/single-strip-verify.ps1 | D1 诊断循环期望名由「返回图库」改横条「◀ 返回图库」 |

接口层面无破坏性变更：命令、路由、灰态判定、其余走查脚本引用的按钮名（`◀ 返回图库` 等）全部保持。

## 验收标准

- [x] 顶部信息横条首元素「◀ 返回图库」仍在：实机 UIA 实测 `rect=492,147 129x43`，`enabled=False`（`-d <dir> -i 1` 直开无图库 → HasGallery=false 的既有灰态语义）。
      （有图库时启用并点击回图库的路径本轮未复跑：该绑定本次未改，由 back-button-and-chrome-simplify 实机走查覆盖。）
- [x] 工具栏无该按钮：UIA 精确名查「返回图库」= 不存在（工具栏组内按钮名为「上一张/下一张/左旋/右旋/设置/主题/全屏」）。
- [x] 单图组首无悬空竖线：工具栏行像素扫描只剩 2 条 16DIP 竖线区段——x=1124（下一张|左旋，翻页|旋转分界）与 x=1293（右旋|设置，单图组|常显收尾组分界）；「上一张」左侧（x<933）无任何竖线列。
- [x] Esc 路由与 CanExecute 灰态不变（对应代码未触碰，`TryRouteEscape` 与按钮互相独立）。
- [x] 走查脚本已同步且单图工具栏覆盖不丢：`single-strip-verify.ps1` 实机全绿（D1 六按钮可见 + 全选/删除隐藏 OK + NO-RED-SEAM + 0 FAIL/MISSING）；`toolbar-catalog-verify.ps1` 图库段 11 断言全 OK、批量目录 C3 与删除选中集 D2 端到端全过。

## 测试用例

1. `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → 120/120 全绿。
2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` → 第 1 次尝试成功（exit 0）。
3. 实机 `scripts\single-strip-verify.ps1`（CLI 直开单图 + 3 张夹具）：
   D1 段 `◀ 返回图库/上一张/下一张/左旋/右旋/设置` 全「可见」、`全选/删除` 隐藏 OK；
   顶部横条文本与按钮 OK；`NO-RED-SEAM: 顶带全宽无透图缝隙`；`SETTINGS-RESTORED`。
4. 实机 `scripts\toolbar-catalog-verify.ps1`（临时图库 4 张）：
   图库段 6 常显/图库组 + 5 单图组按钮断言全 OK（含「返回图库」隐藏）；批量目录 C3 端到端；
   删除选中集 D2 端到端。**单图段因 Enter 注入未生效按脚本既定口径跳过**（该段覆盖由用例 3 承接，
   并在本轮另用临时探针复核：工具栏无「返回图库」、横条按钮在、无悬空竖线）。
5. 临时探针（不入库）：`-d <dir> -i 1` 直开单图 → 精确名「返回图库」不存在；横条「◀ 返回图库」@492,147；
   `PrintWindow` 截屏后逐列扫竖线（标定色 RGB 35,35,41，阈值连续 ≥16px）。
