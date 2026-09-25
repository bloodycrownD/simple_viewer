---
date: 2026-09-25 23:20
title: 部署到 E:\App\Others\viewer + 单图详情页两项优化（返回入口唯一化 / 适配盒改可见区）
keywords: 部署, release, E:\App\Others\viewer, 旧 Python 版替换, 单图详情页, 返回图库, 适配盒, contain, 可见区, 遮盖式布局, fit-verify.ps1, DPI 感知, detail-view-return-entry-simplify, detail-view-fit-visible-area
abstract: 两轮任务。①把 release 产物（v1.0.4 publish，244 文件/109.8MB）铺到 E:\App\Others\viewer 替换 2025-02-22 的 Python 3.9/PyInstaller 旧应用（旧目录改名 viewer.old-py39 留回滚，未硬删），逐文件 hash 校验 + 实机启动冒烟通过。②用户报两项详情页优化，按 agile-dev 交付：左上一个「◀ 返回图库」保留、删工具栏右簇重复按钮（同删其前置竖线）+ 适配盒由「整窗」改「未被 chrome 遮挡的可见区」（关键反转：实测现状已是整窗 contain，"被切四成宽"是遮挡而非无适配；静态读码结论曾被 DPI 虚拟化的探针量测误导）。120/120 单测 + 实机四夹具量测前后对照 + 走查脚本同步。待用户实机确认；出包/deploy 更新未做（属发布决策）。
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
