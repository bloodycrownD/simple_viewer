---
date: 2026-09-19
agile_trace: true
---

# tagging-pipeline-fixes 实现规格（SPEC）

## 根因 / 方案摘要

**问题①根因链**（走查+诊断日志实锤）：单图打标成功后 `ToggleTagOnCurrentImageAsync` 调 `LoadCurrentAsync` → 先 `ReleaseCurrentImageSource()`（ImageSource 置 null 闪空）→ 按新路径重解码（ImageLoaderService LRU 键含完整路径，改名必 miss）→ SingleImageView 以路径判"切图"复位缩放。缩略图缓存（内存+磁盘 SHA1(路径)）同病，下次 ResetFrom 全量 miss。

**走查中实锤的竞态**（比根因更深的坑）：打标开始时 InfoBar 弹出 → 布局抖动 → ImageHost.SizeChanged → OnViewportSizeChangedAsync → 解码尺寸变化 → 立即 LoadCurrentAsync 读**旧路径** → 撞上 File.Move 改名 → FileNotFoundException → catch 分支清空视图（HasImage=false/图片消失/空态出现），随后打标链路的状态行刷新又掩盖"加载失败"文案——表现为"打标后图片神秘消失"。诊断日志定位：`FileNotFoundException: Image file not found | path=旧路径 | current=旧路径`（异常时 _imageFiles 尚未替换，路径比对重试不命中）。

**问题③方案**：Linux 255 UTF-8 字节组件名预算（最严平台，跨平台安全）+ 既有 Windows 260 字符全路径双口径并存。

## 变更点清单

| 提交 | 内容 |
|---|---|
| 791810e | 同图改名不重载：RefreshCurrentAfterRenameAsync 轻量刷新 + CurrentImageRenamed 事件（视图缩放态归属追新）+ ImageLoader/Thumbnail MigrateCache + 磁盘探测移 Task.Run + GIF 特判 |
| 1255db7 | 角标 hue 滞后修复（UpdateTagHues 变化后对已呈现卡片补发 Badges 重通知）+ 幂等口径统一 OrdinalIgnoreCase |
| 879ea6c | TagFilenameBudget 纯函数（Core）+ BuildNewPath 双口径拦截 + 单图打标前预检 + 8 个单测 |
| 6cf7e76（部分） | 竞态双保险：打标中忽略视口尺寸变化（_pendingDecodeSizeRefresh + ClearTagOperationRunning 补判定）+ LoadCurrentAsync 失败重试放宽（打标在途文件消失 → 80ms 后重读路径重试） |

## 详细改动说明

- **RefreshCurrentAfterRenameAsync**：`_currentLoaded` 非 GIF 时仅 `UpdateStatusText(loaded, 新路径)`（displayPath 参数重载——loaded.Path 停留旧路径，显示信息按新路径算）；GIF/缺失回退 LoadCurrentAsync（UriSource 指向路径无法内存重建）。
- **MigrateCache**：锁内扫描全部分档键（fit/全分辨率/旋转桶），旧键条目删除、新键插入共享像素数组的副本；缩略图磁盘缓存 File.Copy 旧 SHA1 → 新 SHA1（Task.Run 内，失败静默）。防 prefetch 竞态：在途 prefetch 回插旧键由 LRU 自然逐出，无害。
- **LoadCurrentAsync 竞态重试**：`for(attempt)` 循环；重试条件 = 路径已变 **或**（打标在途且文件已不存在）——后者等待 80ms 让同步阶段替换 _imageFiles 后重读。两轮加/删标签复测零失败（诊断日志已移除）。
- **TagFilenameBudget**（ITagFilenameService.cs 内静态类，对齐 TagSemantics 惯例）：`MaxFileNameComponentBytes=255`；ComposeFileName（拼接唯一来源，TagFilenameService 复用）；CheckFileNameBudget 超限返回「打标后文件名 N 字节超过 Linux 兼容上限 255 字节（超出 X 字节）」。

## 测试策略

### 测试用例

- 单测 71/71：预算函数边界（ASCII/中文/混合、恰好达界/超界）、BuildNewPath 双口径文案断言（"255"/"260" 各自触发）、大小写改名幂等（T_TG_11）。
- 实机：两轮目录加/删标签（竞态高发路径）图片不消失、按钮态保持、chips/状态行同步；loadfail 诊断日志零记录（修复后移除诊断代码）。

## 风险与回滚方案

- 风险：缓存迁移共享像素数组——旧条目引用与副本并存期间内存峰值略升（LRU 容量内可控）；GIF 打标仍有一次重载（UriSource 机制限制，已注释）。
- 回滚：revert 791810e/1255db7/879ea6c/6cf7e76 即回到旧行为；无 schema/配置变更。
