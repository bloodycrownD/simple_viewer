# PRD：Windows App SDK 元包裁剪（maintenance）

- 状态：已实现并验证（2026-09-26），**发布动作待用户点头**
- 触发：用户「剩下的可选事项（都不急，你说了才做）：WinAppSDK 2.x 组件裁剪（能省 ~16 MB 包体）、
  给 NuGet 缓存加 Defender 排除（治构建抽风）、以及 viewer.old-py39 / viewer.old-1.0.6 / viewer.old-1.1.0
  三个备份确认无误后删掉。**都可以做了**」
- 分支：`chore/winappsdk-trim-2x`
- 关联：`docs/apm/RULE.md`（构建节 · Windows App SDK 版本条第 ⑨ 点 / XamlCompiler 归因条）；
  上一轮 `maintenance/winappsdk-2x-upgrade/`（v1.1.0 节已预告「后续可单独做组件裁剪」）

## 背景

v1.1.0 把框架升到 Windows App SDK 2.5.1 时，2.x 已把包拆成 10 个子包，但项目仍引用元包
`Microsoft.WindowsAppSDK`，于是发布物里带上了本项目从不调用的组件：AI、机器学习（含
ONNX Runtime 与 DirectML 两个大文件）、搜索、小组件。升级轮的 CHANGELOG 已明写这条代价
（安装包 43 MB → 60 MB）并预告后续裁剪——本轮即兑现。

## 目标

1. 发布物不再携带未使用的组件，安装包体积回到升级前水平。
2. **框架装配逐位不变**：所需框架程序集必须与 v1.1.1 发布物字节一致，界面/功能零回归。
3. 构建链（`build.ps1` / `release.ps1` / CI 的假设）不因裁剪而失效。

## 非目标

- 不升级/降级框架（仍是 2.5.1）；不改 TFM、`WindowsAppSDKSelfContained=false`、部署形态。
- 不接任何 AI / ML / Search / Widgets API（只是把它们从依赖里摘掉）。
- 不动 `Microsoft.Windows.SDK.BuildTools` 引用（Base 包的版本下限校验仍需要它）。
- 本轮不做发布动作：不打 tag、不 push、不更新 `E:\App\Others\viewer`（发布是用户的决定）。

## 验收标准（可复跑）

| 项 | 判据 |
|----|------|
| 构建 | `scripts\build.ps1` 成功且无新增警告；Debug 产物不再含 AI/ML 文件 |
| 单元测试 | `dotnet test` 120/120 |
| 裁剪正确性 | 与上一版发布物做**全文件 SHA256 交集比对**：交集文件逐位相同（仅自身程序集/PDB/deps.json 允许变） |
| 依赖完整性 | 新 `viewer.deps.json` 仍含 Base/Foundation/InteractiveExperiences/WinUI/DWrite/Runtime + Bootstrap + win-x64 runtimeTarget |
| 体积 | zip 明显回缩（展开后应少 ~40 MB） |
| 实机回归 | `offscreen-soak-check.ps1`（publish 与 zip 解压副本各一轮，屏外不抢焦点）/ `fit-verify.ps1` / `page-loop-verify.ps1` / `walkthrough2-verify.ps1` / `zoom-path-verify.ps1` / `cold-start-verify.ps1` 全绿 |

## 同轮环境维护（user 清单另两项）

| 项 | 结论 |
|----|------|
| NuGet 缓存加 Defender 排除 | **前提不成立，未执行**：实测本机 Defender 已停用（三项全 False、`WinDefend` Stopped），在管的是火绒；Defender 排除是空操作且需提权 UIA。改为纠正文档归因 + 给出火绒信任区操作路径（见 spec §4） |
| 删除三个 `viewer.old-*` 备份 | **已删除**（389 MB）：删前核对安装目录与 v1.1.1 发布物 SHA256 逐位相同、安装副本 soak 全绿；py39 内的用户快捷键配置已留档 |
