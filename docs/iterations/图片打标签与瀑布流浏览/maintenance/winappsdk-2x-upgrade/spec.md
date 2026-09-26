# SPEC：Windows App SDK 1.6 → 2.5.1 升级

- 分支：`feature/winappsdk-2x-upgrade`
- 日期：2026-09-26
- 结论：**升级完成，回归全绿**；界面与交互无可见变化，部署形态不变（.NET 自包含 + WinAppSDK 框架依赖）。

## 1. 改动点（全部）

| 文件 | 改动 | 为什么必须 |
|------|------|-----------|
| `SimpleViewer.csproj` | `Microsoft.WindowsAppSDK` 1.6.240923002 → **2.5.1**；`Microsoft.Windows.SDK.BuildTools` 10.0.26100.1742 → **10.0.26100.4654** | 2.5.1 的 Base 包在 buildTransitive 里硬校验 BuildTools ≥ 10.0.26100.4654，低于即 NU1605 还原失败（实测） |
| `scripts/release.ps1` | XamlCompiler 预热改为扫 `microsoft.windowsappsdk*`（元包 + winui 子包）两族、按版本目录取最新；文件头注释 1.6 → 2.5 | 2.x 起 **XamlCompiler.exe 移入子包** `microsoft.windowsappsdk.winui\<ver>\tools\net472\`——只扫元包会预热到旧的 1.6 编译器（等于没预热） |
| `.github/workflows/release.yml` | 注释：目标机运行时 1.6 → 2.5 | 部署前置条件变化 |
| `README.md` | 前置条件 Windows App SDK `2.5+`（并注明目标机需 Windows App Runtime 2.5）；构建脚本说明与文档路径修正 | 同上 |
| `scripts\{fit,page-loop,zoom-path,walkthrough2,cold-start}-verify.ps1` | 屏幕外改造与口径修正（见 §4） | 用户约束：UI 验证不得占用前台 |

TFM / `TargetPlatformMinVersion` / `WindowsPackageType` / `WindowsAppSDKSelfContained` / XAML / 任何业务代码：**均未改动**。

## 2. 包级事实（本机实证，非文档转述）

- **net8.0 仍受支持**：WinUI 2.3.9 / Foundation 2.3.12 的托管资产是 `lib/net6.0-windows10.0.17763.0`，与
  `net8.0-windows10.0.19041.0` 兼容；本机仅有 .NET SDK 8.0.425，无需装新 SDK。唯一的 net8 下限来自
  ML 传递链（`Microsoft.Windows.AI.MachineLearning` → `System.Numerics.Tensors 9.0.0`，net8.0-only），对本项目无影响。
- **元包拆分**：2.5.1 → Base 2.0.4 / Foundation 2.3.12 / InteractiveExperiences 2.1.9 / WinUI 2.3.9 /
  DWrite 2.1.0 / Widgets 2.0.5 / AI 2.5.5 / ML 2.1.94 / Search 2.5.5 / Runtime `[2.5.1]`（精确锁定）。
- **运行期兼容策略**：框架依赖应用默认启用「最新 servicing 级别」，`RuntimeCompatibilityOptions.PatchLevel*`
  只能锁同一 major.minor；bootstrapper 按 `[0, max)` 匹配 → **目标机装了更高的 2.x 补丁时会跑那个补丁**。
  复现环境记录不能只写「2.x」，要写运行时具体版本（本机 `Microsoft.WindowsAppRuntime.2` = 2.5.1.0）。
- `WindowsAppSDKSelfContained` 对非库工程默认变 **true**（本项目显式 false）；类库上设 true 会硬报错
  （`WindowsAppSDKSelfContainedAudit`）。框架依赖需引用 `Microsoft.WindowsAppSDK.Runtime`（元包已含）。
- 命名空间、bootstrapper API、`app.manifest`、pri/MRT 流程、XamlCompiler 调用方式：**无变化**
  （本项目 `WindowsPackageType=None` + SelfContained=false 只走 bootstrapper 自动初始化）。
- 2.5.1 官方 release notes 与 winui3 2.5.1 发布说明**均无 known issues / 破坏性变更**；未发现图像解码、
  拖拽、DispatcherQueue 相关回归条目。

## 3. 验证矩阵（全部本机复跑）

| 项 | 命令 | 结果 |
|----|------|------|
| 构建 | `scripts\build.ps1` | 第 1 次尝试成功，0 错误；警告仅为既有的 WMC1506（x:Bind OneWay，与 1.6 时期相同） |
| 单元测试 | `dotnet test tests\...\--no-restore` | **120/120** |
| 图库屏外 soak | `scripts\offscreen-soak-check.ps1` | 窗口 (4560,200) 屏外；首屏缩略图非背景比 0.462；滚动 diff 765/39750；点侧栏「喜欢」diff 14224/39750（筛选生效）；alive=True；事件日志无新崩溃；`brushNew=0`、`pool acquired=11 returned=11` |
| 翻页压力 | `scripts\page-loop-verify.ps1` | 3 轮 ×24 次 UIA Invoke = **72/72，missed=0**；`NO-LOAD-FAILURE`（RO_E_CLOSED 修复在 2.5.1 上继续成立） |
| 打标埋点 | `tag-op-finalizer-verify.ps1 -Phase churn` | 三轮重建 `brushNew=0`（callsSinceLast=170/74/42，cache=19）；打标改名正确 |
| 打标链路 | `tag-op-finalizer-verify.ps1 -Phase chain` | 单图加标签 `photo_8.jpg → photo_8[喜欢].jpg`；右栏 chip 区像素差 **4462**（与 1.6 基线记录的 4462 一致）；chip ✕ 移除 → 还原；返回图库、筛选点击、筛选条 chip、组折叠/展开全部正常 |
| 单图适配几何 | `scripts\fit-verify.ps1` | 4 夹具全部 `band == box`（714x536 / 714x179 / 161x806 等），宽高比与源一致（如 1.332 vs 1.333） |
| 右栏面板/选择 | `scripts\walkthrough2-verify.ps1` | 面板上/下段同色（diff=0）；CARDS-FOUND=2；连点两卡保持「已选 1 张」（单选重置成立）；chip 落在标题区（y=304，OK-顶部区） |
| 换源翻页 | `scripts\zoom-path-verify.ps1` | 3/3 页 `red=True`（图像元素中心采样 254,40,40），横条序号 1/3→2/3→3/3 跟随 |
| 冷启动 | `scripts\cold-start-verify.ps1 3 60` | **0/3 轮**卡死告警（看门狗无假警报） |

## 4. 验证脚本的口径修正（本轮附带，均因「屏幕外」约束而暴露）

1. **`CopyFromScreen` → `PrintWindow`**：屏幕外窗口用 `CopyFromScreen` 只能抓到桌面（假失败）。
   `zoom-path`/`walkthrough2` 改为 `PrintWindow(PW_RENDERFULLCONTENT)` 截窗口，再按**旧版假设的窗口原点
   (40,40)** 贴回全屏画布 —— 既有采样字面量（如 (600,400)）因此继续成立。
2. **UIA 坐标改窗口内相对**：`walkthrough2` 圈卡片用的 `X∈[500,1100]` 是绝对屏幕坐标，窗口一挪出屏幕就
   恒不命中（曾报 CARDS-FOUND: 0）；改为减 `GetWindowRect().Left` 的相对判定。
3. **采样点改用元素中心**：`zoom-path` 原先写死 (600,400)，而单图自 2026-09-25 起按「可见区」适配、
   可见区随左右栏收展变化（本轮实测图像盒 314x209 居中偏下）。改为取 UIA Image 元素中心。
4. **raw 注入例外**：`tag-op-finalizer-verify.ps1` 的双击卡片/快捷键打标依赖真实光标与前台窗口，
   `SetCursorPos` 被限制在可见桌面内 → 该脚本**保持屏上 (40,40)**（全程 `Deactivate-Window` 不抢焦点），
   已在脚本头与 RULE 里写明例外理由。
5. `cold-start-verify.ps1` 原先让窗口屏上静置 60s×3 轮（违反约束）→ 启动后立即 `SetWindowPos` 屏外 +
   `SWP_NOACTIVATE`；该脚本此前是纯 ASCII，加了中文注释后**补齐 UTF-8 BOM**（PS5.1 铁律）。

## 5. 屏外/屏上渲染等价性（针对「窗口在屏幕外是否被降级渲染」的验证）

同一进程、同一窗口，先把窗口移到屏外抓一次、再移回 (40,40) 抓一次：
`OFF-SCREEN: redBBox=(444,370)-(756,578)` / `ON-SCREEN: redBBox=(444,370)-(756,578)` —— **逐位相同**，
`PrintWindow` 采到的内容与窗口是否在屏内无关。（该探针脚本在 `%TEMP%\probe-zoom-ab.ps1`，一次性使用。）

## 6. 遗留与后续

- **未做**：2.x 的分包裁剪（当前仍引用元包，AI/ML/Search/Widgets 的投影 DLL 会随包发布；运行时 MSIX
  本就包含全部原生组件，裁剪只减小产物体积、不改变运行期行为）——后续如在意体积可单独评估。
- **未决**：升级**不**解除 XAML 对象纪律（CsWinRT #2532 仍 open）；`XamlOptionalChanges`/`XamlChangeId`
  这批 2.3.1 起可选的 XAML 行为开关全部保持默认（未启用）。
- 发布物：`release\SimpleViewer-v1.1.0-win-x64.zip`；本机 `E:\App\Others\viewer` 更新并屏外复核。
