---
date: 2026-09-19
dependency: iterations/图片打标签与瀑布流浏览/prd.md
---

# tagging-pipeline-fixes Feature PRD

## 背景与变更动机

用户走查提出三个管线问题：
1. **打标后图片重新加载**：打标 = 重命名文件，图片字节不变，却出现闪空/重新解码/缩放复位，性能差且打断浏览态。
2. **瀑布流"文件名不变化"**：期望打标后瀑布流有可见反馈。
3. **标签数量上限**：需按文件名长度计算"还能挂多少标签"，且 Windows/Linux/macOS 限制规则不同。

## 范围说明（相对原需求）

- 问题 2 经核实大部分为**设计行为**（D15：卡片显示名剥离标签段、打标不改行序）；真实缺陷是角标颜色（hue 索引）滞后刷新，本次修复。
- 平台规则确认：Windows/NTFS 文件名 255 个 UTF-16 字符（全路径默认 260）；Linux（ext4 等）255 **字节**（UTF-8 汉字 3 字节，预算缩至约 1/3）；macOS/APFS 约 255 字符。**跨平台安全按最严的 Linux 字节数预算**，同时保留 Windows 260 全路径预检（双口径并存、文案区分）。

## 影响模块与接口

| 模块 | 变更 |
|---|---|
| MainViewModel | 单图打标后不再 LoadCurrentAsync（同图改名轻量刷新）；CurrentImageRenamed 事件；SyncRenamedItemsAsync 磁盘探测与缓存迁移移入 Task.Run；角标 hue 重通知 |
| ImageLoaderService / ThumbnailService | 新增 MigrateCache（改名后内存条目迁新键 + 缩略图磁盘缓存复制，失败静默降级） |
| Services（Core） | TagFilenameBudget 纯函数（255 UTF-8 字节组件名预算）；BuildNewPath 双口径拦截；单图打标前预检 |
| SingleImageView | CurrentImageRenamed 订阅：缩放态归属路径追新（不复位） |

## 验收标准

- [x] 单图打标：图片不闪空、不重解码、缩放/平移态保持；状态行/文件名分段按新路径刷新；GIF 特判按旧策略重载一次。
- [x] 打标后翻页回来命中迁移后的解码缓存；缩略图缓存键同步迁移（筛选切换不引发全量重解码）。
- [x] 打标后瀑布流卡片角标底色即时更新（hue 重通知）。
- [x] 超预算打标被拦截：单图入口即时拒绝并附超出字节数；批量失败明细含双口径文案（255 字节 / 260 字符各自可断言）。
- [x] 重命名幂等比较口径统一为 OrdinalIgnoreCase（Windows 文件系统语义）。

## 测试用例

1. 单测：TagFilenameBudget（纯 ASCII/中文/混合、达界超界）；BuildNewPath 双口径文案（扩展 T_TG_07 模式）；幂等口径（T_TG_11）。合计 71/71 绿。
2. 实机：目录打标加/删两轮无闪空、无空态、按钮态保持（竞态修复后复测通过，诊断日志零失败记录）。
