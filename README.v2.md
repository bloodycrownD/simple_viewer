# Simple Viewer v2（WinUI 3）

本目录为 git worktree，分支 **`v2`**，用于 WinUI 3 重写。

- 主仓库（Python / master）：`d:\Dev\Python\simple_viewer`
- 需求与规格：`.apm/kb/docs/Iterations/SimpleViewer-WinUI3/`

## 前置条件

| 组件 | 版本 |
|------|------|
| Windows | 10 1809+（推荐 Windows 11） |
| Visual Studio | 2022 17.10+（含「使用 C++ 的桌面开发」与 WinUI） |
| .NET SDK | **8.0**（`global.json` 指定 8.0.400+） |
| Windows App SDK | **1.6+**（由 `Microsoft.WindowsAppSDK` NuGet 引用） |

安装 .NET 8 SDK：<https://dotnet.microsoft.com/download/dotnet/8.0>

安装 Windows App SDK 工作负载（任选其一）：

```powershell
dotnet workload install maui-windows
# 或在 Visual Studio Installer 中勾选「Windows application development」
```

## 构建与测试

**服务层 + 单元测试（dotnet CLI，无需 Visual Studio）：**

```powershell
cd d:\Dev\Python\simple_viewer-v2
dotnet build src\SimpleViewer\SimpleViewer.Core.csproj -c Debug
dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug
```

**完整 WinUI 应用（`viewer.exe`）** 需要 Visual Studio 2022 的 **Windows 应用开发** 工作负载（提供 `Microsoft.Build.AppxPackage.dll` 等 PRI/Appx 任务）。仅安装 .NET SDK 时，`dotnet build` 可能在打包阶段失败，但核心程序集仍会编译。

```powershell
dotnet build SimpleViewer.sln -c Debug -p:Platform=x64
dotnet run --project src\SimpleViewer\SimpleViewer.csproj -p:Platform=x64
```

发布产物文件名为 **`viewer.exe`**（`AssemblyName` = `viewer`）。

## 推送 v2 分支

```powershell
cd d:\Dev\Python\simple_viewer-v2
git push -u origin v2
```

## 项目结构

```
SimpleViewer.sln
src/SimpleViewer/          # WinUI 3 应用（输出 viewer.exe）
tests/SimpleViewer.Tests/  # xUnit 服务层测试
```
