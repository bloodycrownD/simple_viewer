# SPEC：放大锚点偏离光标（ZoomAt 变换原点算错）

- 分支：直接在 `master` 收口（单一症状、单点修复）
- 结论：**根因 = 变换原点按「元素盒 = 槽位」估算，而 WinUI 的 Image 实际是「内容盒居中于槽位」**；
  改为按槽位中心实算后，实测不动点回到光标（偏差 ≤15 物理像素，测量噪声量级）。

## 1. 现象量化（修复前）

**方法（可复跑）**：夹具图 = 3000×2000（横）/ 2000×3000（竖），图内 35%/35% 处画红色方块作标记；
`-d` 直开单图 → 窗口固定 (40,40) 1200×800（屏上，raw 注入例外）→ 双击复位 → 光标移到指定点并
**点击一次**（本机注入移动会被丢弃，点击可刷新应用侧指针位置）→ 滚轮 2 档 → `PrintWindow` 前后各抓一帧 →
按标记的位移与尺寸比反解**不动点**：`A = p_before − (p_after − p_before)/(ratio − 1)`。

| 夹具 | 光标 (窗口内) | 修复前不动点偏差 | 修复后 |
|------|---------------|------------------|--------|
| 横图 3000×2000 | (521,422) | (+1.5, **+193.0**) | (+1.5, +1.0) |
| 横图 3000×2000 | (679,516) | (−3.5, **+189.0**) | (−3.5, −3.0) |
| 竖图 2000×3000 | (521,357) | (+3.3, **+67.6**) | (+3.3, +5.6) |
| 竖图 2000×3000 | (679,569) | (−11.0, **+47.3**) | (−11.0, −14.7) |

X 分量在两个光点位上都跟随光标（偏差 ≤11 px，且修前修后**逐位相同** ⇒ 属标记质心测量噪声）；
Y 分量是与光标位置无关的**常数偏移**（横图 ~190 px、竖图 ~47–68 px）——这正是用户感知的「固定点」。

## 2. 根因

应用内埋点（`SIMPLEVIEWER_ZOOM_DIAG=1` 时 ZoomAt 落一行 startup.log）给出实机数字：

```
[zoomprobe] anchor=(330.0,244.7) C=(392.7,153.7) actualTL=(288.0,216.7)
            box=209.3x139.3 margin=(280.0,76.0) host=785.3x496.0 s=1.000 t=(0.0,0.0)
```

- `anchor` 与光标位置完全一致（330×1.5=495…换算回窗口物理坐标即注入点）⇒ **输入侧无辜**。
- 代码算的 `C=(392.7,153.7)` 来自 `Padding + Margin + ActualSize/2`，前提是「元素盒左上 = Padding + Margin」
  = (288, **84**)。
- 实测元素实际落位 `actualTL=(288,216.7)`、盒高 139.3（= 位图 Uniform contain 后的内容盒高度，
  也是 UIA Image 盒的高度）；槽位高度 = 496 − 16 − 76 = 404 ⇒ 内容盒在槽位内**垂直居中**
  （84 + (404−139.3)/2 = 216.35 ✓）。
- 即：**WinUI 的 `Image` 在 `Stretch=Uniform` + 对齐 Stretch 下不把元素盒撑满槽位**——元素盒 = 内容盒，
  且在槽位内居中。横向恰因横图填满槽位宽度而无偏移，纵向则整段偏掉 (404−139.3)/2 = **132.35 DIP**
  （= 实测 132.7，与反解不动点一致）。

由内容反解的真实原点：`L = (391.5, 286.9)` = 内容盒中心 = **槽位中心**（两者恒等，因内容盒居中）✓。

## 3. 修复

`Views/SingleImageView.xaml.cs` 的 `GetViewerImageBoxCenter()` 改按**适配槽位中心**实算：

```csharp
var insetLeft = ImageHost.Padding.Left + ViewerImage.Margin.Left;
var insetTop  = ImageHost.Padding.Top  + ViewerImage.Margin.Top;
var slotWidth  = ImageHost.ActualWidth  - insetLeft - ImageHost.Padding.Right  - ViewerImage.Margin.Right;
var slotHeight = ImageHost.ActualHeight - insetTop  - ImageHost.Padding.Bottom - ViewerImage.Margin.Bottom;
return new Point(insetLeft + slotWidth / 2, insetTop + slotHeight / 2);
```

槽位中心 ≡ 内容盒中心（内容盒居中于槽位）⇒ 变换原点正确；盒未布局（首帧 ActualWidth/Height 为 0）
仍退回宿主中心。`ZoomAt` 的推导与公式不变（其数学本就正确，只是喂进去的 C 错了）。

另留诊断钩子：`App.ZoomDiagnosticsEnabled`（环境变量 `SIMPLEVIEWER_ZOOM_DIAG=1`）时每次缩放落一行
现场到 startup.log——默认关，续查锚点类问题不必再临时改码重编。

## 4. 验证

- **锚点**：见 §1 表（修复后四个组合偏差 ≤15 px，且 X 分量与修复前逐位相同 ⇒ 残余为测量噪声）。
- **适配不回退**：`fit-verify` 4 组夹具 `band == box`、宽高比守恒（数字与修复前一致）。
- **无连带回归**：`dotnet test` 120/120；`page-loop-verify` 72/72 零 missed + `NO-LOAD-FAILURE`；
  `offscreen-soak-check`（图库缩略图 0.462 / 滚动 / 筛选点击 / 无新崩溃 / `brushNew=0` / 位图池 11-11）。
- 复跑命令（探针为一次性脚本，落在 `%TEMP%`；要点见 RULE「量化探针」条）：
  夹具 → 屏上窗口 → 双击复位 → SetCursorPos + 点击刷新指针 → 滚轮 → PrintWindow 前后帧 → 反解不动点。

## 5. 顺带修掉的上游坑

本轮修 bug 期间构建曾失败：`XamlCompiler error WMC1007: Cannot resolve metadata for WinUI types`
——即 RULE 记录的「双 csproj 还原踩踏」（`dotnet restore <sln> --force` 让主工程视角丢 WinAppSDK 引用）。
`scripts/build.ps1` 的前置还原与冷重建兜底已一并改为**串行定向还原**（tests → Core → 主工程
`RestoreRecursive=false`）+ 主工程视角校验，实跑通过。
