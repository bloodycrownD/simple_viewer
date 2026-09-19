# SimpleViewer 持久规则（跨会话生效）

## 构建

- 一律用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1`，不用裸 `dotnet build`。
- WinAppSDK 1.6 的 XamlCompiler 有间歇沉默崩溃（MSB3073、退出码 1 无输出，疑似 Defender 冷启动竞态）：脚本已内置 `-m:1 -nr:false` + 分级重试；仍失败时手动执行一次 XamlCompiler.exe 预热再重跑；根治需用户给 NuGet 缓存目录加 Defender 排除（用户未决策）。
- 冷重建（删 obj）后偶发"CS0234 引用级联失败"（还原增量误判）：`dotnet restore --force` 后再 build，一般第二次成功；成功产物有效，紧随其后的增量 build 失败可忽略。
- **双 csproj 同目录同 TFM 的还原踩踏（2026-09-19 实锤）**：`SimpleViewer.csproj` 与 `SimpleViewer.Core.csproj` 共享 `obj\project.assets.json`，`dotnet restore`（含 build.ps1 前置还原）有时只还原 Core，UI 工程 assets 被覆盖缺 WinAppSDK/CommunityToolkit → 全量 CS0234/CS0246。解法：`dotnet msbuild SimpleViewer.csproj -t:Restore -p:Platform=x64` 后 build.ps1 即恢复；遇"突然全量引用错误"先跑这条，别怀疑代码。
- **重建前必须 `taskkill /IM viewer.exe /F`**——运行中的 viewer 锁 DLL 导致复制失败（注意 cmd 下用 `&` 分隔，`;` 会让杀进程静默失败）。

## XAML 硬约束（违反 = 崩溃或运行期炸）

- 禁止 U+00AB/U+00BB 字面与任何 PUA 字形；FontIcon/SymbolIcon/Glyph 一律不用（图标用文本字符）。
- 不用 DockPanel（WinUI 3 无此控件）。
- 含中文的 XAML 必须保存为 UTF-8 带 BOM（丢 BOM 会解析乱码）。
- 新引用 ThemeResource 键必须核对 WinUI 3 真实存在（`LayerFillColorSecondaryBrush` 事故曾致启动即崩，编译期不校验）。
- 交互用 `Click` 不用 `Tapped`：Tapped 仅真实指针手势触发，键盘/自动化/辅助功能路径静默失效。
- **修饰键检测只判 `CoreVirtualKeyStates.Down`，禁止 `|| Locked`**：Locked 是 Caps/Num 类 toggle 位，对 Shift 无意义，但中文 IME 切中英文会把 Shift 的该位置位（曾致所有普通点击被误判 Shift 连选且无法取消）——4 处同模式坑（MainWindow/WaterfallView/TagSidebarControl/SettingsPage）已修，勿回潮。

## 主题与颜色

- 代码侧取 `Application.Current.Resources` 的 ThemeResource 画刷**不认 `RootGrid.RequestedTheme` 运行时覆盖**（按应用/系统主题解析）——深色下会取回浅色主题的深色画刷。代码颜色一律走 `TagSidebarConverters.IsDarkTheme` 双值模式（MainWindow.ApplyTheme 写入并重建侧栏）。
- 主题三态：Dark（默认）/Light/System，持久化在 `AppSettings.PreferredTheme`；主题按钮在 CommandBar 溢出菜单。

## 架构与边界

- 双 csproj 白名单：`Services\ Models\ Helpers\` 自动编入 Core（可单测）；UI 工程需要的新 Helper 须双侧 csproj 显式 Include（先例：ImageSourceHelper/ConsoleHelper）。tests 只引用 Core，可测逻辑必须下沉 Core。
- 索引（SQLite）与缩略图缓存是可丢弃缓存，事实源永远是文件名（TagSpaces 文件名协议 `base[tag1 tag2].ext`）；打开图库 = 清表重扫（`ClearAllItemsAsync`，防孤儿行污染候选集）。
- 缩略图 UI 应用必须走 `GalleryItemViewModel.UiApplyGate` 串行闸门（并发 SetSourceAsync 在首帧渲染期死锁过 UI）。
- 打标/重命名后的同步阶段用"宽松预测 + 磁盘事实判定"（`TryComposeNewPath`），不要用 BuildNewPath 的目标冲突预检（改名后目标必存在，会误判失败跳过同步）。
- **图像解码管线两铁律（2026-09-19 修线条毛刺确立；铁律②口径随遮盖式布局重排更新）**：① WIC 缩小插值必须 `BitmapInterpolationMode.Fant`（默认 Linear 大倍率缩小丢高频细节生锯齿；单图 ImageLoaderService 与缩略图 ThumbnailService 两处 CreateTransform）；② 解码尺寸 = 整窗画布区——SingleImageView.ImageHost 在遮盖式布局（2026-09-19）下铺满整窗、几何恒定，解码即贴合显示区 1:1；显示层二次缩小会重新引入锯齿（按比显示区更大的区域解码同样不可取）。**侧栏/右栏/工具栏/状态栏均为 chrome 遮盖层，收展只改变遮盖范围、不得改变画布几何**（画布几何恒定 → 图片位置不动、不触发重解码，只有窗口 resize 改变画布）。放大 ≥1.2× 经 EnsureFullResolutionAsync 按需换全分辨率源（每图一次）；WinUI 的 RenderTransform 缩放作用于源纹理而非布局光栅（实测），故换源即得高分辨率采样。

## 诊断

- 崩溃/卡死先看 `%LocalAppData%\SimpleViewer\logs\startup.log`：全局未处理异常 + UI 心跳看门狗（≥15s 无响应自动转储操作追踪与 UI 线程堆栈）+ 缩略图失败明细。
- `Services\DiagnosticTrace` 打点关键路径；`scripts\fg-monitor.ps1`（前置窗口监控）、`scripts\freeze-stress.ps1`（清缓存冻结压力测试）。

## 实机 UI 测试（2026-09-18 走查方法论）

- 交互类 bug（选择/修饰键/菜单）必须实机走查：UIA 元素树断言（`get_app_state` 找文本/计数）比视觉模型可靠；**主题/颜色断言用屏幕像素采样**（CopyFromScreen），视觉模型对深浅主题误判过两次。
- 合成 Shift/Ctrl+点击用 `scripts\shift-click-test.ps1`（SendInput）：① x64 INPUT 结构体必须按联合体 40 字节对齐（32 字节版 SendInput 静默失败且不报错）；② 注入前必须激活目标窗口（键盘事件只进前台窗口线程，后台注入的修饰键目标进程永远看不到）；③ PowerShell 须 `SetProcessDPIAware`，否则 SetCursorPos 坐标被 DPI 虚拟化重映射。
- **本机 SendInput 鼠标"移动"事件被丢弃（2026-09-19 实锤）**：注入返回成功（ret=1）但光标不动（SetCursorPos 正常、点击/键盘注入可达）——**拖拽手势无法自动化注入**，拖拽类交互只能用户实机验证；`scripts\drag-tag-test.ps1` 是当时的注入脚本（含 40 字节断言与相对移动），留作环境复测用。
- UIA bounds 与截图光栅同坐标系（窗口物理尺寸）；`GetDpiForWindow`=144（150%）只影响应用内渲染密度，UIA 坐标即屏幕点，勿再乘缩放。
- 临时改用户 `%LocalAppData%\SimpleViewer\settings.json` 做测试时：先备份、测完原样还原（app 只在改设置时写盘，退出不覆盖）。

## 杂项

- **记忆文件追加轮次必须用"文件末尾锚点追加"**（old_string 取当前末轮结尾文本，new_string = 该文本 + 新轮次）：禁止以某轮 `user:` 头作 old_string 整体替换——2026-09-19 连犯三次"吃掉上轮 user 头"+一次插错位置致轮次倒序；追加后用 `(?m)^(user|assistant):` 正则核对轮次序列（应严格 u→a 交替）且轮次顺序=时间顺序。
- 含中文的 PowerShell 脚本必须 UTF-8 带 BOM（PS5.1 无 BOM 按 ANSI 读会语法错）。
- 文档：PRD/spec 在 `docs\iterations\图片打标签与瀑布流浏览\`；交互原型在 `demo\`（改交互先对照它）。旧 `.apm\` 目录已废弃（APM 新家在 `docs\apm\`）。
- gitignore 已含 `test-library/`（本地测试图库，勿提交）。
