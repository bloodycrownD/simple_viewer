# SPEC：Windows App SDK 元包裁剪

- 分支：`chore/winappsdk-trim-2x`
- 日期：2026-09-26
- 结论：**裁剪完成并验证**；zip 60.4 MB → **43.1 MB**，展开 151.0 MB → 110.2 MB，
  框架装配与上一版**逐位相同**，实机回归全绿。发布动作（tag/CI/本机安装）按要求留给用户决定。

## 1. 改动点（全部）

| 文件 | 改动 | 为什么 |
|------|------|--------|
| `SimpleViewer.csproj` | `PackageReference Microsoft.WindowsAppSDK 2.5.1`（元包）→ **6 个子包直引**：Base 2.0.4 / Foundation 2.3.12 / InteractiveExperiences 2.1.9 / WinUI 2.3.9 / DWrite 2.1.0 / Runtime 2.5.1（附一行英文注释说明缘由） | 元包会把 AI/ML/Search/Widgets 一并拉进发布物 |
| `CHANGELOG.md` | 新增 `## [1.1.2]` 节（体积 60→43 MB、验证口径） | 用户可感知变更 |
| `docs/apm/RULE.md` | ①XamlCompiler 间歇崩溃条：归因由「Defender」纠正为「杀软（本机火绒在管、Defender 已停用）」+ 火绒信任区路径；②2.5.1 条追加第 ⑨ 点：元包裁剪定例与验证法 | 跨会话规则 |
| `scripts/build.ps1`、`scripts/release.ps1` | 注释里的「Defender 实时扫描」措辞改为「杀软（本机火绒在管）」 | 避免后人照着加无效的 Defender 排除 |
| `docs/.../maintenance/winappsdk-metapackage-trim/{prd,spec}.md` | 本留痕 | 项目惯例 |

**代码、XAML、TFM、`TargetPlatformMinVersion`、`WindowsPackageType`、`WindowsAppSDKSelfContained`、
`Microsoft.Windows.SDK.BuildTools` 引用均未改动。**

## 2. 包级事实（本机实证，非文档转述）

- **元包 `microsoft.windowsappsdk/2.5.1` 的 nuspec 是硬依赖**（`<dependency id=... />` 十条），
  没有「按需裁剪」的开关；其 `build/`、`buildTransitive/` 下**只有 ProjectCapability 声明、
  targets 是空壳**（逐文件读过）——所以换成子包直引**不会丢任何构建逻辑**。
- **所需 6 包的依赖闭包不含被裁的 4 包**（逐个 nuspec 核对）：
  WinUI → WebView2 + Base + Foundation + InteractiveExperiences；DWrite → Base；
  Foundation → Base + InteractiveExperiences；InteractiveExperiences → Base；Runtime → Base；Base → BuildTools 族。
- **被裁 4 包的功劳簿**：`Widgets 2.0.5`（x64 原生 2.47 MB + 投影 167 KB）、
  `Search 2.5.5`（3.47 MB + 190 KB）、`AI 2.5.5`（9 个投影 DLL ≈ 0.5 MB；其原生 DLL 不进 publish）、
  **`ML 2.1.94`（本体仅 151 KB，但它依赖 `Microsoft.Windows.AI.MachineLearning [2.1.74,3.0.0)`
  → 传递出 `onnxruntime.dll` 21.66 MB、`DirectML.dll` 18.70 MB、`Microsoft.ML.OnnxRuntime.dll`、
  `System.Numerics.Tensors.dll`）**。
- **源码零引用**：`findstr` 全仓扫 `Windows.AI` / `Windows.ML` / `Windows.Search` / `Windows.Widgets` /
  `OnnxRuntime` / `DirectML` / `WebView2`（`*.cs *.xaml *.csproj`，排除 obj/bin）**无命中**。
- 框架依赖路线不变：`Microsoft.WindowsAppSDK.Runtime` 仍在引用内（元包原本也含它），
  `WindowsAppSDKSelfContained=false` 不动，运行时仍是机器上的 Windows App Runtime 2.5。

## 3. 验证矩阵（全部本机复跑）

| 项 | 命令 | 结果 |
|----|------|------|
| 构建 | `scripts\build.ps1` | 第 1 次尝试成功；警告仅既有 WMC1506 |
| Debug 产物 | 目录扫描 | 无 `onnxruntime/DirectML/Windows.AI/Windows.Search/Windows.Widgets` 文件（96 文件 / 75.4 MB） |
| 单元测试 | `dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj` | **120/120** |
| 出包 | `scripts\release.ps1 -Version 1.1.2` | 第 1 次发布即成功；`SimpleViewer-v1.1.2-win-x64.zip` = **43.1 MB** |
| 体积 | publish 清单 | **264 → 249 文件**、158,308,925 → **115,565,318 B**（−40.8 MB）；**只少不多**（新增 0） |
| 裁剪正确性 | 上一版 zip 解压 vs 新 publish 全文件 SHA256 | 交集 **249/249 逐位相同**；差异 6 个 = `viewer.exe/dll/pdb`、`SimpleViewer.Core.dll/pdb`（自身程序集重建）、`viewer.deps.json`（依赖清单） |
| 依赖完整性 | 新 deps.json 关键字核对 | Base/Foundation/InteractiveExperiences/WinUI/DWrite/Runtime、`Microsoft.WinUI.dll`、`Microsoft.WindowsAppRuntime.Bootstrap.dll`、`runtimes/win-x64/native/...` runtimeTarget **全在**；deps 差异行 == 正好是被裁 4 包 + 其传递（AI/ML/OnnxRuntime/Tensors） |
| zip 完整性 | `Expand-Archive` 后清单比对 | 249 文件、字节总数与 publish 相同，无缺无多 |
| 图库屏外 soak | `offscreen-soak-check.ps1 -Exe release\publish\viewer.exe`（窗口定位 (4560,200) 屏外、不抢焦点） | 缩略图非背景比 **0.462**（与 v1.1.0/1.1.1 记录一致）；滚动 diff 755/39750；侧栏筛选 diff 14423/39750；`pool acquired=11 returned=11 created=11`；`retire` 全 0；无新崩溃；settings 已还原 |
| zip 解压副本 soak | 同上（`-Exe release\_zipcheck-112\viewer.exe`） | 全绿，数字与 publish 轮一致（ratio 0.462 / diff 755） |
| 单图适配几何 | `fit-verify.ps1 -Exe release\publish\viewer.exe` | 4 夹具 `band == UIA 盒`（714x536 / 714x179 / 161x806 / 714x536）、比例守恒（1.332 / 3.989 / 0.200 / 1.332），与 v1.1.1 记录**逐位一致** |
| 翻页压力 | `page-loop-verify.ps1` | 3 轮 × 24 = **72/72、missed=0**，`NO-LOAD-FAILURE`（RO_E_CLOSED 修复在裁剪后继续成立） |
| 走查 2 | `walkthrough2-verify.ps1` | `CARDS-FOUND: 2`；两次点击均「已选 1 张」；`PANEL-TOP-BOTTOM-DIFF=0` |
| 换源路径 | `zoom-path-verify.ps1` | 3/3 页红图可见、序号跟随 |
| 冷启动 | `cold-start-verify.ps1` | **0/3 轮**卡死 |
| 本机安装 | `E:\App\Others\viewer` 全文件 SHA256 vs v1.1.1 zip | **264/264 逐位相同** + 安装副本 soak 全绿 → 确认安装版无碍 |

> 说明：`viewer.exe/dll`、`Core.dll` 与两个 PDB 的差异是「同一份源码重新构建」的必然结果（MVID/时间戳），
> 源码本轮零改动；`deps.json` 的差异已逐行核对。故「框架装配逐位相同」这一判据成立。

## 4. 环境维护两项（用户清单）

### 4.1 AV 排除：前提不成立，未执行

实测（2026-09-26，本机 LAPTOP-F62U02Q3）：

- `Get-MpComputerStatus` → `AMServiceEnabled=False`、`AntivirusEnabled=False`、
  `RealTimeProtectionEnabled=False`；服务 `WinDefend` **Stopped**、`WdNisSvc` Stopped。
- 在管的安全软件是**火绒**：`HipsDaemon` 运行中、`HipsTray` 位于
  `E:\App\Others\Sysdiag\Huorong\Sysdiag\bin`（另有 `HipsDaemon.exe.dmp` 残留）。

结论：**「给 NuGet 缓存加 Defender 排除」在本机是空操作**，且 `Add-MpPreference` 需要管理员权限
（当前会话非提权，会弹 UAC）——故**未执行**，改为把归因写正。真正对症的做法（用户侧手动，火绒无公开 CLI）：

> 火绒主界面 → 设置 → 信任区 → 添加目录 → `C:\Users\BloodyCrown\.nuget\packages`
> （可选再加项目 `obj`/`bin`）。目标是把 `XamlCompiler.exe` 的冷启动扫描排除掉。

已同步：RULE 构建节 XamlCompiler 条、`build.ps1` 两处注释、`release.ps1` 一处注释。

### 4.2 三个 `viewer.old-*` 备份：已删除

删前核对（用户要求「确认无误后」）：

- 当前安装 `E:\App\Others\viewer` 与 `release\SimpleViewer-v1.1.1-win-x64.zip` 解压副本
  **264/264 文件 SHA256 逐位相同**；安装副本屏外 soak 全绿（ratio 0.462、pool 11/11、无新崩溃）。
- 三个备份均为**真实目录**（非链接）且内容都是程序文件：`viewer.old-py39` 211 文件/115.4 MB（2025-03-23）、
  `viewer.old-1.0.6` 244 文件/115.2 MB、`viewer.old-1.1.0` 264 文件/158.3 MB。
- **发现并留档用户数据**：`viewer.old-py39\_internal\config.json` 是用户在 Python 版里自定义的快捷键配置
  （含 `Ctrl+1/2/3` → 移动到 `..\good` / `..\keep` / `..\trash`、`Ctrl+A` 左旋等）——
  删除前复制到 `%LocalAppData%\SimpleViewer\legacy-py39-config.json`（994 B）。
  新版快捷键在 `%LocalAppData%\SimpleViewer\settings.json`，与旧格式无关。

释放 389 MB；`E:\App\Others` 下只剩 `viewer`（v1.1.1）。回退能力仍由 `release\` 下的
`SimpleViewer-v1.1.1-win-x64.zip` / `v1.0.6` zip 提供。

## 5. 风险与遗留

- **发布未做**：未打 `v1.1.2` tag、未 push、未更新本机安装（`E:\App\Others\viewer` 仍 v1.1.1）。
  `release\publish` 现为 v1.1.2 产物（旧 v1.1.1 由 zip 保留，属既有惯例）。
- 火绒信任区未添加（需用户 GUI 操作）；因此 XamlCompiler 的冷启动抽风仍会偶发，脚本重试/预热照旧兜底。
- GitHub 上 v1.1.1 draft 仍未点 Publish（历史 draft 按用户指示不清理）。
- 未裁剪 `Microsoft.Web.WebView2`（WinUI 的硬依赖，本项目不用但摘掉会动到编译引用面）；
  本次只摘元包显式声明、且闭包独立的 4 个组件，属保守范围。
