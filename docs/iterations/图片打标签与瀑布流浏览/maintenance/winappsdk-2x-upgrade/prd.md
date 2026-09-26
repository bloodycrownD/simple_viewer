# PRD：Windows App SDK 1.6 → 2.5.1 升级（maintenance）

- 状态：已实现（2026-09-26）
- 触发：用户拍板「我还是建议直接使用最新的框架，当初使用旧框架就是不对的。直接升级吧」
- 分支：`feature/winappsdk-2x-upgrade`
- 关联：`docs/apm/RULE.md`（构建节 · Windows App SDK 版本条 / 实机 UI 测试节 · 屏幕外三条边界）

## 背景

项目自 v1.0.0 起锁定 Windows App SDK **1.6.240923002**。1.6 已于 **2025-09-04 停服**，1.7（2026-03-18）、
1.8（2026-09-24）相继停服，**当前唯一受维护的是 2.x 线**（2.5.1 / 2026-09-16，维护至 2027-04-29）。
此前一轮评估的结论是「升级属待决策项：有迁移面，须独立一轮」——本轮即该独立轮。

动机不只是合规：拖拽打标走的 `StartDragAsync` 链路有已知的「拖后 GC 回收必崩」问题
（microsoft-ui-xaml #10948，在 1.8 上不复现而在 1.6 上无解），框架侧的新修复我们此前拿不到。
社区资料同时确认：升级**不**解除 XAML 对象纪律（CsWinRT #2532 仍 open，终结器跨线程 Release
路径未修）——画刷缓存 / 缩略图池化 / 显式退役队列继续是 v1.0.6 那套约束。

## 目标

1. 依赖换到 **2.5.1** 且构建链（`build.ps1` / `release.ps1` / CI）在本机与 GitHub Actions 上打通。
2. **功能与界面零回归**：图库瀑布流、单图翻页/适配、打标链路、筛选、主题、设置全部照旧。
3. 交付可发布的安装布局，并把本机 `E:\App\Others\viewer` 升到新版本（旧版可回退）。

## 非目标

- 不改任何业务行为、不新增 2.x 特性（AI / Widgets / Search 等一概不接）。
- 不改变部署形态：仍是 **.NET 自包含 + WinAppSDK 框架依赖**（`WindowsAppSDKSelfContained=false`）。
  2.x 里 `WindowsAppSDKSelfContained` 对非库工程默认变 true，本项目显式保持 false。
- 不做版本号策略变更（tag 规则照旧：新轮新 tag，不重打）。

## 验收标准（可复跑）

| 项 | 判据 |
|----|------|
| 构建 | `scripts\build.ps1` 成功（首次即过、无新增警告） |
| 单元测试 | `dotnet test` 120/120 |
| 屏外实机回归 | `offscreen-soak-check.ps1` / `page-loop-verify.ps1` / `zoom-path-verify.ps1` / `fit-verify.ps1` / `walkthrough2-verify.ps1` / `tag-op-finalizer-verify.ps1`（churn+chain）/ `cold-start-verify.ps1` 全绿，且**窗口不在用户前台** |
| 发布 | `release.ps1` 出包，解压产物启动成功（屏外复核） |
| 本机安装 | `E:\App\Others\viewer` 更新为新版本并可启动，旧版留有回退 |

## 环境事实（本轮核查）

- 本机已装 `Microsoft.WindowsAppRuntime.2` **2.5.1.0**（x64/x86），框架依赖路线无需再装运行时；
  注意 PFN 主版本号已从 `1` 变 `2`（`Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe`）。
- 本机 .NET SDK 仅 8.0.425：**2.5.1 仍支持 net8.0**，无需装新 SDK（详见 spec 的包级证据）。
