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

### 发布（Release）

需要与完整 WinUI 构建相同的环境（Visual Studio 2022 + Windows App SDK 工作负载）：

```powershell
cd d:\Dev\Python\simple_viewer-v2
.\scripts\publish.ps1
```

或手动执行：

```powershell
dotnet publish src\SimpleViewer\SimpleViewer.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained false -p:PublishSingleFile=true
```

产物路径（默认）：

```
src\SimpleViewer\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\viewer.exe
```

### 命令行

| 参数 | 说明 |
|------|------|
| `viewer.exe <file>` | 打开指定图片 |
| `-d`, `--directory DIR` | 打开目录中的图片列表 |
| `-i`, `--index N` | 目录内 1-based 索引（仅图片扩展名，自然排序） |
| `-h`, `--help` | 在控制台输出帮助并退出（不显示窗口） |

示例：

```powershell
.\viewer.exe -d D:\pics -i 10
.\viewer.exe -h
```

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
