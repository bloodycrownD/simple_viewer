---
date: 2026-09-19 13:20
title: 打标管线修复与交互重构（pipeline-fixes + interaction-rework）
keywords: 打标性能, 缓存键, 重命名, 同图改名, 标签预算, 255字节, 点击筛选, 拖拽打标, 详情页右栏
abstract: 用户四连问的敏捷处理：①打标后图片重载根因=解码缓存键含路径且打标后走 LoadCurrentAsync 全量重载；②瀑布流文件名不变是 D15 设计行为（剥离标签段），真缺陷是角标 hue 滞后；③标签数量上限按最严平台 Linux 255 UTF-8 字节做预算（Windows 255 UTF-16 字符/路径 260，macOS ~255 字符）；④打标交互重构：点击标签=筛选（自动回图库）、拖拽卡片到标签行=打标（选中集整集）、单图右栏信息+标签 chips。两个敏捷串行：tagging-pipeline-fixes → tag-interaction-rework。
---

user:
几个问题：1. 需要根据文件名计算标签最大数量，windows/linux 限制不同（我理解 windows 255 字符，linux/mac 不同规则？）；2. 打标签时图片总是刷新，性能差——改文件名没必要重新加载图片，图片本身不变；3. 瀑布流文件名不发生变化；4. 打标交互不好用，改成：a 点击左侧标签=筛选出该标签所有图片（多选 OR）；b 拖拽图片到标签上打标（多选=整集）；c 图片详情页右侧新增一栏放图片信息与标签 chips（✕ 移除、＋ 弹标签目录选择添加）。

assistant:
先答规则：Windows/NTFS 文件名 255 个 UTF-16 字符、全路径默认 260；Linux（ext4 等）文件名 255 **字节**（UTF-8 汉字 3 字节，预算缩到 1/3）；macOS/APFS 约 255 字符。跨平台安全按最严的 Linux 字节数算。两轮并行探索结论：①根因=打标成功后 ToggleTagOnCurrentImageAsync 调 LoadCurrentAsync（MainViewModel.cs:1010-1014）——先置空 ImageSource 再按新路径重解码（ImageLoaderService 缓存键含路径:36,235 必 miss），缩放态也被当切图复位（SingleImageView.xaml.cs:81-91）；缩略图缓存同病（ThumbnailService.cs:85,279+，磁盘键 SHA1(路径)）。②DisplayName 剥离标签段是 D15 拍板设计（打标不改显示名与行序），真缺陷=角标 hue 静态索引只在侧栏 Rebuild 更新（GalleryItemViewModel.cs:70-91），打标后角标底色滞后；另有幂等口径不一致（TagService Ordinal:138 vs 同步侧 OrdinalIgnoreCase:1642）。③唯一预检是 BuildNewPath 的 260 字符（TagFilenameService.cs:11,129-133）。④点击分流在 MainViewModel.cs:805-840（打标优先于筛选——用户嫌弃的点）；卡片是 Border 可挂 CanDrag；树行 Button 可挂 AllowDrop；项目零拖拽/Flyout 先例；单图视图两行布局无右栏，解码尺寸源已是 ImageHost 显示区。

拍板决策：点击标签一律=筛选且自动切回图库（单图模式点击有可见反馈）；Shift+点击移除入口取消（移除走详情页 ✕，RemoveTagFromSelectionAsync 删除）；拖选中集内任一卡=整集打标、拖未选中卡=单卡；快捷键打标保留不动；标签目录选择器走 ContentDialog 惯例（无 Flyout 先例）；demo 同步（交互基准）。执行顺序：先 tagging-pipeline-fixes（bug+预算，"同图改名"路径是右栏打标的前置），后 tag-interaction-rework。

user:
（两轮实现由子代理完成，主代理实机走查）

assistant:
实现提交：fixes=791810e（同图改名不重载+缓存迁移）/1255db7（角标hue+幂等口径）/879ea6c（255字节预算+8单测，71/71 绿）；rework=1dd249f（点击=筛选+自动回图库）/b591b2e（拖拽管线+DropOverlay）/7ae4b7f（右栏+TagCatalogDialog）/60bdb7c（demo）。走查抓到并修复两个真 bug（6cf7e76）：①右栏＋按钮 CanExecute=HasImage 但漏加 OnHasImageChanged 刷新点→永久禁用；②打标改名竞态：InfoBar 弹出→布局抖动→ImageHost.SizeChanged→立即重载读旧路径→撞 File.Move→FileNotFound 清视图（图片消失/空态/按钮禁用，状态行随后被覆盖掩盖）——诊断日志实锤后双保险修复：打标中忽略视口尺寸变化+结束后补判定、LoadCurrentAsync 失败按"改名在途"重试（80ms 后重读路径）。复测两轮加/删标签全绿。
实机通过项：图库筛选开/关（命中统计/筛选条）、单图点标签自动回图库+筛选、目录选择器添加、chip ✕ 移除、组头折叠、双击进单图、卡片点选、侧栏计数与未分组虚拟组。**拖拽未能自动化验证**：本机 SendInput 鼠标移动事件被丢弃（ret=1 但光标不动，点击可达）——拖拽手势无法注入，代码链路已静态核对（格式串/事件/兜底齐全），真实手感留待用户实机；drag-tag-test.ps1 留作环境复测。
环境经验入 RULE：双 csproj 同目录 restore 踩踏（dotnet msbuild SimpleViewer.csproj -t:Restore -p:Platform=x64 解法）；SendInput 移动被丢；UIA 坐标=屏幕点勿乘缩放。留痕：features/tagging-pipeline-fixes 与 tag-interaction-rework 的 prd/spec。待用户确认。

user:
展开/收起为什么会刷新页面（图片闪一下）？

assistant:
根因：收展侧栏/右栏 → ImageHost 显示区尺寸变化 → 解码尺寸重载（RULE 铁律②的设计行为），而 LoadCurrentAsync 是"先 ReleaseCurrentImageSource 清空再异步解码"——清空到换源之间的窗口就是闪空。修复（579f33e）：区分同图/异图——同图重载（收展、窗口 resize 引起的尺寸变化）保持旧源显示直到新源就绪一次性换上（对齐 EnsureFullResolutionAsync 同图换源先例）；切图（路径变化）仍预清空防旧图残影；同图换源后对被替换旧源清 GIF UriSource 句柄（防文件锁阻碍打标改名——原设计靠加载前统一清，改为换源后精准清）。实机：收起/展开/切图循环全过。这也是"打标竞态"的同族问题（布局抖动→重载闪空），同图不预清空后此类闪空从根上消除。

user:
（走查中断后用户催办"活没干完？"）

assistant:
三项 UI 修正收尾（a737aa1，71/71 绿）：①左栏顶部空块根因 = WinUI CommandBar 主命令默认右对齐、左上留大块空白——工具栏整体重做为左对齐横向文本按钮行（Border+ScrollViewer+StackPanel+Ghost 按钮，分隔线 1px 竖 Rectangle；ThemeButton 从 AppBarButton.Label 改普通 Button.Content"主题：深色"）；②放大置顶：scale>1（容差 1.05）时 SingleImageView 提升 ImageArea ZIndex（盖右栏，同父）+ 写 VM.IsCurrentImageZoomed，MainWindow 订阅后提 ContentAreaGrid（盖左栏）与 MainAreaGrid（盖工具栏/InfoBar/状态栏）ZIndex=100——Grid 默认不裁溢出，双链提升实现"最高层遮盖"；复位/切图/回图库还原（OnCurrentModeChanged 清残留，防图库模式侧栏被盖不可交互）；③收起/展开图标化：左栏标题 ◀/折叠条 ▶、右栏标题 ▶/折叠条 ◀（BMP U+25C0/U+25B6，方向指向展开侧）。实机：滚轮放大遮左右栏→双击复位还原→◀收起折叠条▶→展开，全循环通过。踩坑两记：XamlCompiler 增量漏编 XAML（构建"成功"但跑旧 XBF——遇 UI 改动不生效先删 obj 冷重建）；CommunityToolkit 分部方法 OnCurrentModeChanged 已有实现、重复添加 CS0757（冷重建才暴露，增量构建居然没报）。demo 无折叠交互无需同步。

user:
优化弹窗主题：弹窗与深色主题不符；且弹窗中的标签库应与左侧栏标签库用相同 UI 样式（附截图：目录弹窗白底）。

assistant:
两处修复（7b0791e，71/71 绿）：①弹窗白底根因 = ContentDialog 宿主在 popup 层、不在 RootGrid 视觉树内，ThemeResource 与底色按应用/系统主题解析、不认 RootGrid.RequestedTheme 运行时覆盖（与侧栏画刷问题同族的另一半）——MainWindow 新增 ApplyDialogTheme(dialog)（dialog.RequestedTheme = RootGrid.RequestedTheme），四处 ContentDialog（设置/标签编辑/标签目录/删除确认）统一调用。②目录弹窗行样式对齐侧栏：组头行复刻侧栏组头（静态 ▾ chevron——目录内组恒展开状态真实、组名 SemiBold、互斥/兼容徽章、右对齐计数、36 高）、标签行复用 TagTreeRowButtonStyle + 缩进连接线 + 互斥组单选圆点（组色相描边、已含实心）+ ✓已有 + 右对齐计数；计数经 GetTagCatalogSnapshot 第三返回值与侧栏同源（_latestTagCounts），目录与侧栏计数永不分裂。实机验证：深色弹窗文字可读、像素级放大核实 chevron/连接线/圆点均渲染（低对比淡雅，与侧栏同款）；用户 settings 备份还原（保留用户自建组）。

user:
你测试了吗？怎么我一用就是 bug？1. 删除标签后，左侧的标签列表也一同删除了；2. 图片详情显示完整名字，列表页显示剥离标签的名字，不统一——要么都显示名字要么都显示完整名。

assistant:
两条都实锤修复（4679a12，71/71 绿）：①根因=未分组标签行仅由索引计数快照生成，计数归零（详情页 ✕ 移除最后一处引用）即从侧栏消失、无法再筛选或重新打标——修为「曾见即留」：TagSidebarViewModel 会话内记忆已见未分组标签，计数 0 仍保留行；显式删除/重命名（✎ 菜单）经 ForgetUngroupedTag 摘除避免残留；不落盘（未分组事实源是文件名，重启无引用即不存在）。注意：配置组标签行本就保留 0 计数，用户遇到的必是未分组路径。②口径统一为「剥离标签段显示名」：单图状态行/底部文件名栏/右栏信息区/瀑布流卡片全部显示 base 名，标签信息由右栏 chips+卡片角标承载，完整文件名挂 tooltip（WinUI TextBlock 无 ToolTip 属性须用 ToolTipService.SetToolTip，踩了一脚编译错）；旧 PRD"单图显示完整名+高亮标签段"口径由此推翻。demo renderViewer 同步。实机走查：photo_2[风景].jpg 三处显示名均 photo_2.jpg ✓；详情 ✕ 移除风景后未分组行保留计数 0 ✓、图片不消失（竞态修复稳定）。教训：交互重构后未覆盖"标签全生命周期只剩 0 引用"与"显示口径一致性"两类端到端场景，走查清单应加这两项。
