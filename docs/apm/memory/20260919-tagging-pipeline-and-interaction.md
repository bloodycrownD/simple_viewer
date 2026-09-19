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
