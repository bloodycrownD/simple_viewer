---
date: 2026-09-18 23:10
title: 打标签与瀑布流迭代：从需求到实机走查修复的全过程
keywords: TagSpaces, 文件名标签, 瀑布流, WinUI3, PRD/SPEC, code-dev-loop, 卡死, 模糊, 走查
abstract: 完整记录"图片打标签与瀑布流"迭代：需求澄清（文件名标签模式/互斥组/几十万张）、PRD→原型→SPEC→14 phase 实现→用户走查暴露的 7 个问题及修复。当前分支 feature/tagging-waterfall（HEAD e5ee576，63 测试全绿）未合并，冻结修复待用户实测确认。
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
更新记忆，我准备压缩了。

assistant:
落盘 RULE.md（构建/XAML/主题/架构/诊断的持久规则）与本记忆。当前状态：分支 feature/tagging-waterfall（HEAD e5ee576），29 提交，63/63 测试绿，未合并 master。待办：用户实测卡死修复与清晰度（看门狗已武装，再冻结日志会自证）；M1-M7 手动走查；合并回 master；可选：publish.ps1 加重试、Defender 排除（需用户拍板）。
