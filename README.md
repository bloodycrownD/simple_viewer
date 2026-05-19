# Simple Viewer（WinUI 3）

Windows 本地图片查看器，WinUI 3 重写版。

- 需求与规格：`.apm/kb/docs/Iterations/SimpleViewer-WinUI3/`
- Python 旧版在分支 **`v1`**

## 前置条件

| 组件 | 版本 |
|------|------|
| Windows | 10 1809+（推荐 Windows 11） |
| Visual Studio | 2022 17.10+（含 WinUI / Windows 应用开发工作负载） |
| .NET SDK | **8.0**（`global.json` 指定 8.0.400+） |
| Windows App SDK | **1.6+**（NuGet `Microsoft.WindowsAppSDK`） |

## 构建与测试

**服务层 + 单元测试（仅需 .NET SDK）：**

```powershell
dotnet build src\SimpleViewer\SimpleViewer.Core.csproj -c Debug
dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug
```

**完整 WinUI 应用（`viewer.exe`）** 建议在 Visual Studio 2022 中 F5，或安装「Windows 应用开发」工作负载后：

```powershell
dotnet build SimpleViewer.sln -c Debug -p:Platform=x64
dotnet run --project src\SimpleViewer\SimpleViewer.csproj -p:Platform=x64
```

### 发布

```powershell
.\scripts\publish.ps1
```

产物：`src\SimpleViewer\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\viewer.exe`

### 命令行

| 参数 | 说明 |
|------|------|
| `viewer.exe <file>` | 打开指定图片 |
| `-d`, `--directory DIR` | 打开目录中的图片列表 |
| `-i`, `--index N` | 目录内 1-based 索引（仅 png/jpg/jpeg/gif，自然排序） |
| `-h`, `--help` | 控制台输出帮助并退出 |

```powershell
.\viewer.exe -d D:\pics -i 10
.\viewer.exe -h
```

## 项目结构

```
SimpleViewer.sln
src/SimpleViewer/          # WinUI 3 应用（输出 viewer.exe）
tests/SimpleViewer.Tests/  # xUnit 服务层测试
scripts/publish.ps1
```
