---
date: 2026-09-19 19:05
title: 打标管线修复、交互重构与八轮走查打磨（pipeline-fixes + interaction-rework + overlay/selection 打磨）
keywords: 打标性能, 缓存键, 同图改名, 标签预算, 255字节, 点击筛选, 拖拽打标, 详情页右栏, 未分组保留, 显示名统一, 弹窗主题, 工具栏左对齐, 收展闪空, 拖拽视觉, 遮盖式布局, 画布层, DragUI, GIF换源空窗, AXPress假阳性, 单选重置, 成功回执静默, 放大置底
abstract: 用户连续敏捷：①打标不重载；②255 字节标签预算；③交互重构（点击=筛选、拖拽打标、详情页右栏）；随后八轮走查与打磨：未分组保留、显示名统一、弹窗主题、工具栏左对齐、收展闪空、拖拽小缩略图+遮盖式布局（画布全窗底层根治收展闪跳与位移）、右栏被工具栏遮挡（chrome 行高度联动避让，AXPress 假阳性教训入 RULE）、选择模型 Explorer 心智（默认单选/Ctrl 加减选/Shift 范围重置+清除选择按钮）+打标成功回执静默（仅失败弹）+放大置底（拆置顶链）。分支 feature/tagging-waterfall，71/71 绿。待用户实机：Ctrl/Shift 选择、滚轮放大置底、拖拽手感（修饰键/拖拽/滚轮注入不可达）。
---

user:
几个问题：1. 需要根据文件名计算标签最大数量，windows/linux 限制不同（我理解 windows 255 字符，linux/mac 不同规则？）；2. 打标签时图片总是刷新，性能差——改文件名没必要重新加载图片，图片本身不变；3. 瀑布流文件名不发生变化；4. 打标交互不好用，改成：a 点击左侧标签=筛选出该标签所有图片（多选 OR）；b 拖拽图片到标签上打标（多选=整集）；c 图片详情页右侧新增一栏放图片信息与标签 chips（✕ 移除、＋ 弹标签目录选择添加）。

assistant:
先答规则：Windows/NTFS 文件名 255 个 UTF-16 字符、全路径默认 260；Linux（ext4 等）文件名 255 **字节**（UTF-8 汉字 3 字素，预算缩到 1/3）；macOS/APFS 约 255 字符。跨平台安全按最严的 Linux 字节数算。两轮并行探索结论：①根因=打标成功后 ToggleTagOnCurrentImageAsync 调 LoadCurrentAsync（MainViewModel.cs:1010-1014）——先置空 ImageSource 再按新路径重解码（ImageLoaderService 缓存键含路径:36,235 必 miss），缩放态也被当切图复位（SingleImageView.xaml.cs:81-91）；缩略图缓存同病（ThumbnailService.cs:85,279+，磁盘键 SHA1(路径)）。②DisplayName 剥离标签段是 D15 拍板设计（打标不改显示名与行序），真缺陷=角标 hue 静态索引只在侧栏 Rebuild 更新（GalleryItemViewModel.cs:70-91），打标后角标底色滞后；另有幂等口径不一致（TagService Ordinal:138 vs 同步侧 OrdinalIgnoreCase:1642）。③唯一预检是 BuildNewPath 的 260 字符（TagFilenameService.cs:11,129-133）。④点击分流在 MainViewModel.cs:805-840（打标优先于筛选——用户嫌弃的点）；卡片是 Border 可挂 CanDrag；树行 Button 可挂 AllowDrop；项目零拖拽/Flyout 先例；单图视图两行布局无右栏，解码尺寸源已是 ImageHost 显示区。

拍板决策：点击标签一律=筛选且自动切回图库（单图模式点击有可见反馈）；Shift+点击移除入口取消（移除走详情页 ✕，RemoveTagFromSelectionAsync 删除）；拖选中集内任一卡=整集打标、拖未选中卡=单卡；快捷键打标保留不动；标签目录选择器走 ContentDialog 惯例（无 Flyout 先例）；demo 同步（交互基准）。执行顺序：先 tagging-pipeline-fixes（bug+预算，"同图改名"路径是右栏打标的前置），后 tag-interaction-rework。

user:
（两轮实现由子代理完成，主代理实机走查）

assistant:
实现提交：fixes=791810e（同图改名不重载+缓存迁移）/1255db7（角标hue+幂等口径）/879ea6c（255字节预算+8单测，71/71 绿）；rework=1dd249f（点击=筛选+自动回图库）/b591b2e（拖拽管线+DropOverlay）/7ae4b7f（右栏+TagCatalogDialog）/60bdb7c（demo）。走查抓到并修复两个真 bug（6cf7e76）：①右栏＋按钮 CanExecute=HasImage 但漏加 OnHasImageChanged 刷新点→永久禁用；②打标改名竞态：InfoBar 弹出→布局抖动→ImageHost.SizeChanged→立即重载读旧路径→撞 File.Move→FileNotFound 清视图（图片消失/空态/按钮禁用，状态行随后被覆盖掩盖）——诊断日志实锤后双保险修复：打标中忽略视口尺寸变化+结束后补判定、LoadCurrentAsync 失败按"改名在途"重试（80ms 后重读路径）。复测两轮加/删标签全绿。
实机通过项：图库筛选开/关（命中统计/筛选条）、单图点标签自动回图库+筛选、目录选择器添加、chip ✕ 移除、组头折叠、双击进单图、卡片点选、侧栏计数与未分组虚拟组。**拖拽未能自动化验证**：本机 SendInput 鼠标移动事件被丢弃（ret=1 但光标不动，点击可达）——拖拽手势无法注入，代码链路已静态核对（格式串/事件/兜底齐全），真实手感留待用户实机；drag-tag-test.ps1 留作环境复测。
环境经验入 RULE：双 csproj 同目录 restore 踩踏（dotnet msbuild SimpleViewer.csproj -t:Restore -p:Platform=x64 解法）；SendInput 移动被丢；UIA 坐标=屏幕点勿乘缩放。留痕：features/tagging-pipeline-fixes 与 tag-interaction-rework 的 prd/spec。待用户确认。

user:
你测试了吗？怎么我一用就是 bug？1. 删除标签后，左侧的标签列表也一同删除了；2. 图片详情显示完整名字，列表页显示剥离标签的名字，不统一——要么都显示名字要么都显示完整名。

assistant:
两条都实锤修复（4679a12，71/71 绿）：①根因=未分组标签行仅由索引计数快照生成，计数归零（详情页 ✕ 移除最后一处引用）即从侧栏消失、无法再筛选或重新打标——修为「曾见即留」：TagSidebarViewModel 会话内记忆已见未分组标签，计数 0 仍保留行；显式删除/重命名（✎ 菜单）经 ForgetUngroupedTag 摘除避免残留；不落盘（未分组事实源是文件名，重启无引用即不存在）。注意：配置组标签行本就保留 0 计数，用户遇到的必是未分组路径。②口径统一为「剥离标签段显示名」：单图状态行/底部文件名栏/右栏信息区/瀑布流卡片全部显示 base 名，标签信息由右栏 chips+卡片角标承载，完整文件名挂 tooltip（WinUI TextBlock 无 ToolTip 属性须用 ToolTipService.SetToolTip，踩了一脚编译错）；旧 PRD"单图显示完整名+高亮标签段"口径由此推翻。demo renderViewer 同步。实机走查：photo_2[风景].jpg 三处显示名均 photo_2.jpg ✓；详情 ✕ 移除风景后未分组行保留计数 0 ✓、图片不消失（竞态修复稳定）。教训：交互重构后未覆盖"标签全生命周期只剩 0 引用"与"显示口径一致性"两类端到端场景，走查清单应加这两项。

user:
优化弹窗主题：弹窗与深色主题不符；且弹窗中的标签库应与左侧栏标签库用相同 UI 样式（附截图：目录弹窗白底）。

assistant:
两处修复（7b0791e，71/71 绿）：①弹窗白底根因 = ContentDialog 宿主在 popup 层、不在 RootGrid 视觉树内，ThemeResource 与底色按应用/系统主题解析、不认 RootGrid.RequestedTheme 运行时覆盖（与侧栏画刷问题同族的另一半）——MainWindow 新增 ApplyDialogTheme(dialog)（dialog.RequestedTheme = RootGrid.RequestedTheme），四处 ContentDialog（设置/标签编辑/标签目录/删除确认）统一调用。②目录弹窗行样式对齐侧栏：组头行复刻侧栏组头（静态 ▾ chevron——目录内组恒展开状态真实、组名 SemiBold、互斥/兼容徽章、右对齐计数、36 高）、标签行复用 TagTreeRowButtonStyle + 缩进连接线 + 互斥组单选圆点（组色相描边、已含实心）+ ✓已有 + 右对齐计数；计数经 GetTagCatalogSnapshot 第三返回值与侧栏同源（_latestTagCounts），目录与侧栏计数永不分裂。实机验证：深色弹窗文字可读、像素级放大核实 chevron/连接线/圆点均渲染（低对比淡雅，与侧栏同款）；用户 settings 备份还原（保留用户自建组）。

user:
左侧栏顶部是空的，感觉很奇怪。图片放大后层级奇怪，调整到最高遮盖侧栏。收起展开用 icon（"展"字竖排丑）。

assistant:
三项 UI 修正（a737aa1，71/71 绿）：①左栏顶部空块根因 = WinUI CommandBar 主命令默认右对齐、左上留大块空白——工具栏整体重做为左对齐横向文本按钮行（Border+ScrollViewer+StackPanel+Ghost 按钮，分隔线 1px 竖 Rectangle；ThemeButton 从 AppBarButton.Label 改普通 Button.Content"主题：深色"）；②放大置顶：scale>1（容差 1.05）时 SingleImageView 提升 ImageArea ZIndex（盖右栏，同父）+ 写 VM.IsCurrentImageZoomed，MainWindow 订阅后提 ContentAreaGrid（盖左栏）与 MainAreaGrid（盖工具栏/InfoBar/状态栏）ZIndex=100——Grid 默认不裁溢出，双链提升实现"最高层遮盖"；复位/切图/回图库还原（OnCurrentModeChanged 清残留，防图库模式侧栏被盖不可交互）；③收起/展开图标化：左栏标题 ◀/折叠条 ▶、右栏标题 ▶/折叠条 ◀（BMP U+25C0/U+25B6，方向指向展开侧）。实机：滚轮放大遮左右栏→双击复位还原→◀收起折叠条▶→展开，全循环通过。踩坑两记：XamlCompiler 增量漏编 XAML（构建"成功"但跑旧 XBF——遇 UI 改动不生效先删 obj 冷重建）；CommunityToolkit 分部方法 OnCurrentModeChanged 已有实现、重复添加 CS0757（冷重建才暴露，增量构建居然没报）。demo 无折叠交互无需同步。

user:
展开/收起为什么会刷新页面（图片闪一下）？

assistant:
根因：收展侧栏/右栏 → ImageHost 显示区尺寸变化 → 解码尺寸重载（RULE 铁律②的设计行为），而 LoadCurrentAsync 是"先 ReleaseCurrentImageSource 清空再异步解码"——清空到换源之间的窗口就是闪空。修复（579f33e）：区分同图/异图——同图重载（收展、窗口 resize 引起的尺寸变化）保持旧源显示直到新源就绪一次性换上（对齐 EnsureFullResolutionAsync 同图换源先例）；切图（路径变化）仍预清空防旧图残影；同图换源后对被替换旧源清 GIF UriSource 句柄（防文件锁阻碍打标改名——原设计靠加载前统一清，改为换源后精准清）。实机：收起/展开/切图循环全过。这也是"打标竞态"的同族问题（布局抖动→重载闪空），同图不预清空后此类闪空从根上消除。

user:
更新记忆了吗？准备压缩上下文。

assistant:
已完整收口（8a363aa 后追加本次整理提交）：本记忆 6 轮按时间序重排（此前修复"吃 user 头"时插错位置导致后四轮倒序）、front matter 刷新为全貌摘要+当前状态（分支 feature/tagging-waterfall，HEAD 见 git log，71/71 绿）。当前快照：五轮走查修复全部提交（4679a12 未分组保留+显示名统一 / 7b0791e 弹窗主题+目录样式 / a737aa1 工具栏左对齐+放大置顶+图标化 / 579f33e 收展闪空）；待用户实机验证项：拖拽打标手感（环境注入不了拖拽手势）、放大置顶体验、收展顺滑度；工作区干净。教训入 RULE：追加记忆新轮次必须用"文件末尾锚点追加"方式，禁止以旧轮 user: 头为 old_string 替换（本会话连犯三次吃头+一次插错位）。

user:
恢复记忆后三个新问题：1. 拖拽图片到标签时图片仍完整显示，应缩小随鼠标移动；2. 展开/收起第一次仍出现页面刷新现象；3. 展开/收起影响图片位置——建议画布放最底层、侧栏作为画布之上的遮盖（而非组件同级），收展即不影响图片位置。

assistant:
判定为迭代内敏捷项 drag-visual-thumbnail-and-overlay-canvas（①bug + ②③布局级 feature），继续 feature/tagging-waterfall。双探索结论：①拖拽视觉根因=卡片 CanDrag 无 DragUI 定制→系统默认整卡快照；WinAppSDK 1.6 实有 DragUI.SetContentFromBitmapImage/SoftwareBitmap（WaterfallView.xaml.cs:138 旧注释有误需纠正）；DragUI 不缩放位图，bucket 缩略图 360+px 偏大，需缩略图加载时顺带 WIC 降采样 ~120px SoftwareBitmap 备用；多选计数可走目标侧 DragUIOverride.Caption。②首次收展闪根因=收展→decodeSize 变化→首次缓存 miss 走磁盘+WIC 数十至数百 ms，旧源先 Uniform 拉伸重布局、新源就绪换上=跳变感；二次起 LRU 命中一帧完成故只首次闪。GIF 另有每次换源异步空窗（UriSource 异步打开）。③图片位移=ImageHost 尺寸变化必然结果。拍板用户遮盖方案：RootGrid 改两层——底层画布层（整窗，承载 SingleImageView：ImageHost 铺满+右栏/文件名栏改浮层）+ 上层 chrome 层（原四行：工具栏/InfoBar/MainAreaGrid(左栏+图库)/状态栏）；单图模式 chrome 空明区无 Background 不吞指针、画布指针事件正常；放大置顶简化为画布层 ZIndex 0↔100；图库模式占位结构不动；顺手修 GIF 同图换源 ImageOpened 后提交 + 修订 RULE 铁律②文本（解码尺寸=整窗画布）。执行：先拖拽视觉（小）后遮盖改造（大），两个实现子代理串行。

user:
（两个实现子代理先后完成，主代理复核与实机走查）

assistant:
实现+验证+留痕收口（5 提交）：10745d6 拖拽视觉——ImageSourceHelper.TryCreateDragVisualAsync（WIC+Fant 短边 120px SoftwareBitmap，IgnoreExifOrientation 防二次旋转）在 UiApplyGate 闸门段后预生成，OnCardDragStarting 三级回退（小位图 SetContentFromSoftwareBitmap 居中锚点 > Thumbnail BitmapImage > 系统整卡快照），纠正"DragUI 不可定制"错误旧注释；b0e05fd 多选计数——MainViewModel.DragPayloadCount + 标签行 DragOver N>1 时 Caption「打标 N 张」；532c637 遮盖式布局——RootGrid 改 CanvasLayer(Z0 整窗承载 SingleImageView) + ChromeLayer(Z1 原四行)，SingleImageView 单 cell 叠加（ImageHost 铺满、右栏/文件名栏改右/底浮层不透明背景），放大置顶收敛两级单点，Grid 链无 Background 不吞指针；a127599 GIF 同图换源等 ImageOpened（2s 超时/失败/取消放行）再提交；6512655 RULE 铁律②改口径（解码=整窗画布区）。验证：71/71 绿（主代理复核重跑）、build 过（restore 踩踏当日升级必现，按 RULE 定向还原+--no-restore 绕过，RULE 第 8 条已补强）；主代理实机 UIA 走查：单图模式收展左栏/右栏四态 image bounds 恒 [427,323,959,720] 不变（核心验收达成）、FolderPicker 开图库瀑布流 10 卡正常、双击回单图正常。滚轮放大置顶与拖拽视觉留用户实机（computer-use 滚轮注入被前台校验拒绝，与 SendInput 移动丢弃同族限制）。留痕 features/drag-visual-thumbnail-and-overlay-canvas prd+spec。

user:
你测试过吗？右侧栏无法收起，因为被顶部工具栏遮盖了。

assistant:
实锤走查假阳性：我用 UIA AXPress 验证收展（a11y 直调按钮动作、不做视觉命中测试），绕过了遮挡；真实鼠标点到的是工具栏。根因：右栏浮层（含 ▶ 收起按钮/◀ 折叠条）在画布层（Z0）内顶到窗口顶，而 ChromeLayer（Z1）的工具栏行横跨整宽，把右栏标题行/折叠条的可点区盖住（▶ 相对窗口 y=63-99 vs 工具栏到 y=93）；左栏不受影响（本就在 chrome 层内）。修复（b1063b0，主代理直接实现）：MainWindow 给工具栏/InfoBar/状态栏三行命名并挂 SizeChanged → 写 VM.TopChromeHeight（工具栏+InfoBar 实际高+8 margin）/BottomChromeHeight；SingleImageView.ApplyChromeInsets 依此设右栏展开/折叠条顶底 Margin 与文件名栏底 Margin（替代硬编码 34）。验证：build 过、71/71 绿；raw 鼠标路径走查（left_click 元素 target + strategy=event，真实命中测试）右栏 ▶ 收起→◀ 展开双向通过，▶ 从 y=63 移到 y=132（工具栏下方），image bounds 全程恒定。教训入 RULE（实机 UI 测试节）：AXPress 假阳性——可点性/遮挡/ZIndex 验证必须 raw 鼠标路径或 bounds 重叠核对；滚轮注入亦被拒（放大仍留用户实机）。

user:
三个新问题：1. 水印添加生效的 toast 移除，有点碍事；2. 滚轮放大图片层级置底吧，之前选置顶的策略不合适；3. 默认多选不合适——应默认单选、Ctrl+点击多选，且需要清除选择的能力（多选太多逐个取消太累）。

assistant:
敏捷项 selection-and-zoom-polish（三提交，71/71 绿）："水印"即用户自建标签组（侧栏有"水印"组），toast=打标回执 InfoBar。①29e3949 选择模型 Explorer 心智：无修饰=单选重置（SelectSingleCard，点已选卡保持选中）、Ctrl=toggle 加减选（IsControlKeyDown 只判 Down）、Shift=范围重置（SelectCardRange 先清再选，原为纯加选）；状态行右侧新增「清除选择」按钮（常显灰态、CanExecute=HasSelection 联动）；demo onCardClick 同步。②8874d93 成功回执一律静默（全成功分支不弹且关残留回执，exclusiveHint 删除；单图 BeginTagOperation(showProgress:false) 不再弹"正在处理"一闪）；失败/警告回执与批量进度保留（PRD 验收项，口径修订记于敏捷 PRD）；附带收益=InfoBar 不弹则 TopChromeHeight 不被推高、右栏避让区不抖。③97daba 放大置底：整体拆除 IsCurrentImageZoomed 链（UpdateZoomLayering/_isCurrentImageZoomed+回图库清理/MainWindow 订阅与处理器/两处 XAML 注释改置底口径），推翻同日早前"最高层"拍板；缩放交互态（_zoomOwnerPath/全分辨率升级/切图复位）保留。实机走查（主代理）：目录选择器打标成功与 chip ✕ 移除均无 InfoBar、chip 就地更新；raw 点击卡片 A→"已选 1 张"→点击 B→仍 1 张（单选重置）→点清除选择→0 张按钮禁用；Ctrl/Shift 注入不可达（GetKeyStateForCurrentThread 读不到分批注入的修饰键，与拖拽滚轮同列）——分流代码人工审查与拍板一致，留用户实机。走查顺手恢复 test-library（打标后移除）。留痕 features/selection-and-zoom-polish prd+spec。

user:
还有几个需要调整的：1. 下侧状态栏是不是多余？感觉被右侧栏替代了，而且 UI 上也有重叠；2. 图片详情页没有好的返回按钮，只能点工具栏"返回图库"，操作习惯上很奇怪；3. 清空选择和清空筛选两行占位置——不需要单独的清除多选按钮，多选后正常点击一张即单选（已实现），移除清除按钮就行。

assistant:
敏捷项 back-button-and-chrome-simplify（三提交，71/71 绿，主代理实机走查通过）。①0725322 状态栏整体移除：探索实锤单图内容与右栏同源重复（UpdateStatusText 同方法写两处）、图库模式显示残留文案（切模式不清）；4 类独有信息迁 InfoBar（重命名批量回执=全成功静默/有失败弹 Warning 聚合数；删除/移动/加载失败走 ShowInstantTagFeedback 即时提示）；扫描进度本就走图库状态行无丢失；BottomChromeHeight 避让半链整体移除（TopChromeHeight 保留）。②bbd3b5b 单图左上"◀ 返回图库"浮层按钮：画布层 Z1、左侧动态让出左栏宽（280+12/36+12，订阅 IsSidebarCollapsed 跟随）、顶部 TopChromeHeight 避让、无图库禁用灰态；关键点是左侧避让（否则被 chrome 层左栏遮盖，与右栏按钮被遮同构）。③c014350 撤销清除选择按钮（上轮刚加，用户权衡后移除）：ClearCardSelection 本体保留（6 存活调用点）。过程实锤新环境坑入 RULE：XamlCompiler 确定性崩溃——MainAreaGrid 作为 ChromeLayer 末子元素时 Pass1 沉默崩溃（与 Defender 无关，二分实锤），以 MainInfoBar 后置规避（Grid.Row 定位不受顺序影响）；另补 restore 后校验 nuget.g.props 存在。实机走查：CLI 直开返回按钮禁用灰态位置正确、开图库后双击进单图返回按钮启用、raw 点击真实命中回图库（选中保留）、状态行纯文本无按钮。留痕 features/back-button-and-chrome-simplify prd+spec。

user:
我发现底部的图片名栏没去除，这个也移除吧。

assistant:
补删完成（06b8811，trivial 豁免主代理直接实现，build 一次过 + 71/71 绿 + 实机走查通过）：SingleImageView 删 FileNameOverlay 底部浮层，RebuildFileNameInlines 仅重建右栏 InfoFileNameText（文件名由右栏信息区承载，完整名 tooltip 保留）；demo 顶部 viewer-file 区（fileName+path 行）与 CSS 同步删除。实机：单图底部无文件名栏、右栏文件名正常、画布居中不变。补记入 back-button-and-chrome-simplify 的 prd/spec。至此单图模式底部 chrome 全清（状态栏+文件名栏均无），信息呈现收敛于右栏。

user:
还有一个问题，一个标签处于"未分组"，我理解这是脏数据？不应该这样兜底——直接忽略没有分组的标签，因为理论上标签一定有分组（UI 操作逻辑限制的）。

assistant:
敏捷项 ungrouped-tags-ignore（f87767b+9440d8c，71/71 绿，主代理实机走查通过），推翻 4679a12"曾见即留"与 spec Step 9 虚拟组设计。拍板口径：①聚合类 UI 完全忽略无组标签——侧栏"未分组"虚拟组（含曾见即留/ForgetUngroupedTag/未分组行✎✕）、卡片角标（对齐 demo 只遍历组内，+N 按过滤后）、筛选回退全部删除；②右栏 chips 保留显示无组标签且 ✕ 可移除——唯一清理出口（有意偏差 demo），移除管线本就按名落盘（FindGroupByTagName 兜底组保留仅满足签名，remove 不消费组）；③归组途径=配置组内新建同名标签自然收编；④互斥语义层"组外保留"（2 项测试覆盖）与 Services/索引层零改动。探索要点：目录选择器天然只遍历配置组（零改动）；打标入口全携带配置组不可能产出无组标签；TagSidebarControl 未分组视觉 x:Bind 分支删字段时被编译期强制清零。实机走查：造 photo_99[神秘标签].jpg → 图库侧栏无"未分组"组；CLI 直开 → 右栏 chips 显示"神秘标签" → ✕ → 磁盘改名 photo_99.jpg 落盘实证；产物已清理。留痕 features/ungrouped-tags-ignore prd+spec。

user:
清除筛选按钮用处不大（二次点击标签即可取消）；且目前不支持筛选无标签图片——参考截图（Billfish 标签面板标题栏的"无标签素材"按钮），增加一个 icon 筛选无标签图片。

assistant:
敏捷项 untagged-filter-entry（77bcdd8+b2fa29f+c064cd1，74/74 测试绿含 3 新用例，主代理实机走查通过）。①77bcdd8 索引层 QueryUntaggedAsync（tags IS NULL OR tags=''，独立方法不动 QueryByTagsCore 空集=全量语义）+T_IX_08/09/10。②b2fa29f：IsUntaggedFilterActive+ToggleUntaggedFilter 命令；HasAnyFilter 派生统一挂 FilterBarVisibility 与扫描渐进过滤（原 IsTagFilterActive 删除）；ApplyTagFilterAsync 三分流；互斥拍板——无标签图片按定义不含任何标签，点标签筛选自动退出无标签态、反之激活清空标签集；筛选条"无标签"chip（FilterChipText 空组名特判、chip 中性灰防 HueOfName("")落红色）；侧栏标题行三列 ∅ 按钮（U+2205，激活=强调色淡底，ApplyTheme 重建侧栏故无主题滞留）；删 ClearTagFilters 命令与按钮；打开图库重置。实现微调两处：未激活背景 TransparentBrush（null 会丢画刷）、chip 空组名配色特判。③c064cd1 demo 同步。实机走查：∅ 激活命中 10/10+chip+清空按钮确认消失；∅ 下点"大水印"行互斥切换为标签筛选（命中 0 空态提示）；误触 ✕ 弹删除确认框取消正常（顺带无回归）。留痕 features/untagged-filter-entry prd+spec。

user:
左侧栏标签的选择逻辑也和图片一样吧，ctrl+点击是多选，默认是单选。

assistant:
敏捷项 tag-filter-select-model（a6f9274，trivial 豁免主代理直接实现，74/74 绿，实机走查三步通过）：侧栏标签筛选对齐卡片 Explorer 心智——无修饰=单选重置（Clear+Add，多选集下点击也替换）、**保留既有习惯**（当前唯一选中的标签再点=取消回全量——用户在移除清空筛选按钮时明确依赖二次点击取消，与图片"点已选卡保持"是刻意差异：筛选是状态可取消、选择集用于批量）、Ctrl+点击=加减选（原 toggle 收窄到 Ctrl）。实现：TagSidebarControl 补 IsControlKeyDown（照抄 WaterfallView 模式只判 Down）经 HandleChipTappedAsync 转发；ToggleTagFilterAsync 三分支；demo onChipClick 同语义。走查注意：CLI 直开无索引时筛选无处执行（ApplyTagFilterAsync 依赖 _indexService），须先开图库；侧栏行按钮 UIA 索引与标签文本有一格错位（点按钮落在下一行标签），按错位规律反推目标行。留痕 features/tag-filter-select-model prd+spec。

user:
合并到 master，然后我们对整个项目做一个完整的 CR。

assistant:
合并 b6b3e7c（--no-ff，92 文件 +14925 行）后执行 code-review-loop full：round1 三并行 review-scope（vm/views/services readonly 子代理），round2 review-full 全局维度（D/E/F/H-K）+fix-spec 逐条校验，末轮 trivial 豁免主代理直接增补。产出 cr-fix-spec.md（3854309，fix-spec-ready）：P0=0/P1=8/P2=17。P1 要点：扫描中重开图库竞态（ODE+状态覆盖+List 并发）、扫描中激活筛选渐进追加重复卡片、三段高亮死代码链、PRD 回车进单图未实现、卡片回收不释放缩略图/拖拽小图（5 万张内存无界）、卡片 Tapped 违反 RULE 硬约束（键盘/UIA 不可达）、EXIF 方向未计入宽高比（竖拍卡片比例错——高价值发现）、自然排序双实现无 parity 测试。P2 要点：OpenLibrary 错误处理/死代码清理×6/筛选状态机与 MasonryLayout 下沉 Core 补测/RebuildAsync Task.Run/SettingsService 迁移容错/260 边界>=/索引 Dispose 竞态/LIKE 转义测试/标签名非法字符校验/英文注释中文化。spec_deviations 7 项全闭合（AskUserQuestion 用户拍板：批量移除接受收窄、LastLibraryRoot 保留补记文档；其余有敏捷留痕）。C-orch 正面结论：打标四入口收敛两管线单点落盘、筛选单一事实源、计数单一源。open_questions 9 项待拍板（重命名部分失败口径/缓存键稳定性/thumbcache 容量等）。**fix-spec-ready 已达成待用户确认是否开工执行**（skill 终点是修复说明书，未改实现代码）。
