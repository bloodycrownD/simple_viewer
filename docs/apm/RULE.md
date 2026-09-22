# SimpleViewer 持久规则（跨会话生效）

## 构建

- 一律用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1`，不用裸 `dotnet build`。
- WinAppSDK 1.6 的 XamlCompiler 有间歇沉默崩溃（MSB3073、退出码 1 无输出，疑似 Defender 冷启动竞态）：脚本已内置 `-m:1 -nr:false` + 分级重试；仍失败时手动执行一次 XamlCompiler.exe 预热再重跑；根治需用户给 NuGet 缓存目录加 Defender 排除（用户未决策）。
- **MSB3073 排查先跑裸 `dotnet build -v:q -m:1 -nr:false`**（2026-09-22 实锤：build.ps1 的 `| Out-Null` 会吞掉 CS 错误行、只剩 MSB3073 尾巴，极具误导性）——x:Bind 绑定不存在的属性、样式 TargetType 错误、code-behind CS 错误都会以「无输出 MSB3073」形态出现，直跑裸命令看真实错误再分类处置。
- 冷重建（删 obj）后偶发"CS0234 引用级联失败"（还原增量误判）：`dotnet restore --force` 后再 build，一般第二次成功；成功产物有效，紧随其后的增量 build 失败可忽略。另：还原后校验 `obj\SimpleViewer.csproj.nuget.g.props` 是否存在，缺失则 `dotnet restore SimpleViewer.csproj --force` 重试（间歇不生成该文件时表现即全量 CS0234）。
- **XamlCompiler 确定性崩溃（2026-09-19 二分实锤，与 Defender 竞态无关）**：ChromeLayer 内 MainAreaGrid 作为**最后一个子元素**时 XamlCompiler Pass1 沉默崩溃（MSB3073、退出码 1 无输出，冷热 obj 均复现）——解法：把 MainInfoBar 块移到 MainAreaGrid 之后（Grid 按 Grid.Row 定位，子元素顺序不影响布局）；MainWindow.xaml 相应位置有注释，改此区域时保持该顺序。
- **双 csproj 同目录同 TFM 的还原踩踏（2026-09-19 深夜定论：并行竞写竞态）**：`SimpleViewer.csproj` 与 `SimpleViewer.Core.csproj` 共享 `obj\project.assets.json`，对主工程的 msbuild `-t:Restore` 会**并行**传递还原 Core——两个还原竞写同一文件、后写完者胜，故一切"间歇"（时而主工程视角、时而 Core-only 视角 → 全量 CS0234/CS0246 或 NETSDK1047）。**确定性修法（release.ps1 已内置）**：①串行还原 Core 先、主工程压轴（各带 `-nr:false` 防节点复用陈旧跳过）；②还原后双条件校验（assets 同时含 `/win-x64` 与 `Microsoft.WindowsAppSDK`——TFM 规范化去尾 .0，全串匹配会落空；Core 视角无 WinAppSDK）；③异常兜底=删 assets+nuget.g.props 后带 `RestoreForce=true` 串行重还原（守卫把竞态转化为重试，两次连跑全通）；④**publish/build 必须带 `--no-restore`**——隐式还原会再次触发竞写。`dotnet restore`（裸形态/sln/--force）都会踩同坑，勿用。
- **重建前必须 `taskkill /IM viewer.exe /F`**——运行中的 viewer 锁 DLL 导致复制失败（注意 cmd 下用 `&` 分隔，`;` 会让杀进程静默失败）。
- **发布三坑（2026-09-19 v1.0.0 打包实锤）**：①WindowsAppSDKSelfContained=true 的 unpackaged 布局启动即崩（ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml 定位失败——无包图时框架 pri 子图登记机制未打通，makepri 手工生成应用 pri 也无效）——release.ps1/workflow 走 **.NET 自包含 + WinAppSDK 框架依赖**（机器装一次 1.6 运行时，bootstrap 包图解析与 Debug 同机制）；②publish 默认不拷 Content 项，图标须 `CopyToPublishDirectory=PreserveNewest`（散装 xbf 框架依赖 publish 下会正常拷贝）；③**还原顺序铁律**：tests 项目还原会连带还原 Core 踩踏根 obj assets——tests 还原在前、主工程定向还原永远最后；定向还原还可能"降级"（assets 丢 RID 目标→NETSDK1047，nuget.g.props 缺失），release.ps1 已内置 g.props 缺失时 `dotnet restore --force` 补救。
- **发布命令行三坑（2026-09-19 release.ps1 实锤）**：① `dotnet msbuild` 的 verbosity 只认冒号形式 `-v:q`——`-v q` 报 MSB1016（dotnet restore/build/publish 认空格形式，裸 msbuild 解析器不认）；② PowerShell splat 数组首元素不得含动词——脚本行已写 `dotnet publish` 时 `@arr` 里再放 "publish" 会拼成 `publish publish 项目` 报 MSB1008"只能指定一个项目"；③ Release 配置 obj 冷路径下 XamlCompiler MSB3073 可连败（原样重试无效）——release.ps1 已内置"重试间自动执行遗留 input.json + nuget 缓存 net472\XamlCompiler.exe 预热"，手动等价解法见构建节 XamlCompiler 条。

## XAML 硬约束（违反 = 崩溃或运行期炸）

- 禁止 U+00AB/U+00BB 字面与任何 PUA 字形；FontIcon/SymbolIcon/Glyph 一律不用（图标用文本字符）。
- 不用 DockPanel（WinUI 3 无此控件）。
- **App.xaml 全局 Style 禁 TargetType=Border**（2026-09-22 实锤：`x:Key + TargetType="Border"` 使 XamlCompiler Pass1 沉默崩溃 MSB3073 退出码 1 无输出）——装饰性 chip/面板样式用 Button 载体（BasedOn GhostButtonStyle 族）或控件内联属性。
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
- **无 UI 回收站删除用 SHFileOperationW P/Invoke**（2026-09-22 实锤）：`Microsoft.VisualBasic.FileIO.UIOption` **没有 NoUI 成分**（仅 AllDialogs/OnlyErrorDialogs），OnlyErrorDialogs 失败时弹 Shell 错误框而非抛异常（批量场景连环卡死）；批量删除逐文件 `SHFileOperationW` + `FOF_SILENT|FOF_NOCONFIRMATION|FOF_ALLOWUNDO|FOF_NOERRORUI`，失败以非零返回码进 BatchOperationResult 聚合（先例 FileOperationService.DeleteToRecycleBin(paths)）。
- **图像解码管线两铁律（2026-09-19 修线条毛刺确立；铁律②口径随遮盖式布局重排更新）**：① WIC 缩小插值必须 `BitmapInterpolationMode.Fant`（默认 Linear 大倍率缩小丢高频细节生锯齿；单图 ImageLoaderService 与缩略图 ThumbnailService 两处 CreateTransform）；② 解码尺寸 = 整窗画布区——SingleImageView.ImageHost 在遮盖式布局（2026-09-19）下铺满整窗、几何恒定，解码即贴合显示区 1:1；显示层二次缩小会重新引入锯齿（按比显示区更大的区域解码同样不可取）。**侧栏/右栏/工具栏/状态栏均为 chrome 遮盖层，收展只改变遮盖范围、不得改变画布几何**（画布几何恒定 → 图片位置不动、不触发重解码，只有窗口 resize 改变画布）。该铁律约束**单图画布侧**（CanvasLayer/SingleImageView）；图库瀑布流区不适用——左栏本就是 MainAreaGrid 布局列（收展改变瀑布流可用宽度、经 resize 同路径重排属预期），图库右栏（选中集标签面板）2026-09-23 用户走查拍板同为布局列（首版浮层遮盖藏住缩略图被打回）。放大 ≥1.2× 经 EnsureFullResolutionAsync 按需换全分辨率源（每图一次）；WinUI 的 RenderTransform 缩放作用于源纹理而非布局光栅（实测），故换源即得高分辨率采样。

## 诊断

- 崩溃/卡死先看 `%LocalAppData%\SimpleViewer\logs\startup.log`：全局未处理异常 + UI 心跳看门狗（≥15s 无响应自动转储操作追踪与 UI 线程堆栈）+ 缩略图失败明细。
- `Services\DiagnosticTrace` 打点关键路径；`scripts\fg-monitor.ps1`（前置窗口监控）、`scripts\freeze-stress.ps1`（清缓存冻结压力测试）。

## 实机 UI 测试（2026-09-18 走查方法论）

- 交互类 bug（选择/修饰键/菜单）必须实机走查：UIA 元素树断言（`get_app_state` 找文本/计数）比视觉模型可靠；**主题/颜色断言用屏幕像素采样**（CopyFromScreen），视觉模型对深浅主题误判过两次。
- 合成 Shift/Ctrl+点击用 `scripts\shift-click-test.ps1`（SendInput）：① x64 INPUT 结构体必须按联合体 40 字节对齐（32 字节版 SendInput 静默失败且不报错）；② 注入前必须激活目标窗口（键盘事件只进前台窗口线程，后台注入的修饰键目标进程永远看不到）；③ PowerShell 须 `SetProcessDPIAware`，否则 SetCursorPos 坐标被 DPI 虚拟化重映射。
- **本机 SendInput 鼠标"移动"事件被丢弃（2026-09-19 实锤）**：注入返回成功（ret=1）但光标不动（SetCursorPos 正常、点击/键盘注入可达）——**拖拽手势无法自动化注入**，拖拽类交互只能用户实机验证；`scripts\drag-tag-test.ps1` 是当时的注入脚本（含 40 字节断言与相对移动），留作环境复测用。
- **CanDrag 手势在"可命中子元素占满宿主"时永不触发（2026-09-19 两连实锤）**：①Button.CanDrag 直接无效（Button 捕获指针）；②外层 Grid 包 CanDrag 也无效——按压全落在拉伸占满的 Button 上，宿主手势只在自身空白背景触发（Q&A "Drag Grid with Streached elements" 机制）。**卡片拖拽定论走命令式路径**：AddHandler(handledEventsToo:true) 监听按压/位移（XAML 挂接在 Button 标记已处理后收不到）+ 位移超阈值 `StartDragAsync(pointerPoint)`（DragStarting 照常在挂 CanDrag 的根元素触发）；ElementClearing 成对 RemoveHandler。拖拽类修复交付时必须同步给用户可一键复测的构建（Debug bin 或 zip），勿凭静态把握宣布修复。
- **UIA AXPress 不做视觉命中测试（2026-09-19 假阳性实锤）**：a11y Press 直调按钮动作、绕过遮挡层——遮盖式布局中右栏收起按钮整体被工具栏横行遮盖，AXPress 走查"通过"而用户真实鼠标点不到。涉及可点性/遮挡/ZIndex 的验证必须走 raw 鼠标路径（left_click 元素 target + `strategy=event`，坐标转真实事件经 Windows 命中测试）或核对目标 bounds 与上层元素 bounds 无重叠；AXPress 仅适用于纯命令性断言。滚轮缩放注入被 transport 前台校验拒绝（与移动丢弃同族），放大类交互仍留用户实机。
- **UI 改动交付前必须实机自检（2026-09-21 筛选面板打回实锤）**："build 过+单测绿"不等于 UI 能看——Flyout 默认 `FlyoutThemeMaxWidth=456` 会把内容硬夹窄（Width=620 无效，需 FlyoutPresenterStyle BasedOn `DefaultFlyoutPresenterStyle` 覆盖）；面板/弹层类改动至少跑一轮 `scripts\filter-panel-verify.ps1` 模式：**WScript.Shell AppActivate 激活窗口（裸 SetForegroundWindow 有前台锁）→ UIA AutomationId 定位 Invoke（code-behind 构造按钮的 UIA Name 为空，别按 Name 找）→ BoundingRectangle 量实际尺寸 → CopyFromScreen 采证视觉模型复核**。复现图库态须走 LastLibraryRoot 自动恢复（备份改 settings.json 启动后还原）——`-d` 是单图模式不触发扫描。
- **WinUI 视觉问题先查三个"隐形层"再动布局尺寸（2026-09-21 筛选面板四轮实锤）**：①Button **系统焦点框**（UseSystemFocusVisuals 默认开，点击/程序聚焦必现矩形框）——自绘面板类按钮一律显式 false；②**ScrollViewer 滚动条是悬浮 overlay**（叠在内容之上、不占布局位）——内容右缘会被压，右侧 Padding 留 ~14-18 让位，加宽度无效；③hover/PointerOver 底色块与预期边框混淆。用户连续两轮报同一问题时，先复现用户原场景（矮窗口/多条件/展开态）再改，勿按自己推断的场景修。
- UIA bounds 与截图光栅同坐标系（窗口物理尺寸）；`GetDpiForWindow`=144（150%）只影响应用内渲染密度，UIA 坐标即屏幕点，勿再乘缩放。
- **WinUI Border/Grid 无 automation peer**（2026-09-22 实锤）：AutomationId 挂其上 UIA 树不可见——走查锚点必须挂交互控件（Button/TextBlock）或用其内按钮/标题文本判定（如右栏面板用收起按钮+「已选 N 张」标题）。
- **UIA 驱动测试优先 Invoke 工具栏按钮而非键盘注入**（2026-09-22 实锤）：中文 IME 环境下 SendKeys Enter/Ctrl+A 可能被输入法/前台竞态吞掉（AppActivate 成功也未必送达）；且用户快捷键表 TryMatch 优先于 Ctrl+A/Enter 接管分支（如用户绑 Ctrl+A=左旋则全选接管收不到，属产品设计）——自动化测试先清空测试 settings 的 shortcuts，全选/进单图用 UIA Invoke「全选」按钮等价驱动。**注入含修饰键的键序列（如 '^a'）有 OS 级卡键风险**（2026-09-23 疑案：注入若被中断丢 keyup，GetAsyncKeyState 对所有应用报修饰键按下，用户普通点击全被当成 Ctrl+点击；按一次该修饰键松开即复位）——必须注入时收尾逐个补偿修饰键 keyup（keybd_event KEYEVENTF_KEYUP）。
- 临时改用户 `%LocalAppData%\SimpleViewer\settings.json` 做测试时：先备份、测完原样还原（app 只在改设置时写盘，退出不覆盖）。
- **XAML 模板"等价重构"不可免检（2026-09-19 实锤）**：cr/P2-9 把 WaterfallView 卡片 RowDefinition 从 [*,48] 改为 [48,*] 而 Grid.Row 分配未动——缩略图被钉死 48 DIP 细条、文字区吞掉剩余高度；提交信息称"零布局变化"，单测/CR 校验全过（XAML 布局不可单测），直到用户实机开图库才暴露。教训：①改 RowDefinition 行序/对齐/尺寸约束后必须实机走查**视觉布局**（截图行带投影+降采样字符画即可无视觉模型完成，工具已转正：scripts\visual-band-check.py、scripts\visual-ascii-view.py）；②执行轮走查范围须覆盖上轮改过的每个 XAML 文件的呈现，不能只测交互路径（AXPress/Enter 走查全绿但页面是坏的）。

## 杂项

- **记忆文件追加轮次必须用"文件末尾锚点追加"**（old_string 取当前末轮结尾文本，new_string = 该文本 + 新轮次）：禁止以某轮 `user:` 头作 old_string 整体替换——2026-09-19 连犯三次"吃掉上轮 user 头"+一次插错位置致轮次倒序；追加后用 `(?m)^(user|assistant):` 正则核对轮次序列（应严格 u→a 交替）且轮次顺序=时间顺序。
- 含中文的 PowerShell 脚本必须 UTF-8 带 BOM（PS5.1 无 BOM 按 ANSI 读会语法错）。
- 文档：PRD/spec 在 `docs\iterations\图片打标签与瀑布流浏览\`；交互原型在 `demo\`（改交互先对照它）。旧 `.apm\` 目录已废弃（APM 新家在 `docs\apm\`）。
- gitignore 已含 `test-library/`（本地测试图库，勿提交）。
