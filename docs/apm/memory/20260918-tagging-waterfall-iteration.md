---
date: 2026-09-19 00:05
title: 打标签与瀑布流迭代：从需求到实机走查修复的全过程
keywords: TagSpaces, 文件名标签, 瀑布流, WinUI3, PRD/SPEC, code-dev-loop, 卡死, 模糊, 走查, 选择bug, IME, Locked位, 删除bug
abstract: 完整记录"图片打标签与瀑布流"迭代：需求澄清→PRD→原型→SPEC→14 phase 实现→三轮用户走查修复（启动崩溃/样式/卡死模糊/选择交互/删除假删）。第二轮走查修复 4 处修饰键 Locked 位误判（IME 根因）、删除命令从不删文件、Ctrl+A 默认绑定冲突，并确立实机全功能走查方法论。分支 feature/tagging-waterfall（HEAD ccda278），63 测试绿，未合并。
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
更新记忆，我准备压缩了。

assistant:
落盘 RULE.md（构建/XAML/主题/架构/诊断/实机测试的持久规则）与本记忆。当前状态：分支 feature/tagging-waterfall（HEAD ccda278，30 提交），63/63 测试绿，未合并 master。待办：用户实测确认选择/删除修复；合并回 master；用户本机 settings.json 仍是旧默认（Ctrl+A=左旋），想用全选需在设置里改绑或删配置重新生成；可选：publish.ps1 加重试、Defender 排除（需用户拍板）。
