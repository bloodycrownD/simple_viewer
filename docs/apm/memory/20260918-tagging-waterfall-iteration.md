---
date: 2026-09-19 10:08
title: 打标签与瀑布流迭代：从需求到实机走查修复的全过程
keywords: TagSpaces, 文件名标签, 瀑布流, WinUI3, PRD/SPEC, code-dev-loop, 卡死, 模糊, 走查, 选择bug, IME, Locked位, 删除bug, 滚轮缩放, 毛刺, Fant, 全分辨率重解码
abstract: 完整记录"图片打标签与瀑布流"迭代：需求澄清→PRD→原型→SPEC→14 phase 实现→四轮用户走查修复（启动崩溃/样式/卡死模糊/选择交互与删除假删/滚轮缩放与线条毛刺/放大全分辨率重解码）。分支 feature/tagging-waterfall（HEAD 781131d，36 提交），63 测试绿，未合并。
---

user:
我要给图片打标签，用 TagSpaces 协议，支持互斥与非互斥标签组，图片几十万张。顺便瀑布流排布、左侧标签栏、UI 优雅些。

assistant:
澄清后拍板：文件名标签模式（`photo[标签1 标签].jpg`，零额外文件，打标即改名）；几十万张卡不卡取决于数据层（虚拟化+SQLite 索引+缩略图管线）而非 UI 框架，WinUI 3 反而内存预算最宽。走了完整流程：PRD（含 8 项风险默认值）→ HTML 交互原型（demo/，用户验收通过）→ SPEC（15 条决策 D1-D15、14 Step、38 测试用例，经 spec-check-loop 两轮审查）→ code-dev-loop 16 wave 实现完毕（dev-ready）。

user:
启动无反应，你验证过吗？

assistant:
承认验证缺口（只验了编译+单测，没实机启动）。加全局异常日志后秒定位：WaterfallView 用了不存在的主题资源键 LayerFillColorSecondaryBrush，XAML 编译期不校验 ThemeResource 键名、运行期才炸。教训：启动冒烟是底线验证；全局异常日志（startup.log）成为常驻设施。

user:
标签删除不了、输入框卡顿、样式和 demo 差太多。

assistant:
三个根因：①未分组标签（存量文件名标签）的编辑按钮被整体隐藏——补齐入口；②扫描期每 1.5s 全表聚合计数重建侧栏 + 频繁 UI 追加——节流 5s + 合并批量；③分组色相/卡片层次/筛选条没做——按 demo.css 对齐。连带修了两个自发现问题：同步阶段误判目标冲突（回执"成功1失败1"、索引不同步）和重开图库索引孤儿行。

user:
标签字体也是黑色？你看过工具的样式了吗？

assistant:
用户抓得准：深色主题下 chip 文字黑底黑字。根因是 Application.Current.Resources 的 ThemeResource 查找不认 RootGrid.RequestedTheme 运行时覆盖。修复：代码颜色改 IsDarkTheme 双值驱动 + 主题切换重建。此轮后确立"改样式必截放大图逐元素核对"的流程。

user:
F:\Pictures\Storage\new 打开就卡死，图片还模糊。

assistant:
最硬的一仗。图库仅 37 张图（非规模问题）。搭无人值守诊断（自动恢复上次图库 + UI 心跳看门狗 + 操作追踪）后两次复现：卡死发生在首帧渲染期首批缩略图解码落位时（UI 线程 <1s 即冻、消息泵假活）。根因（高置信）：并发 BitmapImage.SetSourceAsync 在首帧期与合成器死锁，清缓存后的全新解码风暴最易触发。修复：UI 应用段串行闸门（UiApplyGate），解码仍并行。验证：3 轮清缓存风暴全绿。模糊：用户屏 150% 缩放，改"实际卡宽×DPI 动态分桶"。附带：XamlRoot.RasterizationScale 在隐藏容器不可靠，改窗口句柄 P/Invoke 查 DPI。

user:
图片的选择和取消选择有bug，或者说根本没有取消选择，选择也不是单选而是从第一张到点击的所有图片。你是不是没测试过？继续修复，确保所有功能都测试过，只测标签功能是没用的。

assistant:
又是一次验证缺口实锤——选择交互从未实机点过。根因抓得非常准：IsShiftKeyDown 等 4 处 `|| CoreVirtualKeyStates.Locked` ——中文 IME 用 Shift 切中英文会置位 Shift 的 toggle 位，导致所有普通点击被判为 Shift 连选（首击设锚点后 SelectCardRange 只加不减，所以永远无法取消、表现成"从第一张连到点击处"）。诊断时用 SendInput 注入实测拿到实锤日志（winrt=Locked, win32Async=0）。顺手挖出两个连带真 bug：①删除命令确认后只删内存列表，FileOperationService.DeleteToRecycleBin 从未被调用——文件根本没进回收站，重开图库"已删"图片复活（修复：磁盘删除+索引 RemovePath+图库列表/瀑布流/扫描计数四方同步）；②工厂默认快捷键 Ctrl+A=左旋与图库 Ctrl+A 全选冲突，绑定优先逻辑使全选在出厂默认下永不触发（默认改 Ctrl+L/Ctrl+R，存量 settings.json 不迁移）。本轮完成全功能实机走查（自动化驱动真实 app）：单选/取消/多选/Shift 连选/Ctrl+A/Esc 三态/批量打标/部分移除/筛选/双击看图/方向键翻页/单图打标/删除全链路/旋转/主题三态（像素断言）/设置页，全部通过；63/63 单测绿。方法论沉淀进 RULE.md「实机 UI 测试」段：UIA 断言+像素采样优先于视觉模型、SendInput 注入三坑（INPUT 40 字节对齐/前台窗口/DPI aware）、临时改用户 settings 须备份还原。

user:
图片滚轮放大缩小不支持吗？另外我发现图片解码可能适配不好，有的图片出现线条不顺滑，有毛刺的现象。

assistant:
两个都实锤：①单图视图原本就是居中 Image，零缩放设施——新增滚轮缩放（光标锚点不动点缩放/拖拽平移/双击复位/切图复位/旋转共存，锚点公式 T' = v − (s'/s)·(v − T)，旋转矩阵在推导中消去 R·k·R⁻¹=k，首版带旋转向量的写法是错的）；②毛刺两个根因：WIC 解码缩小默认 Linear（大倍率缩小丢高频细节）→ 改 Fant；更隐蔽的是解码按整窗 1280 而显示区仅 806 → 显示层二次缩小 0.63× 重新引入锯齿 → 解码尺寸源改为 SingleImageView.ImageHost 实际显示区。验证：UIA bounds 与数学推导逐项吻合；毛刺用 3000×2000 线条图物理像素采样+视觉评估（斜线平滑/圆弧无多边形化）。方法论教训：亚像素线（显示宽<0.5px）任何插值都会摩尔纹，测试图必须用真实线宽；中间灰度计数不是毛刺的可靠指标（Fant 箱滤波天然比双立方少），视觉形态评估+物理像素截图更可信。提交 12bdd5d。已知限制：放大超解码分辨率会像素化（全分辨率重解码待做）。

user:
增加 放大时自动全分辨率重解码。

assistant:
落地（提交 0a2e985）：缩放跨 1.2x 阈值触发 MainViewModel.EnsureFullResolutionAsync——以 decodeSize=null 重解码原图、原地换 ImageSource（Uniform 布局渲染尺寸不变、合成器以高分辨率纹理采样）。关键设计：①以 CurrentImagePath 区分「切图」（复位缩放）与「同图分辨率升级」（保持缩放态）——否则换源会重置正在进行的放大；②每图一次+失败不重试，等待期切图丢弃结果；③LRU 以 decodeSize 为键 fit/全分辨率两档共存。实机验证：放大 2x 物理像素评估为高分辨率源采样（边缘锐利、无低分辨率位图拉伸痕迹）——证明 WinUI 的 RenderTransform 缩放作用于源纹理而非元素布局光栅，这条路成立。

user:
更新记忆。

assistant:
落盘收尾。当前状态：分支 feature/tagging-waterfall（HEAD 781131d，36 提交），63/63 单测绿，未合并 master。本迭代四轮用户走查全部闭环：①启动崩溃（ThemeResource 键）②样式/卡死/模糊（chip 双值主题、UiApplyGate 串行闸门、DPI 动态分桶）③选择交互（IME Locked 位×4 处）+删除假删+Ctrl+A 默认绑定冲突④滚轮缩放+毛刺双根因（Fant 插值、显示区贴合解码）+放大全分辨率重解码。全功能实机走查方法论沉淀在 RULE.md。待办：用户实测确认本轮缩放/毛刺/全分辨率体验；合并回 master；用户本机 settings.json 仍是旧默认（Ctrl+A=左旋），想用全选需改绑或删配置重生成；可选：publish.ps1 重试、Defender 排除（需用户拍板）。
