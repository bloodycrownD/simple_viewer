# SimpleViewer 持久规则（跨会话生效）

## 构建

- 一律用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1`，不用裸 `dotnet build`。
- WinAppSDK 1.6 的 XamlCompiler 有间歇沉默崩溃（MSB3073、退出码 1 无输出，疑似 Defender 冷启动竞态）：脚本已内置 `-m:1 -nr:false` + 分级重试；仍失败时手动执行一次 XamlCompiler.exe 预热再重跑；根治需用户给 NuGet 缓存目录加 Defender 排除（用户未决策）。
- 冷重建（删 obj）后偶发"CS0234 引用级联失败"（还原增量误判）：`dotnet restore --force` 后再 build，一般第二次成功；成功产物有效，紧随其后的增量 build 失败可忽略。
- **重建前必须 `taskkill /IM viewer.exe /F`**——运行中的 viewer 锁 DLL 导致复制失败。

## XAML 硬约束（违反 = 崩溃或运行期炸）

- 禁止 U+00AB/U+00BB 字面与任何 PUA 字形；FontIcon/SymbolIcon/Glyph 一律不用（图标用文本字符）。
- 不用 DockPanel（WinUI 3 无此控件）。
- 含中文的 XAML 必须保存为 UTF-8 带 BOM（丢 BOM 会解析乱码）。
- 新引用 ThemeResource 键必须核对 WinUI 3 真实存在（`LayerFillColorSecondaryBrush` 事故曾致启动即崩，编译期不校验）。
- 交互用 `Click` 不用 `Tapped`：Tapped 仅真实指针手势触发，键盘/自动化/辅助功能路径静默失效。

## 主题与颜色

- 代码侧取 `Application.Current.Resources` 的 ThemeResource 画刷**不认 `RootGrid.RequestedTheme` 运行时覆盖**（按应用/系统主题解析）——深色下会取回浅色主题的深色画刷。代码颜色一律走 `TagSidebarConverters.IsDarkTheme` 双值模式（MainWindow.ApplyTheme 写入并重建侧栏）。
- 主题三态：Dark（默认）/Light/System，持久化在 `AppSettings.PreferredTheme`；主题按钮在 CommandBar 溢出菜单。

## 架构与边界

- 双 csproj 白名单：`Services\ Models\ Helpers\` 自动编入 Core（可单测）；UI 工程需要的新 Helper 须双侧 csproj 显式 Include（先例：ImageSourceHelper/ConsoleHelper）。tests 只引用 Core，可测逻辑必须下沉 Core。
- 索引（SQLite）与缩略图缓存是可丢弃缓存，事实源永远是文件名（TagSpaces 文件名协议 `base[tag1 tag2].ext`）；打开图库 = 清表重扫（`ClearAllItemsAsync`，防孤儿行污染候选集）。
- 缩略图 UI 应用必须走 `GalleryItemViewModel.UiApplyGate` 串行闸门（并发 SetSourceAsync 在首帧渲染期死锁过 UI）。
- 打标/重命名后的同步阶段用"宽松预测 + 磁盘事实判定"（`TryComposeNewPath`），不要用 BuildNewPath 的目标冲突预检（改名后目标必存在，会误判失败跳过同步）。

## 诊断

- 崩溃/卡死先看 `%LocalAppData%\SimpleViewer\logs\startup.log`：全局未处理异常 + UI 心跳看门狗（≥15s 无响应自动转储操作追踪与 UI 线程堆栈）+ 缩略图失败明细。
- `Services\DiagnosticTrace` 打点关键路径；`scripts\fg-monitor.ps1`（前置窗口监控）、`scripts\freeze-stress.ps1`（清缓存冻结压力测试）。

## 杂项

- 含中文的 PowerShell 脚本必须 UTF-8 带 BOM（PS5.1 无 BOM 按 ANSI 读会语法错）。
- 文档：PRD/spec 在 `docs\iterations\图片打标签与瀑布流浏览\`；交互原型在 `demo\`（改交互先对照它）。旧 `.apm\` 目录已废弃（APM 新家在 `docs\apm\`）。
- gitignore 已含 `test-library/`（本地测试图库，勿提交）。
