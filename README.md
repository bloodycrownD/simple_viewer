# Simple Viewer（WinUI 3）

Windows 本地图片查看器，WinUI 3 重写版。

## 功能

- 单图查看（翻页/旋转/全屏）
- 图库浏览（瀑布流，虚拟化布局渐进呈现）
- 图片打标签：TagSpaces 文件名标签、互斥标签组、标签筛选、批量打标（多选后一键打标）、快捷键打标

**标签协议**：标签写入文件名 `photo[标签1 标签2].jpg`（在原文件名后追加方括号标签段，空格分隔），与 TagSpaces 文件名标签模式互通——已有标签可被读取与继续编辑，不依赖额外元数据文件。

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
dotnet build SimpleViewer.Core.csproj -c Debug
dotnet test tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj -c Debug
```

**完整 WinUI 应用（`viewer.exe`）** 建议在 Visual Studio 2022 中 F5，或安装「Windows 应用开发」工作负载后：

```powershell
.\scripts\build.ps1          # 推荐：完整解决方案构建（见下方说明）
dotnet build SimpleViewer.sln -c Debug -p:Platform=x64
dotnet run --project SimpleViewer.csproj -p:Platform=x64
```

> `build.ps1` 内置"单节点禁复用 + 3 次原样重试 + 冷重建兜底"的分级重试机制，
> 用于规避 WinAppSDK 1.6 XamlCompiler 的间歇性沉默崩溃（MSB3073，环境级问题、与源码无关），
> 验证构建请一律使用该脚本。

### 发布

```powershell
.\scripts\publish.ps1
```

产物：`bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\viewer.exe`（`-p:Platform=x64` 下 SDK 输出带平台子目录；含 SQLite native `e_sqlite3.dll`，发布链已实测）

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
SimpleViewer.csproj      # WinUI 3 应用（输出 viewer.exe）
SimpleViewer.Core.csproj # 领域服务（可单独 build/test）
Services/ ViewModels/ Views/ Assets/
tests/SimpleViewer.Tests/
scripts/build.ps1        # 带重试的构建脚本（规避 XamlCompiler 间歇崩溃）
scripts/publish.ps1
```
