---
date: 2026-09-16
---

# 图片打标签与瀑布流浏览 技术规格（SPEC）

## 需求来源

- PRD：`docs/iterations/图片打标签与瀑布流浏览/prd.md`（已确认，2026-09-16，含风险项默认值拍板：文件名标签模式、递归图库、通用单选组等）
- 交互原型：`demo/`（HTML 原型，用户已验收，交互语义以原型为准参考）
- 技术现状：三份代码级探索报告（服务层/UI 层/构建链），本文档中的改动点均可追溯到探索证据

## 设计目标

在不大改现有单图查看能力的前提下，将应用演进为"左侧标签栏 + 瀑布流图库 + 单图查看"双模式，实现 TagSpaces 文件名标签模式的打标/筛选，并满足 PRD 性能口径（5 万张首屏 ≤3s、几十万张后台扫描不假死、10 万级筛选 ≤2s）。

## 总体方案

### 架构与数据流

```
事实源永远是文件名本身（TagSpaces 文件名协议）
           │
   LibraryScanService ──递归分块扫描──► LibraryIndexService（SQLite，可重建缓存）
           │                                   │
           │ 解析 [tag] 段                      │ 标签筛选查询 / 标签计数
           ▼                                   ▼
   GalleryItem 列表 ────► WaterfallView（ItemsRepeater + MasonryLayout 虚拟化）
           │                                   │
           │                     ThumbnailService（内存 LRU + 磁盘缓存，独立于单图缓存）
           ▼
   TagService（打标/移除/重命名/删除 = 重命名文件 + 互斥 enforcement）
           │
           └──► 更新 SQLite 索引行 ──► UI 刷新（缩略图角标/计数/文件名）
```

### 关键技术决策（与探索证据的对应）

| # | 决策 | 依据 |
|---|------|------|
| D1 | 新服务/模型全部放 `Services\`、`Models\`（自动编入 Core，可被 tests 引用单测）；WinUI 依赖的代码只进 `Views\`/`ViewModels\`/根目录 | 双 csproj Compile Remove/Include 白名单机制；tests 只 ProjectReference Core |
| D2 | 本地索引用 **Microsoft.Data.Sqlite 8.0.x**（Core 加包），库文件 `%LocalAppData%\SimpleViewer\index\<根路径哈希>.db`；标签事实源永远是文件名，索引可随时删除重建 | 探索确认 TFM 兼容、publish.ps1 的 RID-specific 发布自动带 e_sqlite3.dll；PRD 风险 8 已拍板允许可重建缓存 |
| D3 | 瀑布流 = **ItemsRepeater + 自定义 `MasonryLayout : VirtualizingLayout`**（列高最短优先分配；布局常量固化——目标卡宽 240px、卡间距 14px、列数 = max(2, ⌊视口宽/240⌋)、卡片高度 = 卡宽/宽高比 + 预留文字区高度，文字区高度为常量化估算值、实施可调；卡片高度由索引中的宽高比预计算），不引入 CommunityToolkit Labs 实验包 | WinAppSDK 1.6 公开 API；稳定通道无现成瀑布流布局 |
| D4 | **前置修复 `SettingsViewModel.TrySave`**：改为 load-modify-save（读现文件→替换 Shortcuts 字段→保存），不再 `new AppSettings { Version=1 }` 整体重建 | 探索发现该 bug 会把新增的 TagGroups 字段在保存快捷键时整体抹掉 |
| D5 | Settings schema v2：`AppSettings` 新增 `TagGroups: List<TagGroup>`；`SettingsService.Load()` 加版本迁移分支（v1 缺字段天然容忍，Version 统一升 2 并回写）；`Save()` 改临时文件+`File.Move` 原子替换 | 现无迁移分支且非原子写；JSON camelCase + 枚举字符串序列化选项不变 |
| D6 | Esc 语义：三态模式感知路由（在 `DispatchShortcut` 模式感知层拦截；默认绑定不改，用户肌肉记忆不破坏）——①单图模式且有图库 → 返回瀑布流（`BackToGallery`）；②瀑布流模式且存在选中集 → 清空选中；③其余情况 → 维持 ExitApp 原行为。路由挂接条件为 `match.Command == ExitApp` 的分派点（用户改绑后该命令整体按模式感知路由，非 ExitApp 命令不受影响）；对话框打开期间快捷键整体屏蔽（沿用 `_shortcutsEnabled` 惯例），Esc 优先关闭对话框 | 默认设置 `Escape→ExitApp` 与 PRD"Esc 返回瀑布流"冲突；全量改默认绑定风险大 |
| D7 | 快捷键打标走现有"命令+参数"体系：`ViewerCommand` 增加 `ApplyTag`，`ShortcutBinding`/`ShortcutMatchResult` 增加 `TagId` 字段（类比 TargetPath 先例）；`ShortcutService` 增加内存缓存（文件 LastWriteTime 失效检查），消除每次击键读盘 | MoveToFolder+TargetPath 先例；TryMatch 每键 Load() 的 IO 热路径 |
| D8 | 缩略图独立管线 `ThumbnailService`：查找顺序为 内存 LRU → 磁盘缓存 → WIC 解码（磁盘命中后回填内存）；WIC 降采样解码（固定目标宽 360px 分桶）+ 内存 LRU（按字节预算 ~300MB）+ 磁盘缓存 `%LocalAppData%\SimpleViewer\thumbcache\<sha1(path)>-<bucket>.jpg`（JPEG q80，GIF 取首帧静态图）；并发上限 4 + 滚动取消 | 现有 ImageLoaderService LRU=6 缓存全尺寸像素、面向单图预取，不可复用；瀑布流 GIF 用动画 BitmapImage 会爆内存 |
| D9 | 扫描服务 `LibraryScanService`：`IAsyncEnumerable<GalleryItem>` 分块产出（每块 ~500），预分词自然排序 key（一次性分配），`NaturalStringComparer` 保持不动（单图目录场景继续用）；首屏不等全量，渐进上报 | 现比较器每次 Compare 双侧 Tokenize 分配密集，几十万全量 OrderBy 不可接受；PRD 验收"扫描中即呈现" |
| D10 | 打标重命名 = `File.Move(old, new)` 同目录元数据操作（前置换算 + 260 字符路径校验 + 目标名冲突检测），批量失败聚合 `BatchOperationResult`；不动 `FileOperationService`（其 MoveToFolder"同名先删后移"语义绝不复用于打标） | 探索发现 MoveToFolder 覆盖语义风险；app.manifest 无 longPathAware |
| D11 | `app.manifest` 增加 `<longPathAware>true</longPathAware>`；同时重命名服务仍做前置长度校验（双保险，兼容用户未启用系统长路径策略的情况） | PRD 风险 9 |
| D12 | UI 文案直接硬编码简体中文（含存量英文文案一并中文化），不启用 resw/PRI 资源链 | 项目 `GenerateProjectPriFile=false` 等三开关是 dotnet CLI 构建的历史修复组合，重开有回归风险；PRD 风险 5 默认中文化 |
| D13 | 批量打标进度/回执用主窗口内嵌 `InfoBar + ProgressBar`，不用 ContentDialog（同一 XamlRoot 同时只能一个 ContentDialog，会与确认对话框互斥）；标签/组的增删改确认对话框沿用 SettingsPage 的 `TrySave()` 模板 | 探索确认的 ContentDialog 单实例限制与既有对话框惯例 |
| D14 | 双模式切换：MainWindow Row1 重构为"左栏（可折叠）+ 内容区"，内容区内 `SingleImageView`（从 MainWindow 抽出的 UserControl）与 `WaterfallView` 用 VM Visibility 计算属性互斥切换，单图视图由瀑布流选中项驱动、Esc 返回保持滚动位置 | 现有 x:Bind/Visibility 惯例，无 Frame/导航先例，最小侵入 |
| D15 | 瀑布流默认排序 = **发现顺序**：块内按预分词自然排序 key 稳定排序，块间按发现顺序直接产出（**不做跨块归并**——归并会使后到项插入已呈现行中间，恰恰制造位置跳动；打标重命名不改变显示名与行序）：打标重命名不引起位置跳动、不引起已呈现项重排；本期不提供排序切换 UI（demo 中的排序切换仅原型验证用） | 原型已验证体验；避免打标后全量重排与索引排序漂移；由风险表口径提升为正式决策 |

### 兼容性说明

- 旧 `settings.json`（v1）读取：`TagGroups` 缺失 → 空列表默认值，无损；保存时升 Version=2。
- **版本偏斜警告**：新版本写入 v2 配置后，若再运行旧版 exe 并保存设置，`TagGroups` 会被丢弃（旧版 TrySave 重建行为）。交付说明中注明"升级后勿回退运行旧版并保存设置"。
- SQLite 库与缩略图缓存均在 `%LocalAppData%\SimpleViewer\` 下，删除即全量重建，不影响事实源。
- `viewer <file>` / `-d -i` CLI 行为不变，且**不触发图库扫描**；单图直开（双击文件关联）同样不自动扫描父目录（保冷启动 ≤2s），用户可手动"打开图库"指向该目录；打标后文件名变化导致 `-i` 索引漂移属 PRD 已拍板的已知影响。

## 最终项目结构

```
SimpleViewer.sln
├─ SimpleViewer.Core.csproj            # +PackageReference: Microsoft.Data.Sqlite 8.0.x
│   ├─ Services/
│   │   ├─ （既有 6 组服务不动，除 SettingsService/ShortcutService 小改）
│   │   ├─ ITagFilenameService.cs / TagFilenameService.cs      [新] 文件名标签解析/合成/校验
│   │   ├─ ITagService.cs / TagService.cs                      [新] 打标/移除/重命名/删除 + 互斥 enforcement + 批量
│   │   ├─ ILibraryScanService.cs / LibraryScanService.cs      [新] 递归分块扫描
│   │   ├─ ILibraryIndexService.cs / LibraryIndexService.cs    [新] SQLite 索引/筛选/计数
│   │   └─ IThumbnailService.cs / ThumbnailService.cs          [新] 缩略图内存+磁盘缓存
│   ├─ Models/
│   │   ├─ （既有 6 个不动，AppSettings 加 TagGroups）
│   │   ├─ TagGroup.cs / TagDefinition.cs                       [新] 字段定义见下方模型契约
│   │   ├─ GalleryItem.cs                                       [新] Path/目录/基名/扩展名/标签列表/宽高比/显示名
│   │   └─ BatchOperationResult.cs                              [新] 成功数 + 失败明细
│   └─ Helpers/（不动）
├─ SimpleViewer.csproj                  # 仅当无 WinUI Helper 新增时不动；无新增计划
│   ├─ MainWindow.xaml(.cs)             [改] Row1 双栏 + 模式切换 + Esc 路由 + 对话框宿主
│   ├─ App.xaml.cs                      [改] 组装新服务
│   ├─ Views/
│   │   ├─ SettingsPage.xaml(.cs)       [改] 命令下拉补 ApplyTag；中文化
│   │   ├─ SingleImageView.xaml(.cs)    [新] 从 MainWindow 抽出的单图视图（完整文件名+高亮标签段）
│   │   ├─ WaterfallView.xaml(.cs)      [新] ItemsRepeater + 卡片模板 + 多选交互
│   │   ├─ TagSidebarControl.xaml(.cs)  [新] 左侧标签栏
│   │   ├─ TagEditDialog.xaml(.cs)      [新] 标签/组 新建·重命名（含影响张数提示）
│   │   └─ MasonryLayout.cs             [新] 自定义 VirtualizingLayout
│   └─ ViewModels/
│       ├─ MainViewModel.cs             [改] 模式状态/图库驱动/选中集/批量命令
│       ├─ SettingsViewModel.cs         [改] TrySave 修复（load-modify-save）
│       ├─ TagSidebarViewModel.cs       [新]
│       ├─ WaterfallViewModel.cs        [新]（薄壳，逻辑尽量下沉 Core）
│       └─ GalleryItemViewModel.cs      [新] 缩略图项（ImageSource 槽位）
├─ app.manifest                         [改] +longPathAware
└─ tests/SimpleViewer.Tests/
    ├─ TagFilenameServiceTests.cs       [新]
    ├─ TagServiceTests.cs               [新]
    ├─ LibraryScanServiceTests.cs       [新]
    ├─ LibraryIndexServiceTests.cs      [新]
    ├─ ThumbnailServiceTests.cs         [新]
    └─ SettingsServiceTests.cs          [改] 补 v1→v2 迁移用例
```

**模型契约（TagId 与标签组模型字段定义）**：

- `TagDefinition { Id: string 稳定 Id, Name: string }` —— Id 在创建时生成、持久化后不变
- `TagGroup { Id: string 稳定 Id, Name: string, Exclusive: bool, Tags: List<TagDefinition> }`
- `ShortcutBinding.TagId` 引用 `TagDefinition.Id`：Id 稳定，重命名标签不影响快捷键绑定
- `ValidateBindings`：ApplyTag 绑定必须携带 TagId 且引用存在的标签（沿用 MoveToFolder 校验 TargetPath 的先例）；删除标签时若被快捷键绑定引用则拒绝并提示先改绑定

## 变更点清单

**新增（Core，零 csproj 改动）**：上表 10 个 `[新]` 服务/模型文件；5 个测试文件。
**修改**：
1. `Models\AppSettings.cs` — +`TagGroups` 属性，Version 默认 2
2. `Services\SettingsService.cs` — Load 迁移分支 + 原子写 + 标签组/标签名校验（非空、无任何空白字符（char.IsWhiteSpace 全集，含全角空格/nbsp）与方括号、全局重名拒绝）+ `ValidateBindings`（ApplyTag 绑定必须带 TagId 且引用存在的标签）
3. `Services\ShortcutService.cs` — 设置内存缓存（LastWriteTime 失效）+ `TagId` 匹配透传
4. `Models\ShortcutBinding.cs` / `ShortcutMatchResult.cs` / `ViewerCommand.cs` — +`ApplyTag`/`TagId`（TagId 引用 TagDefinition.Id）
5. `ViewModels\SettingsViewModel.cs` — TrySave 修复 + ApplyTag 命令的标签选择 UI 状态
6. `ViewModels\MainViewModel.cs` — 模式/图库/选中集/筛选状态与批量命令（CanExecute 按模式）
7. `MainWindow.xaml(.cs)` — 布局重构、工具栏新增"打开图库"按钮（FolderPicker，仿现有 PickImageFileAsync 的 InitializeWithWindow 模式；选定根目录后进入瀑布流并启动递归扫描；CLI 与单图直开不触发扫描；首次启动无图库时瀑布流区显示空态引导文案）、DispatchShortcut 三态模式感知（Esc 路由：返回瀑布流/清空选中/ExitApp 原行为）、对话框宿主、`IsTextInputFocused` 白名单补新输入控件
8. `App.xaml.cs` — 新服务组装与注入
9. `SimpleViewer.Core.csproj` — +Microsoft.Data.Sqlite
10. `app.manifest` — +longPathAware
11. `Views\SettingsPage.xaml(.cs)` — ApplyTag 参数编辑 + 中文化

## 详细实现步骤

（依赖顺序：1→2→3 为标签数据链；4→5 为图库数据链；6 独立；7 依赖 1-6；8-12 依赖 7；13-14 收尾）

- Step 1 — phase-tag-filename — blocking: yes — qa: auto：实现 `TagFilenameService`：`TryParse(fileName) → (base, ext, tags[])`（尾部方括号、空格分隔、容忍多重空格/全角空格归一）、`Compose(base, ext, tags[]) → fileName`、`ValidateTagName`（拒绝空名、任何空白字符——char.IsWhiteSpace 全集，含全角空格与 nbsp——及方括号）、`BuildNewPath(oldPath, newTags)` 含 260 长度校验与目标冲突检测。测试 T-TF1~6。
- Step 2 — phase-settings-v2 — blocking: yes — qa: auto：`AppSettings`+`TagGroups`、`SettingsService` 迁移分支（读到 Version<2 → 补空 TagGroups、Version=2 回写）+ 原子写（temp+Move）+ 标签组校验方法；**同步修复 `SettingsViewModel.TrySave`** 为 load-modify-save（UI 工程改动；编译验证按风险表"每相结束回归 dotnet build"口径执行）。测试 T-ST1~4。
- Step 3 — phase-tag-ops — blocking: yes — qa: auto：`TagService`：`ApplyTagAsync(paths, tag, group)`（互斥组先剔除同组再追加）、`RemoveTagAsync(paths, tag)`、`RenameTagAsync(old, new)`、`DeleteTagAsync(tag)`（前置校验：标签被快捷键绑定引用时拒绝并提示先改绑定）、`DeleteGroupAsync(group)`（级联从全库文件名移除该组全部标签，先提示影响张数），全部基于 TagFilenameService 重命名 + `BatchOperationResult` 失败聚合（占用/冲突/超长）；互斥 enforcement 为纯函数 `TagSemantics.Apply(currentTags, group, tag)` 独立可测，且**只处理目标组内的标签，组外/未分组标签一律保留**。测试 T-TG1~10 + T-ST5。
- Step 4 — phase-library-scan — blocking: yes — qa: auto：`LibraryScanService.ScanAsync(root, progress, ct)`：`IAsyncEnumerable<GalleryItem>` 分块（500/块）、`EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }`、扩展名白名单常量共享、每项解析标签段 + 记录宽高比（JPEG/PNG 头部快速读取，失败回退 1:1）；预分词排序 key 缓存，块内排序、块间按发现顺序直接产出（不做跨块归并，见 D15；首屏即呈现）。测试 T-SC1~5。
- Step 5 — phase-library-index — blocking: yes — qa: auto：`LibraryIndexService`：SQLite WAL 模式；表 `items(path PK, dir, base_name, ext, tags TEXT 空格分隔, w, h, sort_key)` + `meta(key,value)`；库文件名 = 根路径规范化（小写、去尾部斜杠）的 SHA-256 前 16 字符 + `".db"`（落位 `%LocalAppData%\SimpleViewer\index\`，见 D2）；tags 列空格分隔存储，`QueryByTags(OR)` 采用左右补空格的 `LIKE '% tag %'` 填充写法，性能门槛为 10 万行量级实测 ≤2s（不达标再升级标签行表，后备口径见风险表）；库文件打开异常（SQLiteException）即删除重建。`UpsertChunk` / `RemovePath` / `UpdateTags`（打标后行更新）/ `QueryByTags(OR)` / `TagCounts()` / `RebuildAsync`（对账扫描：文件系统为准，孤儿行删除）。测试 T-IX1~6。
- Step 6 — phase-thumbnail — blocking: yes — qa: auto：`ThumbnailService.GetThumbnailAsync(path, bucket, ct)`：查找顺序 内存 LRU → 磁盘缓存 → WIC 解码（磁盘命中后回填内存）；WIC 解码（宽 360 桶，GIF 取首帧）→ JPEG q80 落盘 + 回填内存；字节预算 LRU（~300MB）+ `SemaphoreSlim(4)` + 请求去重。测试 T-TH1~5。
- Step 7 — phase-ui-shell — blocking: yes — qa: auto（编译+核心单测回归）+ manual_user（窗口走查）：抽出 `SingleImageView`（文件名完整显示并高亮方括号标签段，对齐已验收原型）；工具栏新增"打开图库"按钮（FolderPicker，仿现有 PickImageFileAsync 的 InitializeWithWindow 模式）——选定根目录后进入瀑布流并启动递归扫描；CLI（`viewer <file>` / `-d -i`）行为不变、不触发图库扫描；单图直开（双击文件关联）不自动扫描父目录（保冷启动 ≤2s，用户可手动"打开图库"指向该目录）；首次启动无图库时瀑布流区显示空态引导文案；MainWindow Row1 双栏重构（左栏可折叠按钮 + 内容区互斥视图）；`MainViewModel` 增 `ViewerMode`/`HasGallery` 与模式切换命令；Esc 三态路由（①单图+有图库 → BackToGallery；②瀑布流+有选中集 → 清空选中；③其余 → 维持 ExitApp 原行为）；`App.xaml.cs` 组装；存量文案中文化。回归：单图翻页/旋转/全屏/删除/移动/CLI 启动全部不退化（走查清单 M1）。
- Step 8 — phase-waterfall — blocking: yes — qa: manual_user：`MasonryLayout`（VirtualizingLayout：估算行实现列分配、视口外回收；布局常量按 D3 固化——目标卡宽 240px、卡间距 14px、列数 = max(2, ⌊视口宽/240⌋)、卡片高度 = 卡宽/宽高比 + 预留文字区高度，文字区高度常量化、实施可调）+ `WaterfallView`（卡片模板：缩略图占位→渐入、标签角标、选中态、卡片文件名剥离标签段——单图模式展示规则见 Step 7）+ 扫描渐进填充 + resize 去抖重排。验收走查 M2（首步：通过工具栏"打开图库"选定根目录，瀑布流开始呈现并启动递归扫描；含 PRD"滚动不错位不跳动"）。
- Step 9 — phase-tag-sidebar — blocking: yes — qa: manual_user：`TagSidebarControl`（分组 chip、互斥组单选圆点样式、每标签计数实时刷新、点击=筛选；解析到但不属于任何已配置组的标签进入左栏固定末位的**"未分组"虚拟组**——可点击筛选、不可配置互斥属性；为"未分组"组打标属普通非互斥操作；如需把未分组标签纳入正式组，在正式组新建同名单标签即视为同一平铺字符串（文件名协议的平铺语义，无需专门的"移动"操作））+ `TagEditDialog`（新建/重命名/删除组与标签，重命名/删除前显示影响张数）。走查 M3。
- Step 10 — phase-batch — blocking: yes — qa: manual_user：瀑布流多选（单击/Ctrl/Shift 连选/Ctrl+A）+ 点击左栏标签批量打标/Shift 移除 + 单图模式当前图打标 + `InfoBar` 进度与失败回执（成功数/失败数与原因，成功不回滚）。走查 M4（含 Esc 清空选中、单张打标操作反馈 ≤1s）。
- Step 11 — phase-filter — blocking: yes — qa: manual_user：筛选条（激活标签 chip、OR 语义标识、命中数、单删/清空）→ `LibraryIndexService.QueryByTags` 驱动瀑布流刷新，筛选态下单图翻页在命中集内循环。走查 M5。
- Step 12 — phase-shortcut-tag — blocking: yes — qa: auto+manual_user：`ViewerCommand.ApplyTag` + `ShortcutBinding.TagId`（引用 TagDefinition.Id，见模型契约）+ `ShortcutService` 缓存（自动测试 T-SK）+ `ValidateBindings`（ApplyTag 绑定必须带 TagId 且引用存在的标签，沿用 MoveToFolder 校验 TargetPath 的先例）+ 设置页 ApplyTag 参数编辑（标签下拉）+ `DispatchShortcut` 接线（单图与瀑布流选中集两条路径）。
- Step 13 — phase-i18n-polish — blocking: no — qa: manual_user：深浅色主题走查、左栏折叠态、空态/加载态文案、Win11 Fluent 走查（对齐旧迭代口径）。
- Step 14 — phase-perf-qa — blocking: yes — qa: manual_user：5 万张目录首屏 ≤3s、几十万张目录树后台扫描全程可操作、10 万级筛选 ≤2s、单图切换 <100ms 与冷启动 ≤2s 不退化；`scripts\publish.ps1` 全链路冒烟（含 SQLite native 落位）。

## 测试策略

**单元测试（xunit，仅 Core，命名沿用 `T_<模块>_<序号>_<描述>` 既有惯例，临时目录沿用 TempDirectory 模式）**：

- T-TF1 — Step1 — 无标签文件名解析为空标签集
- T-TF2 — Step1 — `a[风景 已修].jpg` 解析/往返合成一致；多重空格归一
- T-TF3 — Step1 — 非尾部方括号（`a[b]c.jpg`）不视为标签
- T-TF4 — Step1 — ValidateTagName 拒绝空名/含任何空白字符（char.IsWhiteSpace 全集，含全角空格与 nbsp）/含方括号
- T-TF5 — Step1 — 新路径超 260 字符时返回明确失败（不抛异常）
- T-TF6 — Step1 — 目标文件名已存在时返回冲突失败
- T-ST1 — Step2 — v1 配置读取 → TagGroups 空、Version 升 2 并回写
- T-ST2 — Step2 — v2 配置读写往返无损（含快捷键+标签组共存）
- T-ST3 — Step2 — 标签组校验：组内重名/跨组重名/非法字符拒绝
- T-ST4 — Step2 — 保存原子性：写入失败不破坏原文件
- T-ST5 — Step3 — 删除被快捷键绑定引用的标签被拒绝并提示先改绑定
- T-TG1~10 — Step3 — 互斥组替换语义/非互斥叠加/批量部分失败回执/RenameTag 全量更新/DeleteTag 清理/幂等打标/长路径拒绝/GIF 文件名打标（仅改名不动内容）/T-TG9：组外（未分组）标签在互斥替换后保留/T-TG10：互斥⇄非互斥切换不改已落盘标签（后续打标按新属性执行）
- T-SC1~6 — Step4 — 递归含子目录/不可访问目录跳过（环境无法稳定模拟 ACL 时降级为枚举选项断言）/白名单过滤/分块渐进产出（首块早于全量完成）/取消令牌生效/T-SC6：存量文件名标签解析与显示名剥离（"聚合进未分组组"的侧栏呈现验收归 Step 9 走查，扫描层测其前置子集）
- T-IX1~6 — Step5 — Upsert 幂等/UpdateTags 行更新/QueryByTags OR 命中/TagCounts 正确/Rebuild 孤儿清理/损坏库文件自动重建
- T-TH1~5 — Step6 — 磁盘缓存命中不解码/内存 LRU 逐出/并发去重/取消不落盘/GIF 首帧静态
- T-SK1~3 — Step12 — 缓存命中不读盘（文件未变）/文件变更后失效重读/T-SK3：ApplyTag 绑定缺 TagId（或 TagId 引用不存在的标签）被 ValidateBindings 拒绝

**手动验收（qa: manual_user，不阻塞自动门禁，合并后按走查清单执行）**：
- M1 单图回归清单（Step7）、M2 瀑布流走查（Step8；首步：通过工具栏"打开图库"选定根目录，瀑布流开始呈现并启动递归扫描）、M3 标签栏走查（Step9）、M4 批量打标走查（Step10；含 Esc 清空选中与单张打标操作反馈 ≤1s）、M5 筛选走查（Step11）、M6 性能与发布冒烟（Step14；含 TagSpaces 互通验收：用 TagSpaces 桌面版（配置为文件名标签模式）打开打标后的图库目录，标签正确显示）、M7 Fluent/主题走查（Step13）
- UI 自动化测试本期不做（沿用历史 SPEC 口径；ViewModel 不可单测是构建结构使然，逻辑已尽量下沉 Core）

## 风险与回滚方案

| 风险 | 缓解 | 回滚 |
|------|------|------|
| `SettingsViewModel.TrySave` 丢字段（存量 bug，D4） | 列为 Step 2 硬门槛，先修再做标签组 | 单文件 revert |
| 自定义 VirtualizingLayout 复杂度（MasonryLayout 是本项目最大技术增量） | Step 8 独立成相；先实现非虚拟化正确版走查布局，再补虚拟化（同相内两段提交） | 降级为固定列 UniformGridLayout+宽高比裁剪（PRD 允许协商） |
| 打标即改名与排序/索引漂移 | PRD 已拍板接受；索引行级更新 + UI 就地刷新避免全量重排；默认排序 = 发现顺序（正式决策见 D15） | — |
| SQLite native 在发布链未实测 | Step 14 发布冒烟为 blocking；Core 加包后先本地 `dotnet test` 验证 | 换 LiteDB 纯托管（决策点已评估，D2） |
| dotnet CLI 构建脆弱（MRT/PRI 三开关历史坑） | 不动三开关（D12 资源决策）；每相结束回归 `dotnet build SimpleViewer.sln -p:Platform=x64` | — |
| 旧版 exe 回退运行并保存设置丢 TagGroups | 交付说明注明；schema 向前兼容（读取不炸） | — |
| 批量重命名中途失败 | BatchOperationResult 成功不回滚（PRD 拍板）；失败项可重试 | — |
| 性能不达标（5 万首屏/10 万筛选） | 各性能关键点均有独立缓存层（索引/缩略图/排序 key），可逐层扩预算；SQLite tags 列 LIKE 查询 10 万行实测不达标时升级为标签行表（多对多）结构；USN Journal 首扫优化列为后备手段（本期不实现） | 降低 PRD 性能口径需用户重新拍板 |

**总体回滚策略**：按 Step/phase 独立提交，任一相失败 revert 该相提交即可；数据层三个缓存（settings v2 字段、SQLite、缩略图）互不依赖，均可单独清理重建；git 工作流沿用当前 master 单分支（历史惯例），每相一组原子提交。
