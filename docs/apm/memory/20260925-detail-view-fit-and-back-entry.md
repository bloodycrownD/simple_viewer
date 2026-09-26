---
date: 2026-09-26 20:40
title: 本会话全程：部署替换旧 Python 版 → 详情页两项优化 → 两轮崩溃根治（终结器跨线程释放 XAML 对象）→ v1.0.5/v1.0.6 发布 → WinAppSDK 1.6→2.5.1 升级 → v1.1.0 发布 → 放大锚点修复 v1.1.1
keywords: 部署, E:\App\Others\viewer, 旧 Python 版替换, 单图详情页, 适配盒=可见区, 返回入口唯一化, 崩溃根治, 终结器跨线程释放, fastfail 0xc0000409, BitmapImage 无 Dispose, 缩略图对象池, TagBrushCache, 屏幕外验证, v1.0.5, v1.0.6, WinAppSDK 2.5.1 升级, BuildTools 下限, XamlCompiler 子包, WMC1007 还原踩踏, 跨版像素 A/B, 放大锚点, 反解不动点, Image 内容盒居中, v1.1.0, v1.1.1
abstract: 一条会话记忆，覆盖 2026-09-25~26 全程：①把 v1.0.4 产物铺到 E:\App\Others\viewer 替换 Python 3.9/PyInstaller 旧版应用（旧目录改名 viewer.old-py39 留回滚）；②详情页两项优化（删工具栏重复「返回图库」+ 适配盒由「整窗」改「未被 chrome 遮挡的可见区」，含"DPI 虚拟化探针误导静态判断"的反转教训）；③打标期崩溃根治（转储铁证=终结器线程跨线程 Release XAML 对象致 native 堆损坏；画刷 churn 静态缓存化 + 图像源四类收口）；④二次全仓扫描后修复残留（BitmapImage 无 IClosable → 退役队列对它是空操作 → 缩略图位图对象池化；退役链异常隔离与 Released/Undisposable 分计；Run/Style 复用；UiKeepAlive 保命环；retire:/pool: 埋点）；⑤发布 v1.0.5 与 v1.0.6（tag+push+CI draft）并更新本地安装；⑥公开资料核证该崩溃类为已知未修互操作坑（CsWinRT #2532 open 无答复）并评估 WinAppSDK 1.6→2.x 升级；⑦**升级落地到 2.5.1**（BuildTools 下限/XamlCompiler 子包路径/屏幕外三条边界与 raw 注入例外/跨版像素 A/B 判零视觉回归），出 v1.1.0 并更新本地安装、打 tag 推 CI（run#23）；⑧用户报「放大中心是固定点不是鼠标」→ 量化反解不动点 + 应用侧埋点定位根因（WinUI Image 在 Stretch=Uniform 下元素盒=内容盒且居中于槽位，代码按「元素盒=槽位」估变换原点偏 132 DIP）→ 按槽位中心实算修复并复测（偏差 190px→≤15px 噪声），顺带把 build.ps1 前置还原改串行定向根治 WMC1007，出 v1.1.1 + 更新本地安装。用户明确要求：UI 验证一律屏幕外、不抢前台；回复用中文。待办：v1.1.1 是否打 tag（本地包与安装已就绪）、Defender 排除、旧 draft 清理（用户说不用清）、2.x 组件裁剪（可选）、viewer.old-py39 / viewer.old-1.0.6 / viewer.old-1.1.0 备份确认后可删。
---

user:
帮我把 release 挪到 E:\App\Others\viewer 目录，替换旧应用

assistant:
判定 = 部署任务（非代码变更）。核实：`release\publish` 就是当前 v1.0.4 构建（与 `SimpleViewer-v1.0.4-win-x64.zip` 同为 2026-09-25 00:41），无 viewer.exe 在运行；目标目录是 2025-02-22 的 Python 3.9/PyInstaller 应用（`viewer.exe` + `_internal`，211 文件/110.1MB）。执行：①旧目录整体改名 `E:\App\Others\viewer.old-py39`（不硬删，留回滚）；②robocopy 铺 publish 全量进新建 `E:\App\Others\viewer`（244 文件/109.84MB，0 失败）；③校验 244 文件相对路径+大小全等、6 个关键文件 SHA256 与源一致、无多余文件；④实机冒烟：窗口「Simple Viewer」正常出窗/响应/干净退出，`startup.log` 本轮零报错；⑤确认 WinAppSDK 1.6 运行时在位（框架依赖构建所需）。告知要点：路径与 exe 名未变（原快捷方式继续可用）、用户数据在 `%LocalAppData%\SimpleViewer`（随应用不随安装路径）、`release\` 与 zip 原样保留、git 工作区干净。备份目录待用户确认后可删。

user:
几个需要优化的地方 1. 留左上一个返回图库旧够用了，不需要右上那个 2. 图片详情页，当前100%加载时有问题的，一般来说应该用宽度或者高度最大一个均在视图内，但现在并不是这个逻辑

assistant:
按 agile-dev 流程（迭代「图片打标签与瀑布流浏览」，分支 feature/detail-view-fit-and-return-entry，两个敏捷项）。Step 2 派两个只读探索子代理并行：①返回入口全景 → 右上那个 = 工具栏右簇 `MainWindow.xaml:167-170` 的「返回图库」按钮（与左上横条按钮同命令；其后紧跟一条「返回图库|翻页」分界竖线必须同删），无 CommandBar/KeyTip/快捷键耦合，影响面主要是两个走查脚本（toolbar-catalog-verify 用精确名当单图态探针 + single-strip-verify 诊断循环）；②适配链 → 静态读码结论是「无 fit 代码、按位图自然尺寸显示」。

**关键反转（本轮最大教训）**：我按静态结论去实机量测基线时，先用 PrintWindow 截屏 + UIA 得到自相矛盾的数字（两个不同夹具红色面积完全相同），改用非 DPI 感知的探针又得出「Image 盒比窗口还大」。修正为 `SetProcessDPIAware` 后真相反转——**单图早就是 contain 适配**（WinUI Image + Stretch=Uniform + Center：元素盒=contain 盒；四夹具 UIA 盒与整窗 contain 期望吻合 0.2%，含小图放大），用户看到的「照片被切一块」是**遮挡**：画布铺满整窗，左栏 280/36、右栏信息面板 280/36、顶部工具栏+横条全压在画布上，实测图片约 40% 宽度落在 chrome 之下（小图水平可见仅 60.6%）。另查 v1 分支的 Python 旧版 `fit_image_to_window`：`scale = min(窗宽/图宽, 窗高/图高)`（含放大，加载与 resize 都跑）——用户基线口径即 contain。定案：适配盒由「整窗画布」改为「未被 chrome 遮挡的可见区」。

Step 3 派实现子代理（inline，一次通过）：①`MainWindow.xaml` 删按钮+前置竖线（三段语义改二段）+ 三处注释同步 + 两个走查脚本同步；②`Views\SingleImageView.xaml` 的 `ViewerImage` 对齐 Center→Stretch（Stretch=Uniform 保留），code-behind 新增右栏宽常量 280/36 + `ApplyChromeInsets` 追加 `ViewerImage.Margin=(左栏宽, 横条底边, 右栏宽, 0)` + 属性白名单加 `IsInfoPanelCollapsed` + 订阅 `TopStatusStrip.SizeChanged`（XAML 回调铁律：轻量赋值 + try/catch 落日志）+ `ZoomAt` 锚点中心改按元素盒实算（含 ImageHost.Padding 项，补偿不对称内缩）；解码尺寸链与画布几何零改动。4 提交：25012bd / cba27b3 / 536aa43（fit-verify.ps1 入库）/ 5bf6187（四份留痕）。

Step 4 主代理独立复核：`dotnet test` 自跑 120/120 绿；`build.ps1` 成功（第 2 次尝试，首次失败属 RULE 已知间歇）；`fit-verify.ps1` 自跑数字与实现子代理逐位一致——修复后 band 尺寸 == UIA 盒尺寸（整张可见）、band 宽高比 == 源宽高比（1.332/3.989/0.200/1.332 vs 1.333/4.0/0.2/1.333）、small 714x536@(483,346) 与 spec 几何推导互证；截图字符画确认红块四边均让开 chrome；单图态 UIA 无精确名「返回图库」、横条「◀ 返回图库」在；`single-strip-verify.ps1` 实机全绿（NO-RED-SEAM / 六按钮 / SETTINGS-RESTORED）。RULE 追加两条：适配盒=可见区口径（含"只能实机量"教训）、几何探针必须 DPI 感知。

收尾：本轮改动以 FF 合并入 master；`E:\App\Others\viewer` 仍停 v1.0.4（未出 v1.0.5 包、未重新部署——版本/tag/CI 属发布决策，等用户拍板）。待用户实机确认两处观感：小图会被放大到可见区（含放大，与旧 Python 版一致）、收展左右栏图片会跟随重适配；已知限制：旋转后不重适配。

user:
没什么问题，感觉可以发布1.0.5，并更新本地、

assistant:
按 RULE:13 出 v1.0.5 并更新本地安装。①CHANGELOG 开 `## [1.0.5] - 2026-09-25` 节（用户可感知口径：可见区适配 / 返回入口唯一化 / 旋转不重适配的已知限制），提交 933f025；②`scripts\release.ps1 -Version 1.0.5` **一次成功**（无 XamlCompiler 重试）→ `release\publish`（244 文件，脚本先清空重建；v1.0.4 的 publish 因此被覆盖，旧版回滚靠 `release\SimpleViewer-v1.0.4-win-x64.zip`）+ `release\SimpleViewer-v1.0.5-win-x64.zip`（45,057,966 B）；③发布三步：annotated tag `v1.0.5` + `git push origin master`（8804295..2836729，6 提交）+ `git push origin v1.0.5` → CI release.yml 触发，run 36158329243 **success**（约 2 分钟），GitHub Draft Release 已生成；④本地安装：先 robocopy /MIR **预演**（只需替换 9 文件/1.14MB、无多余文件待删——目标里没有用户自加文件，故 /MIR 安全），再实铺，244/244 文件字节一致 + 六个关键文件 SHA256 与源相同；⑤对**部署后的正式构建**实测（不是只看 Debug）：`fit-verify.ps1` 四夹具数字与 Debug 逐位一致（small 714x536@(483,346)、band==盒尺寸、宽高比与源一致），单图态 UIA 只有「◀ 返回图库」、无工具栏重复按钮；⑥顺手修验证资产传参坑：cmd 下 `-Fixtures a,b,c` 被当单字符串会落到 Bitmap 构造器抛「stream 无效」误导性异常，现给可读报错（提交 2836729，已含在 tag 内）。

**新发现（待用户处置）**：GitHub 侧 `/releases` 公开列表为**空数组** = 历史所有 CI draft（v1.0.0~v1.0.4）从未点 Publish。v1.0.5 的 draft 已生成待点 Publish；按 RULE:13 建议顺手清理旧 draft。发布后状态：master=2836729 干净、tag v1.0.5 已推、本地安装=v1.0.5、`viewer.old-py39` 备份仍在（确认后可删）。

user:
软件又崩溃了，帮忙看看，这次是长时间使用，然后一次添加标签过程直接崩溃了

assistant:
按 agile-dev 开 bug 轮（敏捷名称 tag-op-finalizer-crash，分支 fix/tag-op-finalizer-crash）。**转储取证（决定性）**：崩溃 2026-09-26 16:00:19（会话已跑 15h40m，v1.0.5 部署版），事件日志 `0xc0000409`（BEX64）+ `ucrtbase+0xa527e`，应用日志零条目；`dotnet-dump` 看 `threads`/`clrstack -all` → **终结器线程栈 `WinRT.IObjectReference.Finalize()` 的 IP 与事件日志「错误偏移」逐位相同（ucrtbase+0xa527e）**，且 `eeheap -gc` 报 `Number of GC Heaps: 0`（托管堆不可遍历）= 崩溃点就是**终结器跨线程 Release XAML 对象**、native 堆已损坏（RULE:26 同族；09-24 三次同指纹）。UI 线程当时在打标收尾（`ShowTagOperationResult → InfoBar 绑定`）只是"当时在做什么"。

探索（只读子代理审计）纠正了我最初的假设：**打标链路并不重载缩略图**（`UpdateFrom` 不碰 Thumbnail、VM 复用），真正的批量裸丢源是——①`Views\TagSidebarConverters.cs` **30 处 `new SolidColorBrush`**（每次求值都新建），而侧栏/筛选条在**每个打标收尾**都全量重建（`RefreshTagDataAsync → RebuildTagSidebar`，另有扫描 5s 节流/组折叠/主题/配置保存），09-24 只缓存了瀑布流模板、漏了同族侧栏；②拖拽打标每次 `DragOver` 再 new 两个画刷；③图像源侧四类残留缺口（`ResetFrom` 整体丢 VM 无显式释放、`TryEnqueue` 与 ElementClearing 竞态、失败/取消路径未提交的 newSource、`EnsureFullResolutionAsync` 先退役后提交的顺序隐患）；④`Drain` 的计数窗口在批量退役时等于没余量。诊断工具限制记一笔：此类 dump 的 `dumpheap -stat`/`finalizequeue` 一定取不到对象统计（堆遍历本身失败），只能走静态审计。

修复（实现子代理，3 提交 a36e426/315ef72/3e66d6e）：新增 `Views\TagBrushCache.cs`（键=最终 ARGB，主题天然正确、UI 线程专用、创建/调用计数）、30 处画刷全部改走缓存 + `SetDropOverlay` 三支同改；`RebuildTagSidebar` 收尾埋点 `sidebar:rebuild brushNew/brushSinceLast/callsSinceLast/cache`（超阈值另告警）；缩略图视图代数守卫 + `ResetFrom` 显式 `ReleaseVisuals`；未提交源/取消路径收口 + 换源顺序统一；`Drain` 改双缓冲边界语义 + HardCap 512。**验证**：120/120 单测、build 通过（首次连败 3 次 MSB3073 属 Defender 抽风——手跑 XamlCompiler 退出 0 即证 XAML 无辜，预热后一次过，判别式已入 RULE）；修复后重建 `brushNew=0`（修复前每轮 39~153 个）；侧栏/筛选条深/浅主题三态逐像素 diff=0（378000 px）、语义锚点全 SAME；打标链路（单图加/移除、批量互斥组、筛选、组折叠）实机通过；日志零新增异常。RULE 补三条（画刷缓存铁律/退役覆盖扩展 + MSB3073 手跑判别式 + 终结器指纹读法）。

**用户反馈（重要流程教训）**：验证期间多次把应用窗口弹到前台，用户被打扰并质询「就不能无头测试吗」。约定：UI 验证窗口一律放屏幕外、不抢焦点（PrintWindow 对非前台窗口有效）、尽量合并成一次、必要时攒到用户不在时跑；build/单测本就是无头。本轮窗口验证已全部跑完（含 settings.json 还原）。修复仍在分支未合并/未发布（等用户拍板是否出 v1.0.6 + 更新 E:\App\Others\viewer）；拖拽打标无法自动化注入，留用户实机复测。

user:
这种bug代码中还有吗？你是不是应该再扫一遍？而且c#的gc怎么这么烂？winui3框架崩溃好几次了

assistant:
按用户要求做**三份只读全仓扫描**（Views 层 / ViewModels 层 / Services+Helpers+Models 层）。**最关键新发现**：`BitmapImage`/`BitmapSource`/`ImageSource` **不实现 IClosable/Dispose**（本投影只有 `SoftwareBitmapSource` 有）→ `ImageSourceRetirement.Release` 的 `(source as IDisposable)?.Dispose()` 对**瀑布流每张卡片缩略图**恒为 null：上一轮所谓「成批缩略图退休」根本没被释放，只是把终结时刻推后，最终仍由 GC 终结器线程 native Release（正是本次崩溃机制）——即该崩溃类**并未真正根治**。其余残留：`GalleryItemViewModel` 未提交 bitmap 在取消/异常路径漏网；退役链 `Release`/`Drain` 无异常隔离（单点抛出→整批中断→剩余全裸丢）；`Release` 无条件计 released（统计假绿）；`ResetFrom` 的「一次 Drain 边界=换帧」名不副实（1000 项重置同帧 Dispose 991 个）；`SingleImageView` 每次改名 `new Run`、筛选 Flyout 每次 `new Style`+4 Setter、`TagFilterPanelControl` 每次点击整树重建 40~300 个对象；`_dragVisual` 属 agile 豁免。另**实证推翻**一条：本投影 `BitmapDecoder`/`BitmapEncoder` 不实现 IClosable（`using` 直接 CS1674 编译失败），P1-7 不可实施。

修复（分支 fix/xaml-finalizer-residuals，4 提交 feefa0e/94fffaa/23eac32/a5190b8）：新增 `Views\ThumbnailImagePool.cs`（UI 线程 BitmapImage 有界对象池，还池前 UriSource=null 释放纹理、DragUI 实例 ExcludeFromPool 一次性排除、容量 64、计数齐全）——缩略图**永不新建、实例终身强引用、终结器永不运行**；未提交源 finally 还池；退役链 try/catch 隔离 + Released/Undisposable 分计 + 首次遇不可 Dispose 源落日志；`ReleaseVisuals(deferDrain)` 批量单次 Drain；`InfoFileNameText.Text` 直写取代 new Run；筛选 Flyout Style 按宽缓存；新增 `Views\UiKeepAlive.cs` 有界保命环接滤波面板旧树；新增 `retire:`/`pool:` 日志埋点。

**主代理屏幕外实机复核（新约定首次执行）**：新建 `scripts\offscreen-soak-check.ps1`——窗口定位到主屏右侧之外 (4560,200) 且带 SWP_NOACTIVATE、绝不置前台；`PrintWindow(PW_RENDERFULLCONTENT)` 屏外截图照常可用；UIA `Invoke`/`ScrollPattern` 无需前台。结果：画布非背景比 0.462（缩略图渲染正常）、滚动与侧栏筛选点击均生效、`pool: acquired=11 returned=11 created=11 pooled=11`（池稳态不增长）、`retire: retired=0 released=0 undisposable=0 pending=0`、进程存活、事件日志无新崩溃、settings 已还原。无头部分：120/120 单测 + build（第 3 次尝试，XamlCompiler 的 Defender 抽风，预热判别式见 RULE）。RULE 新增两条（BitmapImage 无 Dispose→池化/缓存为正解 + 退役链异常隔离与分计；UI 验证一律屏幕外不抢焦点）。待用户拍板：是否加 Defender 排除项根治 XamlCompiler 抽风；是否发布 v1.0.6 并更新本地安装。**回答用户的「GC 为什么这么烂」**：不是 GC 内存管理差，而是互操作边界——WinUI XAML 对象线程亲和（须在创建线程析构），而 .NET 终结器刻意跑在独立线程；CsWinRT 依赖终结器释放 native 引用，于是任何「未显式处置」的 XAML 对象都是隐患（官方 metadata 甚至标注 BitmapImage 为 Agile，与实机崩溃矛盾 = 框架侧契约不一致）；本应用的多次崩溃里，stowed 0xc000027b 是**我们自己的** XAML 回调异常逃逸被框架静默处决（DX 问题），fastfail 是终结器/互操作 + 我们对象 churn 放大，MSB3073 是工具链与 AV 竞态。

user:
你搜索下网络，这种东西应该都是常见问题

assistant:
公开资料核证（GitHub/StackExchange API 直查 + MS Learn）：①**官方文档证实 `BitmapImage` 无 `Close`/`Dispose`**（方法表仅 Play/Stop/SetSource/SetSourceAsync）——「退役队列对它是空操作」由推断升级为权威结论；②同族问题**至今未修**：CsWinRT #2532（2026-08 open、**无维护者答复**）终结器线程把非 agile 对象 Release 派发进反初始化的 apartment → XAML `CDependencyObject::GetContext` 访问违例，报告者问的三问（能否跳过封送 / AppContext 开关 / 是否以「属主线程确定性释放」为官方做法）至今无人答；WindowsAppSDK #6298（open）DisplayInformation 非 UI 线程终结崩溃；microsoft-ui-xaml #10948「`StartDragAsync` 后 GC 必崩」（**我们拖拽打标正走这条 API**），报告者 1.8 未复现，维护者以「1.7 之后停服」关单、无规避建议 → **升级 WinAppSDK 是可选线索**（我们在 1.6）；③社区修法与我们一致：sql-bi/SQLBI-Whiteboard 连开三 PR 修 Graphics Capture 同类崩溃，规矩 = 在创建 dispatcher 线程释放、不给 Dispose 的包装器保命持有到进程结束、`GC.SuppressFinalize` 后留 COM 引用；desktop-lyric 亦记录「标 Agile 却在非 STA 线程 GC 就 AV」的同一矛盾。结论：这是已知未修的互操作坑族，我们的池化/退役/隔离是社区正解而非野路子；SO 上该主题几乎无内容。

user:
可以，先发布吧

assistant:
出 v1.0.6 并更新本地安装：CHANGELOG 新节（用户可感知口径：长会话闪退根治=终结器跨线程释放；画刷静态缓存 / 缩略图位图对象池 / 退役链异常隔离与 Released-Undisposable 分计 / Run·Style 复用 / 视图代数守卫；诊断：终结器指纹读法 + 屏幕外验证；已知限制：模板内联对象与旋转不重适配）→ 提交 55e776f；`scripts\release.ps1 -Version 1.0.6` **一次成功** → `release\SimpleViewer-v1.0.6-win-x64.zip`（43MB）+ `release\publish` 重建；部署：robocopy /MIR **预演**（换 17 文件/1.16MB、无多余项、目标无用户自加文件）→ 实铺 → 244/244 字节一致 + 六个关键文件 SHA256 相同；annotated tag `v1.0.6` + `git push origin master`（9e174df..55e776f）+ `git push origin v1.0.6` → CI release.yml 触发。**对部署后的正式构建做屏幕外复核**（`scripts\offscreen-soak-check.ps1`，窗口 (4560,200) 未置前台）：画布非背景 0.462（缩略图渲染正常）、UIA 滚动与侧栏筛选点击均生效、`pool: acquired=11 returned=11 created=11 pooled=11`（池稳态）、`retire: undisposable=0`、进程存活、事件日志无新崩溃、settings.json 已还原。待用户决定：①NuGet 缓存加 Defender 排除（根治构建抽风）；②是否评估升级 WinAppSDK 1.6→1.8/2.0（#10948 线索，需单独一轮评估与回归）；③GitHub v1.0.6 draft 点 Publish、历史 v1.0.0~v1.0.5 draft 清理；④`E:\App\Others\viewer.old-py39` 备份确认后可删。

user:
（升级）Windows App SDK 的优点是什么？

assistant:
查官方发布通道页 + GitHub 发布记录后，按「对本应用值不值」回答。**最硬的理由是支持状态**：1.6（我们在用）自 **2025-09-04** 起 Out of Support；1.7 于 2026-03-18 停服；1.8 的最后一版服务更新 `1.8.260921001` 发布于 2026-09-24（也已落幕）——**唯一在维护的是 2.x 线**（2.0 起，当前稳定 **2.5.1** / 2026-09-16，支持到 2027-04-29）。不受支持 = 框架侧以后修的崩溃我们永远拿不到、提 issue 也不受理。能具体拿到的：①`StartDragAsync` 之后 GC 回收必崩（microsoft-ui-xaml #10948，**我们拖拽打标正走这个 API**）在 1.8 不再复现；②2.4/2.5 发布说明列出 WinUI 3 / 输入 / 合成 / 部署的多项修复（Storage Picker 焦点恢复、MRT Core 回退路径、运行时目录隔离等）；③新版 CsWinRT 向 GC 上报 native 内存压力 → 该回收的对象更早、更分散地被终结（削弱「成批压到终结器」这一放大因素）；④顺带能力（触控板手势等）+ 待验证项（1.6 的 `WindowsAppSDKSelfContained` XAML 资源解析缺陷若已修，发布布局可简化）。**不会解决**：根因 CsWinRT #2532 仍 open 且无维护者答复 → 画刷缓存/缩略图池/显式退役/保命环这套纪律升级后仍必须保持。代价：2.0 是大版本（迁移面需对照官方说明逐条核；本轮未能抓到完整 breaking changes 列表）、连带要改 csproj / `release.ps1` / CI 的假设、目标机要装 2.x 运行时（现装 1.6）。建议顺序：先让 v1.0.6 跑几天（看 `startup.log` 的 `pool.created` 是否稳定、有无再崩）→ 顺手加 Defender 排除 → 再单独一轮「1.6→2.x 升级评估」（分支试升级 + 全量回归 + 屏外复核）→ 由用户决定是否发。已把「1.6 已停止支持 / 唯一受支持线是 2.x / 升级属待评估」记入 RULE 构建节。

user:
（看得出上下文被压缩了吧）我还是建议直接使用最新的框架，当初使用旧框架就是不对的。直接升级吧。（中途补充：说中文哈，不要英文回复；没有其他事情了，你继续干吧）

assistant:
按「独立一轮」流程把 1.6→**2.5.1** 升完（分支 `feature/winappsdk-2x-upgrade`）。**包级事实（全部实证，非文档转述）**：①`net8.0-windows10.0.19041.0` 与 `TargetPlatformMinVersion 10.0.17763.0` 都不必动（WinUI 托管资产是 net6.0-windows10.0.17763.0）；②**`Microsoft.Windows.SDK.BuildTools` 必须 ≥ 10.0.26100.4654**（2.5.1 的 Base 包 buildTransitive 硬校验，低版直接 NU1605 还原失败——实测踩到）；③2.x 已元包化为 10 个子包（Base/Foundation/InteractiveExperiences/WinUI/DWrite/Widgets/AI/ML/Search/Runtime[2.5.1]），**XamlCompiler.exe 从元包移到 `microsoft.windowsappsdk.winui\<ver>\tools\net472\`** → `release.ps1` 的预热 glob 改为扫两族按版本目录取最新（否则预热到旧 1.6 编译器）；④`WindowsAppSDKSelfContained` 对非库工程默认变 true（本项目显式 false 不动）；⑤**运行期兼容策略**：框架依赖应用默认吃「最新 servicing 级别」、bootstrapper 是 [0,max) 区间匹配 → 目标机装了更高 2.x 补丁就会跑那个补丁，「环境记录」必须写运行时具体版本；⑥无官方 1.x→2.0 迁移文档、无破坏性 API 变更、2.5.1 无 known issues（图像解码/拖拽/DispatcherQueue 均无回归条目）；⑦本机已装 WindowsAppRuntime 2.5.1（**PFN 主版本号已从 1 变 2**，别再写死 `Microsoft.WindowsAppRuntime.1_`）；⑧`TitleBar.Content` 拖拽区变更是 2.1.3 默认，本项目不用 TitleBar API，不适用。

**回归全绿（全部屏幕外或可无头）**：build 首试成功、0 新增警告（只剩既有 WMC1506）；`dotnet test` **120/120**；图库屏外 soak（缩略图非背景 0.462、滚动/筛选点击生效、无新崩溃、`brushNew=0`、pool 11/11）；`page-loop` **72/72 零 missed + NO-LOAD-FAILURE**（RO_E_CLOSED 修复在 2.5.1 继续成立）；`tag-op churn` 三轮重建 `brushNew=0`；`tag-op chain` 加标签→右栏 chip **像素差 4462（与 1.6 基线 4462 逐位一致）**→ ✕ 移除→还原、返回图库/筛选/组折叠全过；`fit-verify` 4 夹具 `band==box` 且比例守恒；`walkthrough2` CARDS=2、连点两卡保持「已选 1 张」、chip 在标题区；`zoom-path` 3/3 页红图可见；`cold-start` **0/3 轮**卡死。

**屏外验证暴露的口径坑（已修脚本并写入 RULE「屏幕外三条边界」）**：①`CopyFromScreen` 抓不到屏幕外窗口 → 改 `PrintWindow` 并把窗口内容贴到画布 (40,40) 以保持既有采样字面量口径；②UIA `BoundingRectangle` 是屏幕绝对坐标，`walkthrough2` 用 X∈[500,1100] 圈卡片在屏外恒不命中（假 CARDS-FOUND: 0）→ 改窗口内相对；③采样点写死 (600,400) 在「按可见区适配」后落到图外（假 red=False）→ 改取 UIA Image 元素中心；④**raw 注入与屏幕外互斥**（`SetCursorPos` 被限制在可见桌面内、键盘只进前台）→ `tag-op-finalizer-verify.ps1` 保持屏上 (40,40) 作**唯一例外**（全程 Deactivate-Window 不抢焦点），新检查优先 UIA Invoke；⑤`cold-start-verify.ps1` 原先屏上静置 60s×3（违反约束）→ 启动即移屏外，且补中文注释后按铁律**补 UTF-8 BOM**。另做了一次「同进程屏外 vs 屏上」对照：`redBBox` 逐位相同 → `PrintWindow` 结果与窗口是否在屏内无关。

**出包与部署**：`release.ps1 -Version 1.1.0` 一次成功 → `SimpleViewer-v1.1.0-win-x64.zip` **59.7MB**（1.0.6 是 43MB；2.x 元包未裁剪，多带 AI/ML/Search/Widgets 投影，后续可单独裁剪、不影响运行期）；对 **publish 产物**与**本机安装**各跑一轮屏外 soak 均绿；`E:\App\Others\viewer` 已更新（旧版完整备份为 `viewer.old-1.0.6`）。**版本号取舍**：平台级升级用 minor（1.1.0）而非 patch，已写在 CHANGELOG；tag/发布等用户点头再推（不重复上轮「先发布」的授权）。文档：新增 `docs/iterations/图片打标签与瀑布流浏览/maintenance/winappsdk-2x-upgrade/{prd,spec}.md`（spec 含包级证据、验证矩阵、脚本口径修正五条），RULE 构建节整段重写为 2.5.1 现状。

user:
Releases 不用清理就这样吧。打tag吧 1.1.0可以

assistant:
**发布走完**：annotated tag `v1.1.0`（指向 281775f，tag 信息含升级一句话要点）+ `git push origin master` → 4a42e48..281775f、`git push origin v1.1.0` → **CI run#23 成功（1m58s）**；工作流最后一步就是「创建 Draft Release 并上传 zip」，该步绿 ⇒ v1.1.0 draft 已生成（draft 未登录看不到，公开 Releases 页仍显示 no releases，符合预期）；旧 draft 按用户指示不动。**两个工具坑**：①`gh` 本机未安装且 GitHub API 对本机 IP 限流（403）→ 改用 WebFetch 读公开 Actions/Releases 页确认状态（run 链接 `https://github.com/bloodycrownD/simple_viewer/actions/runs/36239526716`）；②`git push ... | Select-String`（cmd 里管道接 PowerShell cmdlet）双失败一次、`git commit -F %TEMP%\commit-msg.txt` **误用上次会话遗留的同名临时文件**（标题变成上一轮的 docs 提交）——靠 `git commit --amend -F <新文件>` 修正（未 push，安全）。Actions 提示 Node.js 20 弃用（checkout@v4 / setup-dotnet@v4 / action-gh-release@v2 自带，非本项目代码）→ 记进 release.yml 文件头，下轮升 action 主版本。

user:
e2e测试都过了？没有问题？

assistant:
**答「自动化过的」+ 明确「没盖到的」**，并补两项能机械补掉的缺口：①**真 zip 解压后跑**——`SimpleViewer-v1.1.0-win-x64.zip` 解出 264 文件，直接对解压副本跑屏外 soak 全绿（publish 目录 / 解压副本 / 本机安装三处同源同结果）；②**1.6 vs 2.5.1 跨版像素 A/B**（同夹具同设置、屏外同位置同尺寸、PrintWindow 连拍到「两帧相同」取安定帧，并**带同二进制复跑作方法对照**）：Dark 全窗差 **23** 像素/单通道 maxΔ5、Light **46**/1,440,000/maxΔ3，逐像素定位全落在字形抗锯齿边缘；对照（同二进制两次运行）自身就有 30 像素噪声（工作区因缩略图落位可差 19616）⇒ 判「无可感知 UI 变化」，工具栏横条两主题逐位相同。**没被自动化盖到的（明确告知）**：拖拽打标（本机移动注入被丢弃，只能用户手拖）、长会话（15h40m 那类只能靠日常用 + 看日志计数）、滚轮/触控板缩放手势（注入被拒）、旋转后不重适配（既有已知限制）、删除到回收站与设置页的 UI 路径。**A/B 期间两坑**：探针先把「首帧」当比对帧（假差异 4902 像素，改取安定帧）；A/B 脚本 `"$tag-s.png"` 变量名解析踩雷（改 `'{0}-{1}' -f` 拼接）——教训写进 RULE：**「跑绿了」要先确认真启动了你以为的 exe**（曾因 cmd 里 `$env:TEMP` 未展开导致 soak 空跑却报出漂亮数字）。

user:
放大逻辑不对之前中心点是鼠标，现在是固定点

assistant:
**量化诊断（不靠读码猜）**：①探针——夹具图（3000×2000 横 / 2000×3000 竖）在 35%/35% 处画红方块；`-d` 直开单图 → 窗口 (40,40) 1200×800（raw 注入例外，屏上跑）→ 双击复位 → `SetCursorPos` **再点一下**（本机注入移动被丢弃，点击才能刷新应用侧指针位置）→ 滚轮 2 档 → PrintWindow 前后帧 → 由标记位移与尺寸比反解实际不动点 `A = p_before − Δp/(ratio−1)`。②结果：X 跟随光标、**Y 是与光标无关的常数偏移**（横图 ~190px、竖图 47~68px）= 用户说的「固定点」。③应用侧加 `SIMPLEVIEWER_ZOOM_DIAG=1` 埋点（ZoomAt 落 `[zoomprobe]` 行）拿到：`anchor=(330.0,244.7)`（与注入光标完全一致 ⇒ **输入侧无辜**）、`C=(392.7,153.7)`、`actualTL=(288.0,216.7)`、`box=209.3x139.3`、`margin=(280,76)`、`host=785.3x496`。④**根因**：代码按「元素盒 = 槽位」估变换原点，而 **WinUI 的 `Image` 在 `Stretch=Uniform` 下元素盒 = 位图 contain 后的内容盒、且在槽位内居中**（`ActualWidth/Height` 即内容盒，UIA 报的也是它），留白那一轴整段偏 (槽位404−内容盒139.3)/2 = **132.35 DIP**；由内容反解的真实原点 (391.5,286.9) 正好 = 槽位中心 ✓。**该缺陷自 v1.0.5「适配盒=可见区」引入、v1.0.6/v1.1.0 带着发布**（滚轮缩放一直无自动化覆盖，RULE 早已写「放大类交互留实机」，故此前未发现）。⑤**修复**：`GetViewerImageBoxCenter` 改按**适配槽位中心**实算（槽位中心 ≡ 内容盒中心），`ZoomAt` 公式不变（数学本就正确，喂进去的 C 错了）。⑥**验证**：修后偏差 (1.5,1.0)/(−3.5,−3.0)/(3.3,5.6)/(−11,−14.7)，**残余与修前 X 分量逐位相同 = 标记质心测量噪声**；回归 120/120 + fit-verify 4 组 band==box（数字与修前一致 ⇒ 适配未回退）+ page-loop 72/72 `NO-LOAD-FAILURE` + 屏外 soak 全绿；本机安装升到 v1.1.1（备份 `viewer.old-1.1.0`）后**对安装副本复测锚点同样贴合**。⑦**顺带修上游**：修 bug 期间构建撞 `XamlCompiler WMC1007 Cannot resolve metadata for WinUI types` = RULE 早记录的「双 csproj 还原踩踏」（`dotnet restore <sln> --force` 让主工程视角丢 WinAppSDK 引用）→ `build.ps1` 前置还原与冷重建兜底改**串行定向**（tests → Core → 主工程 `RestoreRecursive=false` + 主工程视角校验），实跑通过；WMC1007 判别式写进 RULE（碰到先跑串行还原，别查 XAML 语法）。文档：CHANGELOG v1.1.1、新增 `docs/iterations/图片打标签与瀑布流浏览/bugs/zoom-anchor-off-cursor/{prd,spec}.md`、RULE 新增「禁止把『元素盒=槽位』当默认前提」与「反解不动点法（含埋点开关说明）」。**待用户点头**：v1.1.1 是否打 tag 发布（本地包与安装已就绪）。
