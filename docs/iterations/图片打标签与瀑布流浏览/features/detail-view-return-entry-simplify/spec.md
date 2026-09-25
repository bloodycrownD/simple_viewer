---
date: 2026-09-25
agile_trace: true
---

# detail-view-return-entry-simplify 实现规格（SPEC）

## 变更点清单

| 提交 | 内容 |
|------|------|
| 25012bd | 删工具栏单图组首「返回图库」按钮及其前置分界竖线（MainWindow.xaml）；右簇元素清单注释与单图组分组注释改「翻页 \| 旋转」二段；MainViewModel.SingleOnlyControlsVisibility 的 XML doc 去「返回图库」并注明入口唯一化/勿误删；toolbar-catalog-verify.ps1 与 single-strip-verify.ps1 同步（探针改「上一张」、S 段新增工具栏该按钮不存在断言、回图库注入改横条按钮、D1 期望名改横条按钮）。4 files changed, +31/−25 |

## 详细改动说明

### 1. 删按钮 + 删前置竖线（MainWindow.xaml）

- 删除单图组（`SingleOnlyControlsVisibility` 绑定的 StackPanel，现 `MainWindow.xaml:168-200`）的**首元素**：`Button Content="返回图库" Command={x:Bind ViewModel.BackToGalleryCommand} Style=GhostButtonStyle`（原 167-170 行）。
- 同删其后的 `Rectangle Width=1 Height=16 Margin=6,0 Fill={ThemeResource CardStrokeColorDefaultBrush}`（原 171-176 行）——它是「返回图库 | 翻页」的分组分界；若不删，单图组首（上一张左侧）会出现一条只分隔空白的悬空竖线。
- 保留两条仍有效的竖线：下一张|左旋（翻页|旋转分界，现 `:180-185`）与组尾（单图组|设置/主题/全屏分界，现 `:194-199`）。分组结构由「三段」变「二段」，与像素扫描结果一致（工具栏行只剩 x=1124/x=1293 两条竖线）。

### 2. 三处注释口径同步

- `MainWindow.xaml` 右簇清单注释（现 `:36-43`）：右簇描述去掉「返回图库」，注明「原『返回图库』按钮已删——detail-view-return-entry-simplify 2026-09-25，单图返回入口唯一化为顶部信息横条首元素『◀ 返回图库』」。
- `MainWindow.xaml` 单图组分组注释（现 `:162-167`）：三段语义改二段，并写明「删按钮必须同删其前置竖线，否则组首悬空竖线」。
- `ViewModels\MainViewModel.cs` `SingleOnlyControlsVisibility` 的 XML doc（现 `:3254-3260`）：清单去掉「返回图库」，补「返回入口唯一化为横条首元素（BackToGalleryCommand 唯一可见绑定点）」「本属性仍是该组（含组尾分界竖线）的整体显隐开关，勿因删按钮而误删」。

### 3. 两个走查脚本的同步改法（验证资产不许烂）

`scripts\toolbar-catalog-verify.ps1`（`FindBtnByName` 是**精确名**匹配，横条按钮名「◀ 返回图库」不命中）：

| 原写法 | 现写法 |
|--------|--------|
| 重试循环用 `FindBtnByName $win '返回图库'` 当「是否已进入单图模式」探针（`$backBtn`，尝试 3 次） | 探针改 `FindBtnByName $win '上一张'`（变量 `$singleProbe`），循环/日志/`if` 分支同步改名 |
| `foreach ($n in @('返回图库','上一张','下一张','左旋','右旋')) { AssertBtn ... $true 'S' }` | 期望列表去掉「返回图库」，并新增 `AssertBtn $win '返回图库' $false 'S'`（断言工具栏不再有该按钮） |
| `Invoke $backBtn` 回图库 | `FindBtnByName $win '◀ 返回图库'` → `Invoke`；缺失时输出 `STRIP-BACK-BTN-MISSING`（脚本原有 `AssertBtn` 对缺失按钮判「隐藏」即通过，故不存在假失败风险） |

`scripts\single-strip-verify.ps1`：D1 诊断循环期望名 `'返回图库'` → `'◀ 返回图库'`（该脚本 101-107 行原本就按 `-like '*返回图库*'` 找横条按钮，改后语义自洽）。

其余脚本（repro-strip-float1~5、repro-crash-tag、zoom-path、page-loop、walkthrough2、filter-panel、sidebar-select、undefined-panel、cold-start）引用的是横条按钮名 `'◀ 返回图库'` 或别的按钮名，**未动**（全仓 grep 核对）。

### 4. 未改动项（明确边界）

- `MainViewModel.TryRouteEscape`（Esc 返回图库）与 `BackToGalleryCommand` 的 `CanExecute = HasGallery` 灰态：零改动。
- `SingleOnlyControlsVisibility` 本体与通知链（`OnCurrentModeChanged`）：零改动。

## 测试策略

- 单测：`dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug` → **120/120 通过**（失败 0、跳过 0）。
- 构建：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` → 第 1 次尝试成功，exit 0，全新 exe 落 `bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe`。
- 实机 UIA 断言（`single-strip-verify.ps1`，exit 0，全文 0 FAIL/MISSING）：

```
=== 1. 单图工具栏 D1 ===
◀ 返回图库: 可见 / 上一张: 可见 / 下一张: 可见 / 左旋: 可见 / 右旋: 可见 / 设置: 可见
全选(应隐藏): 隐藏OK / 删除(应隐藏): 隐藏OK
=== 2. 顶部横条 ===  序号文本: OK "1 / 3"   显示名 d1.jpg: OK   返回图库浮层按钮: OK rect=492,147
=== 3. 顶带透红扫描 ===  NO-RED-SEAM: 顶带全宽无透图缝隙 ✓
DONE / SETTINGS-RESTORED
```

- 实机 UIA 断言（`toolbar-catalog-verify.ps1`，exit 0）：图库段 11 断言全 OK（含 `BTN-G-OK: [返回图库] 隐藏`）；`SINGLE-MODE-ENTER-FAILED`（Enter 注入未生效，脚本既定口径跳过单图段）；批量目录 C3（4 张全部改名成功）与删除选中集 D2（4 张入回收站、已选 0）端到端通过；`SETTINGS-RESTORED`。
- 无悬空竖线的像素证据（临时探针，不入库）：`PrintWindow` 整窗截图 → 工具栏按钮行（窗口局部 y=48..96）逐列求「标定竖线色 RGB(35,35,41) 的最长连续游程 ≥16px」→ 输出两条区段：`x=1124..1125`、`x=1293..1294`（各 maxRun=24px=16DIP）；「上一张」左侧（x 900..932）恒为背景色 (39,39,46)，无竖线列。

## 风险与回滚方案

- 风险①**误留悬空竖线**：已同删该按钮的前置竖线，并以像素扫描（上节）核实工具栏行只剩两条有效竖线；若后续再动单图组成员，需同步检查「组首竖线」约束。
- 风险②**走查脚本假失败被误读为回归**：`FindBtnByName` 为精确名匹配，横条按钮名是「◀ 返回图库」——两个脚本已分别改用「上一张」探针与横条按钮名；本轮两脚本实机复跑通过。反向风险同样存在：删按钮后若脚本仍断言「返回图库」可见，会报假失败——已改为断言**不存在**。
- 风险③**BOM/编码**：三个被改的 XAML/脚本文件（MainWindow.xaml、两个 .ps1）均为 UTF-8 **带 BOM**，提交前后校验首字节 EF BB BF 通过（.ps1 无 BOM 会被 PowerShell 5.1 按 ANSI 解读，中文断言串会乱码）。
- 回滚：单提交（25012bd）独立可 revert；revert 后需同步回滚两个走查脚本的按钮名断言（否则脚本失败）。
