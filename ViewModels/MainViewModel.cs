// 职责：主查看器状态——单图导航/旋转/显示、双模式（图库/单图）互斥切换、图库扫描驱动、文件名标签分段、
//       瀑布流数据源驱动（渐进追加）与卡片选中集（Ctrl/Shift 连选/Ctrl+A，Step 10）、
//       打标管线（快捷键 / 拖拽卡片到标签行 / 单图详情右栏；2026-09-19 交互重构后侧栏点击不再打标）
//       与 InfoBar 进度/回执状态（Step 10，D13）、
//       标签筛选（tag-filter-tree：条件树单一内存求值 + 筛选条表达式段 chips/单删/命中统计/清空 +
//       筛选面板编辑入口 EditFilter——树编辑→互斥清 untagged→重应用集中一处管线，Step 5）、
//       无标签筛选（与条件树互斥，侧栏标题行 ∅ 按钮 toggle）、标签栏数据/编辑执行（Step 9）、
//       单图详情右栏数据（结构化信息行 + 当前图标签 chips）。
// 不变量：Prev/Next 环绕且重置旋转；仅视口解码尺寸变化时重载；
//         单图翻页列表 = 进入单图时的瀑布流呈现集快照（Step 11：筛选态翻页在命中集内环绕循环）；
//         ScanAsync 为同步磁盘 IO 迭代器，一律 Task.Run 后台消费、UI 线程仅经 Progress 收进度/扫描块（几十万张不假死口径）；
//         扫描块经 Progress 回投 UI 线程后追加进 WaterfallViewModel（虚拟化数据源，绝不一次性同步灌入）；
//         卡片选中集状态在本类（WaterfallViewModel 仅转发）；Shift 连选基于当前呈现序列范围加选、锚点随点击更新；
//         批量打标分批走 TagService（内部 Task.Run），批间回 UI 线程推进 InfoBar 进度（D13）；成功不回滚；
//         打标后就地同步（索引 ReplacePath + 卡片 VM UpdateFrom + 单图列表路径替换）——卡片 VM 实例不变，
//         选中集引用天然保持（Step 10：打标后选中集不丢，路径换新）；
//         标签筛选条件树与命中数在本类（tag-filter-tree spec D1/D4，单一内存求值器）：
//         筛选变化 → TagFilterState.Evaluate/MatchesUntagged 对 _galleryItems 全量谓词过滤 →
//         命中按 SortKey 自然序排序后瀑布流整体替换（对齐原索引查询分支口径；索引层零改动，
//         QueryByTagsAsync 留给 RenameFilesAsync（重命名连锁专用）编辑候选集）；取消筛选恢复扫描全量（发现序，不清空索引）；
//         扫描期间追加的块经同一谓词过滤后入瀑布流（筛选态与渐进追加互不干扰）；
//         筛选条 chips = BuildExpression 表达式段形态（条件/且或/括号；untagged 激活时为「无标签」chip，
//         与条件树互斥——任一方向激活清另一方）；重开图库清树（会话态不落盘）；
//         重开图库先 Cancel + await 旧扫描任务再清资源（cr/P1-1），旧扫描迟到的 UI 回投经代次校验丢弃；
//         标签/组编辑前置 ValidateTagGroups 预检（同口径）再动文件/保存配置，避免“文件已改、配置被拒”分裂（删除标签/组已纯化为配置操作、绑定引用前置拒绝，batch-tag-management Step 2）；
//         侧栏重建（ObservableCollection 写）一律经 DispatcherQueue 回投 UI 线程；
//         CLI/单图直开不触发图库扫描，扫描仅由「打开图库」触发。
// 调用链：App → MainWindow → MainViewModel → FileBrowser / ImageLoader / FileOperation / TagFilename / TagService /
//         LibraryScan / LibraryIndex / Settings / Thumbnail 服务。

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
using SimpleViewer.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace SimpleViewer.ViewModels;

/// <summary>查看模式（D14 双模式互斥切换）：Gallery=瀑布流图库，Single=单图查看。</summary>
public enum ViewerMode
{
    /// <summary>瀑布流图库模式。</summary>
    Gallery,

    /// <summary>单图查看模式。</summary>
    Single,
}

/// <summary>
/// 主窗口视图模型：单图查看状态、双模式切换、图库扫描驱动与瀑布流选中集。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IFileBrowserService _fileBrowser;
    private readonly IImageLoaderService _imageLoader;
    private readonly IFileOperationService _fileOperations;
    private readonly ITagFilenameService _tagFilename;
    private readonly ITagService _tagService;
    private readonly IThumbnailService _thumbnailService;
    private readonly ILibraryScanService _scanService;

    private readonly List<string> _imageFiles = [];
    private int _currentIndex = -1;
    private int? _decodeSize;
    private int _lastAppliedDecodeSize = -1;
    private string? _currentDirectory;
    private CancellationTokenSource? _loadCts;

    // 图库状态：_galleryItems 为扫描全量暂存（后台扫描线程逐项 Add，cr/P1-1 单写者）。
    // UI 侧消费：Waterfall 渐进追加（Progress 回投）与筛选管线——Step 6 后枚举点一律经
    // _galleryItemsLock 锁内拷贝（SnapshotGalleryItems）防 List 枚举版本冲突；Count 单读
    // 为原子引用读免锁（FilterStatsText 等高频绑定）。
    private readonly List<GalleryItem> _galleryItems = [];
    private readonly object _galleryItemsLock = new();
    private CancellationTokenSource? _scanCts;

    // 扫描任务句柄与代次（cr/P1-1 重开图库竞态收口）：重开时先 Cancel + await 旧任务（吞异常）
    // 再 Dispose 令牌/索引服务，杜绝旧续体迟到执行；代次递增使旧扫描经 Progress 异步回投 UI 的
    // 迟到回调（状态行覆盖 / 旧块追加瀑布流）凭 generation 比对静默丢弃。均仅 UI 线程读写。
    private Task? _scanTask;
    private int _scanGeneration;

    private ILibraryIndexService? _indexService;
    private string? _libraryRootPath;

    // 卡片选中集（引用相等去重；筛选/重开图库时整体清空）。
    private readonly HashSet<GalleryItemViewModel> _selectedCards = [];

    // Shift 连选锚点（最近一次点击的卡片 VM；随每次点击更新，Esc 清空选中时保留——对齐 demo 语义）。
    private GalleryItemViewModel? _lastClickedCard;

    // 批量打标/移除防重入闸（操作进行中忽略新的侧栏 chip 打标请求）。
    private bool _isTagOperationRunning;

    // 打标期间的视口尺寸记录与补判定（2026-09-19 打标刷新竞态修复）：打标开始时 InfoBar
    // 弹出会引发布局抖动 → ImageHost 尺寸瞬时变化 → 解码尺寸变化 → 立即重载会读到改名前的
    // 旧路径（File.Move 已落盘、同步阶段尚未替换 _imageFiles）→ FileNotFound 清空视图。
    // 故打标进行中忽略视口尺寸变化，结束后用最后尺寸补一次判定（此时路径已同步，安全）。
    private int _lastViewportWidth;
    private int _lastViewportHeight;
    private bool _pendingDecodeSizeRefresh;

    // 标签筛选条件树（tag-filter-tree spec D4：会话态不落盘、重开图库清空；初始空根 And）。
    // 单棵可变树 + 编辑后全量重应用/重建 UI（demo refreshAll 同构）；「有有效条件」以
    // CollectReferencedTags 非空判定（空 Values 条件 = 未启用不约束，不贡献引用）。
    private readonly FilterGroupNode _filterRoot = new();

    /// <summary>
    /// 「无标签」筛选是否激活（untagged-filter-entry，与条件树互斥——spec D4）：
    /// 激活时瀑布流只显示无任何标签的图片（内存谓词 MatchesUntagged）；
    /// 树任何编辑（QuickAdd 成功/面板编辑入口）自动清本位；激活即清树（ToggleUntagged）。
    /// </summary>
    [ObservableProperty]
    private bool _isUntaggedFilterActive;

    // 最近一次索引标签计数快照（侧栏计数与编辑对话框影响张数的共享数据源）。
    private IReadOnlyDictionary<string, int> _latestTagCounts = new Dictionary<string, int>();

    private readonly WaterfallViewModel _waterfall;
    private TagSidebarViewModel? _tagSidebar;
    private ISettingsService? _settingsService;

    /// <summary>UI 线程调度器（构造捕获；侧栏重建等 UI 写操作从后台路径回投）。</summary>
    private readonly DispatcherQueue? _dispatcher;

    /// <summary>
    /// 扫描期间标签计数刷新间隔（毫秒；2026-09-17 走查修复：TagCounts 为全表聚合，大库扫描中
    /// 1.5s 一刷会持续占用 UI 线程重建侧栏，输入明显卡顿；放宽到 5s，扫描结束仍有终态强刷）。
    /// </summary>
    private const int TagDataRefreshIntervalMs = 5000;

    /// <summary>扫描 UI 追加合并的批量阈值（项）。</summary>
    private const int UiFlushBatchSize = 2000;

    /// <summary>扫描 UI 追加合并的时间阈值（毫秒）。</summary>
    private const int UiFlushIntervalMs = 300;

    public MainViewModel(
        IFileBrowserService fileBrowser,
        IImageLoaderService imageLoader,
        IFileOperationService fileOperations,
        ITagFilenameService tagFilename,
        ITagService tagService,
        ILibraryScanService scanService,
        IThumbnailService thumbnailService)
    {
        _fileBrowser = fileBrowser;
        _imageLoader = imageLoader;
        _fileOperations = fileOperations;
        _tagFilename = tagFilename;
        _tagService = tagService;
        _scanService = scanService;
        _thumbnailService = thumbnailService;

        // 瀑布流薄壳（Step 8）：项包装与集合通知在彼处，卡片交互转发回本类。
        _waterfall = new WaterfallViewModel(this, thumbnailService);
        _waterfall.ItemsChanged += OnWaterfallItemsChanged;

        // 构造发生于 UI 线程（App.OnLaunched）；后台路径回投 UI 用。
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>瀑布流视图模型（虚拟化数据源与卡片项）。</summary>
    public WaterfallViewModel Waterfall => _waterfall;

    /// <summary>标签栏视图模型（惰性创建；由 MainWindow 经 AttachSettingsService 激活配置持久化）。</summary>
    public TagSidebarViewModel TagSidebar => _tagSidebar ??= new TagSidebarViewModel(this);

    /// <summary>
    /// 注入设置服务（MainWindow 构造时调用；构造签名保持不变以稳定 App 组装）。
    /// 激活标签/组配置的读写持久化。
    /// </summary>
    public void AttachSettingsService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }


    /// <summary>宿主提供的单图文件选择器（WinRT）；由 <see cref="MainWindow"/> 注入。</summary>
    public Func<Task<string?>>? PickImageFileAsync { get; set; }

    /// <summary>宿主提供的标签目录选择器（单图详情右栏「＋」；含快捷键屏蔽，由 MainWindow 注入）。</summary>
    public Func<Task>? ShowTagCatalogAsync { get; set; }

    /// <summary>宿主提供的图库根目录选择器（FolderPicker）；由 <see cref="MainWindow"/> 注入。</summary>
    public Func<Task<string?>>? PickLibraryFolderAsync { get; set; }

    /// <summary>宿主提供的删除确认对话框；返回 true 表示继续删除。</summary>
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>
    /// 宿主提供的未定义标签连锁删除确认对话框（batch-tag-management Step 3，MainWindow 注入）：
    /// 文案含影响张数与不可逆提示；返回 true 表示继续删除。
    /// </summary>
    public Func<string, int, Task<bool>>? ConfirmUndefinedDeleteAsync { get; set; }

    /// <summary>
    /// 宿主提供的收纳目标组选择对话框（batch-tag-management Step 3，MainWindow 注入）：
    /// 返回 (GroupId, Error)——GroupId 非 null = 确认收纳；GroupId null = 取消；
    /// Error 非 null = 对话框侧拒绝（重名即时提示等，由对话框内自行回显，执行侧不再弹）。
    /// </summary>
    public Func<string, Task<(string? GroupId, string? Error)>>? PickAbsorbGroupAsync { get; set; }

    /// <summary>宿主提供的设置对话框打开回调。</summary>
    public Func<Task>? OpenSettingsAsync { get; set; }

    /// <summary><see cref="IsFullscreen"/> 变化时通知窗口切换 chrome。</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>
    /// 当前单图被"同图改名"（打标重命名，图片字节与 ImageSource 均不变）时通知（旧路径, 新路径）。
    /// 视图据此保持缩放/平移交互态——仅把交互态归属路径追到新路径，不视为切图。
    /// 与 <see cref="EnsureFullResolutionAsync"/> 确立的"同图换源不重置交互态"互补：那次是路径不变源变，
    /// 这次是路径变源不变（先例判据 CurrentImagePath 相等对改名场景天然失效，故需显式通知）。
    /// </summary>
    public event Action<string, string>? CurrentImageRenamed;

    /// <summary>用户触发 ExitApp 快捷键时请求退出。</summary>
    public event EventHandler? ExitRequested;

    [ObservableProperty]
    private ImageSource? _imageSource;

    [ObservableProperty]
    private bool _hasImage;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private double _rotationAngle;

    /// <summary>当前查看模式；默认 Gallery（首次启动无图库时显示空态引导）。</summary>
    [ObservableProperty]
    private ViewerMode _currentMode = ViewerMode.Gallery;

    /// <summary>
    /// chrome 遮盖层顶部行总高（工具栏 + InfoBar，含 InfoBar 上下 Margin；2026-09-19 遮挡修复）：
    /// MainWindow 依各行 SizeChanged 写入。画布层浮层（右栏/折叠条）位于 chrome 层之下，
    /// 顶部可点区必须让出这段高度（右栏收起按钮曾被工具栏横行遮盖、鼠标点不到）。
    /// 底部状态栏已移除（2026-09-19），底部避让链（BottomChromeHeight）随之整体删除。
    /// </summary>
    [ObservableProperty]
    private double _topChromeHeight;


    /// <summary>是否已打开图库（选定根目录并启动过扫描）。</summary>
    [ObservableProperty]
    private bool _hasGallery;

    /// <summary>图库扫描是否进行中。</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>图库扫描状态文本（“扫描中 · 已发现 N 张”/“共 N 张”）。</summary>
    [ObservableProperty]
    private string _scanStatusText = string.Empty;

    /// <summary>左栏（标签栏壳）是否处于折叠态。</summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    /// <summary>瀑布流卡片选中数。</summary>
    [ObservableProperty]
    private int _selectedCardCount;

    /// <summary>工具栏「选择」按钮文案（全选 ⇄ 取消全选，随选中态与呈现集切换；六轮用户需求：常用功能入工具栏）。</summary>
    [ObservableProperty]
    private string _selectAllToggleText = "全选";

    /// <summary>批量打标 InfoBar 是否打开（D13：主窗口内嵌回执区；用户关闭经 TwoWay 写回）。</summary>
    [ObservableProperty]
    private bool _isTagFeedbackOpen;

    /// <summary>回执严重级别（进行中 Informational / 部分失败 Warning / 全失败 Error；2026-09-19 成功静默拍板后不再使用 Success）。</summary>
    [ObservableProperty]
    private InfoBarSeverity _tagFeedbackSeverity = InfoBarSeverity.Informational;

    /// <summary>回执标题（操作名，如“添加标签「海」”）。</summary>
    [ObservableProperty]
    private string _tagFeedbackTitle = string.Empty;

    /// <summary>回执主消息（进度文本 / 终态“成功 N 张，失败 M 张”）。</summary>
    [ObservableProperty]
    private string _tagFeedbackMessage = string.Empty;

    /// <summary>批量标签操作是否进行中（进行中显示 ProgressBar）。</summary>
    [ObservableProperty]
    private bool _isTagOperationInProgress;

    /// <summary>批量操作进度（0..1；分批推进）。</summary>
    [ObservableProperty]
    private double _tagOperationProgress;

    // 失败明细（文件名：原因；整体拒绝为纯原因文本）。
    private IReadOnlyList<string> _tagFeedbackDetails = [];

    /// <summary>失败明细（回执区可展开列表；空 = 无失败）。</summary>
    public IReadOnlyList<string> TagFeedbackDetails => _tagFeedbackDetails;

    /// <summary>是否存在失败明细（明细列表可见性）。</summary>
    public bool HasTagFeedbackDetails => _tagFeedbackDetails.Count > 0;

    /// <summary>失败明细列表可见性。</summary>
    public Visibility TagFeedbackDetailsVisibility =>
        HasTagFeedbackDetails ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>批量操作进度条可见性（仅进行中显示）。</summary>
    public Visibility TagOperationProgressVisibility =>
        IsTagOperationInProgress ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>瀑布流空态文案（扫描完成 0 张 / 筛选无命中；由本类在状态切换点设置）。</summary>
    [ObservableProperty]
    private string _waterfallEmptyText = string.Empty;

    /// <summary>
    /// 当前图显示名（剥离方括号标签段的 base 名 + 扩展名；解析失败回退完整文件名）。
    /// 2026-09-19 统一口径：单图模式（底部文件名栏/右栏信息区）与瀑布流卡片
    /// 一律显示剥离名，标签信息由右栏 chips 与卡片角标承载；完整文件名见
    /// <see cref="CurrentFileFullName"/>（tooltip 用）。
    /// </summary>
    [ObservableProperty]
    private string _currentImageDisplayName = string.Empty;

    /// <summary>当前图完整文件名（含方括号标签段；tooltip 用，不直接展示）。</summary>
    [ObservableProperty]
    private string _currentFileFullName = string.Empty;

    /// <summary>当前图文件大小文本（人类可读 KB/MB；单图详情右栏信息行，2026-09-19）。</summary>
    [ObservableProperty]
    private string _currentImageFileSizeText = string.Empty;

    /// <summary>当前图像素尺寸文本（“宽 × 高”；单图详情右栏信息行）。</summary>
    [ObservableProperty]
    private string _currentImageDimensionsText = string.Empty;

    /// <summary>当前图序号文本（“N / 总数”；单图详情右栏信息行）。</summary>
    [ObservableProperty]
    private string _currentImageIndexText = string.Empty;

    /// <summary>
    /// 当前图标签集合（单图详情右栏 chips；2026-09-19 交互重构）：在 UpdateFileNameSegments /
    /// RefreshCurrentAfterRenameAsync / LoadCurrentAsync 的文件名分段计算点同步重建——
    /// 打标走“同图改名”链路（CurrentImageRenamed → RefreshCurrentAfterRenameAsync →
    /// UpdateStatusText → UpdateFileNameSegments），chips 刷新与文件名分段天然同步，不闪不重载。
    /// </summary>
    public ObservableCollection<string> CurrentImageTags { get; } = [];

    public bool CanNavigateImages => _imageFiles.Count > 0;

    /// <summary>当前图库根目录（未打开图库时为 null）。</summary>
    public string? LibraryRootPath => _libraryRootPath;

    /// <summary>
    /// 当前单图路径（无图为 null）。视图以此区分「切图」与「同图分辨率升级」——
    /// 后者换 ImageSource 但须保持缩放/平移交互态（2026-09-19 放大自动全分辨率重解码）。
    /// </summary>
    public string? CurrentImagePath
        => _currentIndex >= 0 && _currentIndex < _imageFiles.Count ? _imageFiles[_currentIndex] : null;

    /// <summary>当前图最近一次加载结果（切图清空；全分辨率升级后指向全分辨率版）。</summary>
    private LoadedImage? _currentLoaded;

    /// <summary>当前图是否已请求过全分辨率升级（每图一次；切图重置，失败不重试）。</summary>
    private bool _fullResLoadedForCurrent;

    /// <summary>
    /// 放大超过解码分辨率时按需全分辨率重解码并原地换源（2026-09-19）：
    /// fit 解码保证平移浏览性能，放大 ≥1.2× 后视口实际需要更多像素，用 decodeSize=null
    /// 重解码原图（LRU 以 decodeSize 为键，两档共存）。换源后 WriteableBitmap 自然尺寸变大但
    /// Uniform 布局渲染尺寸不变——视觉无缝，合成器以更高分辨率纹理采样，放大区细节不再像素化。
    /// </summary>
    public async Task EnsureFullResolutionAsync()
    {
        if (!HasImage || _fullResLoadedForCurrent || _currentLoaded is not { IsGif: false } loaded)
        {
            return;
        }

        // 原图不比当前解码大（小图或已 1:1）：升级无意义。
        if (loaded.DecodedWidth >= loaded.PixelWidth && loaded.DecodedHeight >= loaded.PixelHeight)
        {
            _fullResLoadedForCurrent = true;
            return;
        }

        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _fullResLoadedForCurrent = true;
        try
        {
            var full = await _imageLoader.LoadAsync(path, decodeSize: null, rotationBucket: 0);
            if (!HasImage || !string.Equals(CurrentImagePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // 等待期间用户已切图：结果丢弃（新图自会按需加载）。
            }

            ImageSource = ImageSourceHelper.FromLoadedImage(full);
            _currentLoaded = full;
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog($"[全分辨率升级失败] path={path}", ex);
        }
    }

    /// <summary>
    /// 应用启动参数（文件或 目录+索引）。帮助信息已在 App 侧处理，窗口打开前完成。
    /// </summary>
    public async Task InitializeAsync(LaunchOptions? options)
    {
        if (options is null || options.ShowHelp)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.FilePath))
        {
            await OpenAsync(options.FilePath);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            return;
        }

        var files = _fileBrowser.GetImagesInDirectory(options.DirectoryPath);
        if (files.Count == 0)
        {
            return;
        }

        var index = options.Index.HasValue
            ? _fileBrowser.ResolveLaunchIndex(files, options.Index.Value)
            : 0;

        SetImageList(files, index);
        // CLI 目录模式（-d -i）沿用现有单图路径：进入单图模式，不触发图库扫描（兼容性口径）。
        CurrentMode = ViewerMode.Single;
        await LoadCurrentAsync();
    }

    /// <summary>
    /// 打开单个文件：刷新所在目录列表（自然排序）并显示；单图打开动作进入 Single 模式。
    /// </summary>
    public async Task OpenAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var files = _fileBrowser.GetImagesInDirectory(directory);
        if (files.Count == 0)
        {
            return;
        }

        var index = files.ToList().IndexOf(fullPath);
        if (index < 0)
        {
            index = 0;
        }

        SetImageList(files, index);
        CurrentMode = ViewerMode.Single;
        await LoadCurrentAsync();
    }

    /// <summary>
    /// 更新视口尺寸，解码尺寸变化时重载（适应窗口）。
    /// 打标进行中忽略（InfoBar 开关引发布局抖动，立即重载会与改名竞态；见字段区注释），
    /// 置待补标志，打标收口（ClearTagOperationRunning）用最后尺寸补判定。
    /// </summary>
    public async Task OnViewportSizeChangedAsync(int width, int height)
    {
        _lastViewportWidth = width;
        _lastViewportHeight = height;
        if (_isTagOperationRunning)
        {
            _pendingDecodeSizeRefresh = true;
            return;
        }

        var newDecodeSize = CalculateDecodeSize(width, height);
        if (newDecodeSize == _decodeSize)
        {
            return;
        }

        _decodeSize = newDecodeSize;
        if (!HasImage || _currentIndex < 0)
        {
            return;
        }

        if (_lastAppliedDecodeSize == (_decodeSize ?? 0))
        {
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>
    /// 打标操作收口：清运行标志并补判定挂起的视口尺寸变化（打标期间布局抖动被忽略，
    /// 此时路径已同步、解码重载安全；无挂起则无动作）。
    /// </summary>
    private void ClearTagOperationRunning()
    {
        _isTagOperationRunning = false;
        if (_pendingDecodeSizeRefresh)
        {
            _pendingDecodeSizeRefresh = false;
            _ = OnViewportSizeChangedAsync(_lastViewportWidth, _lastViewportHeight);
        }
    }

    [RelayCommand]
    private async Task OpenFilePickerAsync()
    {
        if (PickImageFileAsync is null)
        {
            return;
        }

        var path = await PickImageFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenAsync(path);
        }
    }

    /// <summary>
    /// 打开图库：经宿主回调选取根目录 → 切换到图库模式并后台启动递归扫描。
    /// 扫描仅由本命令与「上次图库自动恢复」触发（CLI/单图直开绝不扫描，保冷启动口径）。
    /// </summary>
    [RelayCommand]
    private async Task OpenLibraryAsync()
    {
        if (PickLibraryFolderAsync is null)
        {
            return;
        }

        var folder = await PickLibraryFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await OpenLibraryRootAsync(folder);
    }

    /// <summary>
    /// 打开指定根目录的图库并记录到设置（LastLibraryRoot，供下次启动自动恢复）。
    /// </summary>
    public async Task OpenLibraryRootAsync(string folder)
    {
        try
        {
            var settings = _settingsService?.Load();
            if (settings is not null && !string.Equals(settings.LastLibraryRoot, folder, StringComparison.OrdinalIgnoreCase))
            {
                settings.LastLibraryRoot = folder;
                _settingsService!.Save(settings);
            }
        }
        catch
        {
            // 记录失败不阻断打开图库。
        }

        await StartLibraryScanAsync(folder);
    }

    /// <summary>
    /// 从瀑布流进入单图模式（Step 8 双击卡片入口）：加载目标图片并切换为 Single。
    /// 单图翻页列表 = 当前呈现集（Step 11：筛选态下上一张/下一张在命中集内环绕循环，
    /// 对齐 demo openViewer/viewerStep 的 filteredImages 语义）；呈现集为空时退回目录平铺。
    /// </summary>
    public async Task OpenImageAsSingle(GalleryItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        var presentedPaths = _waterfall.Items
            .Select(vm => vm.Item.Path)
            .ToList();
        if (presentedPaths.Count == 0)
        {
            await OpenAsync(item.Path);
            return;
        }

        var index = -1;
        for (var i = 0; i < presentedPaths.Count; i++)
        {
            if (string.Equals(presentedPaths[i], item.Path, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        SetImageList(presentedPaths, index < 0 ? 0 : index);
        CurrentMode = ViewerMode.Single;
        await LoadCurrentAsync();
    }

    /// <summary>
    /// 启动/重启图库递归扫描：取消并等待既有扫描彻底收尾、按根目录重建索引服务、后台消费扫描流。
    /// 竞态收口（cr/P1-1）：重开图库时旧扫描续体曾与新扫描交错——旧 catch 覆盖新状态行、
    /// finally 对已 Dispose 的索引服务刷新计数抛 ObjectDisposedException（全局未处理异常）、
    /// _galleryItems 并发 Clear/Add。现先 Cancel + await 旧任务（吞异常）再清理资源，
    /// 并以 _scanGeneration 代次丢弃仍可能迟到的 UI 回投续体（Progress 回调入队早于任务完成）。
    /// </summary>
    private async Task StartLibraryScanAsync(string root)
    {
        SimpleViewer.Services.DiagnosticTrace.Mark($"scan:start {root}");

        // 旧扫描收尾（cr/P1-1）：Cancel 后等待其 catch/finally 全部执行完毕，才 Dispose 令牌与
        // 索引服务——旧任务自行收敛终态（"已取消"文案/终态计数刷新），不再污染即将开始的新扫描。
        if (_scanTask is not null)
        {
            _scanCts?.Cancel();
            try
            {
                await _scanTask;
            }
            catch
            {
                // 旧扫描主体的异常已由其 catch 块收敛；此处仅防御 finally 段刷新的意外逃逸。
            }
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var generation = ++_scanGeneration;

        _libraryRootPath = root;
        HasGallery = true;
        CurrentMode = ViewerMode.Gallery;

        // 索引服务按根目录派生库文件名（根目录后建，可延迟创建——D2 口径）；
        // 局部变量捕获，避免扫描中被替换/释放的实例与后台任务竞态。
        var indexService = new LibraryIndexService(root, scanService: _scanService);
        _indexService?.Dispose();
        _indexService = indexService;

        lock (_galleryItemsLock)
        {
            _galleryItems.Clear();
        }
        ClearCardSelection();
        TagFilterState.Clear(_filterRoot); // 筛选会话态：重开图库清空（spec D4，不落盘）
        IsUntaggedFilterActive = false; // 重开图库退出无标签筛选（对齐条件树清空口径）
        _latestTagCounts = new Dictionary<string, int>();
        _waterfall.ResetFrom([]);
        WaterfallEmptyText = string.Empty;
        IsScanning = true;
        ScanStatusText = "扫描中 · 已发现 0 张";
        OnPropertyChanged(nameof(FilterBarVisibility));
        OnPropertyChanged(nameof(FilterStatsText));

        // Progress 构造于 UI 线程：Report 回调自动回投 UI 线程（仅更新状态文本与渐进追加瀑布流）；
        // 回调内代次校验（cr/P1-1）：旧扫描已入队的 Report 在新扫描启动后才回投 UI 时静默丢弃，
        // 避免旧扫描的状态行覆盖与旧块（可能来自另一目录）追加进新瀑布流。
        var progress = new Progress<int>(count =>
        {
            if (generation == _scanGeneration)
            {
                ScanStatusText = $"扫描中 · 已发现 {count} 张";
            }
        });
        IProgress<IReadOnlyList<GalleryItem>> chunkProgress =
            new Progress<IReadOnlyList<GalleryItem>>(chunk =>
            {
                if (generation == _scanGeneration)
                {
                    _waterfall.AppendChunkFromScan(chunk);
                }
            });

        _scanTask = RunLibraryScanAsync(root, indexService, token, generation, progress, chunkProgress);
        await _scanTask;
    }

    /// <summary>
    /// 扫描主体（cr/P1-1 自 StartLibraryScanAsync 拆出，供 _scanTask 句柄跟踪取消与收尾）：
    /// 清空索引缓存、后台消费扫描流、终态收敛（状态行/终态计数刷新/IsScanning 复位）。
    /// </summary>
    private async Task RunLibraryScanAsync(
        string root,
        ILibraryIndexService indexService,
        CancellationToken token,
        int generation,
        IProgress<int> progress,
        IProgress<IReadOnlyList<GalleryItem>> chunkProgress)
    {
        // 索引缓存全量重建（2026-09-17 走查修复）：同一根目录复用旧库文件时，
        // 上一轮的孤儿行（打标改名前的旧 path）会污染候选集与标签计数；
        // 事实源是文件名，每次打开图库即清表重灌。
        // 扫描期间标签计数节流刷新（TagCounts 全表聚合较重，不宜每块刷）。
        var lastTagRefresh = Stopwatch.StartNew();

        try
        {
            // 清表在 try 内（cr/P2-1）：索引目录只读等 IO 异常不再从 try 外逃逸——
            // 统一收敛为"扫描失败"状态行（finally 终态刷新 + IsScanning 复位照常执行，
            // 进程不崩溃，用户仍可再打开其它图库）。
            await indexService.ClearAllItemsAsync(token);

            // ScanAsync 为同步磁盘 IO 迭代器（MoveNextAsync 在消费线程上同步执行磁盘枚举）：
            // 必须 Task.Run 后台消费，UI 线程只收进度/扫描块——几十万张不假死的硬性口径。
            await Task.Run(async () =>
            {
                var chunk = new List<GalleryItem>(LibraryScanService.ChunkSize);
                // UI 追加合并缓冲（2026-09-17 走查修复）：瀑布流追加是 UI 线程操作，
                // 大库扫描中每 500 项一报会高频触发集合通知与布局，挤压输入响应；
                // 攒到 2000 项或距上次投递超 300ms 才 Report 一次（终块在循环外兜底冲刷）。
                var uiBuffer = new List<GalleryItem>(UiFlushBatchSize);
                var uiFlushWatch = Stopwatch.StartNew();
                await foreach (var item in _scanService.ScanAsync(root, progress, token))
                {
                    // 全量暂存 List + 分块 upsert 索引；瀑布流经 chunkProgress 渐进追加（UI 线程）。
                    // 后台线程写：与 UI 侧筛选管线快照读取（SnapshotGalleryItems）经锁互斥
                    // （tag-filter-tree Step 6 扫描中面板编辑实时生效的竞态收口；无竞争锁纳秒级）。
                    lock (_galleryItemsLock)
                    {
                        _galleryItems.Add(item);
                    }
                    chunk.Add(item);
                    uiBuffer.Add(item);
                    if (chunk.Count >= LibraryScanService.ChunkSize)
                    {
                        await indexService.UpsertChunkAsync(chunk, token);
                        chunk.Clear();

                        if (uiBuffer.Count >= UiFlushBatchSize
                            || (uiBuffer.Count > 0 && uiFlushWatch.ElapsedMilliseconds >= UiFlushIntervalMs))
                        {
                            // Report 引用会被异步消费，复用 List 前必须快照。
                            chunkProgress.Report(uiBuffer.ToArray());
                            uiBuffer.Clear();
                            uiFlushWatch.Restart();
                        }

                        if (lastTagRefresh.ElapsedMilliseconds >= TagDataRefreshIntervalMs)
                        {
                            lastTagRefresh.Restart();
                            await RefreshTagDataAsync(indexService);
                        }
                    }
                }

                if (chunk.Count > 0)
                {
                    await indexService.UpsertChunkAsync(chunk, token);
                }

                if (uiBuffer.Count > 0)
                {
                    chunkProgress.Report(uiBuffer.ToArray());
                    uiBuffer.Clear();
                }
            }, token);

            // 代次校验（cr/P1-1）：扫描期间被重开取代时不再写状态行——新扫描 owns 终态文案。
            if (generation != _scanGeneration)
            {
                return;
            }

            ScanStatusText = $"共 {_galleryItems.Count} 张";
            SimpleViewer.Services.DiagnosticTrace.Mark($"scan:end {_galleryItems.Count}");
            if (_galleryItems.Count == 0)
            {
                WaterfallEmptyText = "未在所选目录发现图片";
            }
        }
        catch (OperationCanceledException)
        {
            // 代次校验（cr/P1-1）：被新扫描取代引发的取消不写状态行（旧 catch 曾覆盖新扫描文案）。
            if (generation == _scanGeneration)
            {
                ScanStatusText = $"扫描已取消 · 已发现 {_galleryItems.Count} 张";
            }
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration)
            {
                ScanStatusText = $"扫描失败：{ex.Message}";
            }
        }
        finally
        {
            // 结束态（成功/取消/失败）统一做一次终态计数刷新，保证侧栏计数与索引一致。
            // 代次校验（cr/P1-1）：已被更新扫描取代时静默跳过——避免旧库数据覆盖新扫描计数。
            if (generation == _scanGeneration)
            {
                try
                {
                    await RefreshTagDataAsync(indexService);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // 防御（cr/P1-1）：索引服务已释放/库文件异常时不上抛——终态刷新失败仅记诊断日志，
                    // 不能成为全局未处理异常（自动恢复启动后立即手动重开图库的竞态场景）。
                    App.WriteDiagnosticLog($"[标签计数终态刷新失败] root={root}", ex);
                }

                IsScanning = false;
            }
        }
    }

    /// <summary>返回瀑布流（单图 → 图库模式切换，保持图库状态与滚动位置语义由 Step 8 落实）。</summary>
    [RelayCommand(CanExecute = nameof(HasGallery))]
    private void BackToGallery()
    {
        if (!HasGallery)
        {
            return;
        }

        CurrentMode = ViewerMode.Gallery;
    }

    /// <summary>折叠/展开左栏（标签栏壳）。</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    /// <summary>单图详情右栏是否处于折叠态（默认展开；仿左栏 IsSidebarCollapsed 先例）。</summary>
    [ObservableProperty]
    private bool _isInfoPanelCollapsed;

    /// <summary>折叠/展开单图详情右栏（仅单图模式可见，随 SingleImageView 宿主显隐）。</summary>
    [RelayCommand]
    private void ToggleInfoPanel()
    {
        IsInfoPanelCollapsed = !IsInfoPanelCollapsed;
    }

    /// <summary>
    /// 图库右栏（选中集标签面板）是否处于折叠态（默认展开；batch-tag-management Step 4，
    /// 全仿 <see cref="IsInfoPanelCollapsed"/> 先例）。仅图库模式可见——面板宿主随 GalleryVisibility
    /// 显隐，单图模式天然隐藏，本状态跨模式保持（回图库恢复原收展态）。
    /// </summary>
    [ObservableProperty]
    private bool _isGallerySelectionPanelCollapsed;

    /// <summary>折叠/展开图库右栏（选中集标签面板）。</summary>
    [RelayCommand]
    private void ToggleGallerySelectionPanel()
    {
        IsGallerySelectionPanelCollapsed = !IsGallerySelectionPanelCollapsed;
    }

    /// <summary>
    /// Esc 三态路由（D6）：单图模式且有图库 → 返回瀑布流并返回 true；
    /// 瀑布流模式且有选中集 → 清空选中并返回 true；
    /// 其余返回 false，由调用方维持 ExitApp 原行为。
    /// </summary>
    public bool TryRouteEscape()
    {
        if (CurrentMode == ViewerMode.Single && HasGallery)
        {
            if (BackToGalleryCommand.CanExecute(null))
            {
                BackToGalleryCommand.Execute(null);
            }

            return true;
        }

        if (CurrentMode == ViewerMode.Gallery && SelectedCardCount > 0)
        {
            ClearCardSelection();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 卡片点击入口（WaterfallView 转发，携带 Ctrl/Shift 修饰键状态；2026-09-19 Explorer 心智重构）：
    /// 无修饰 = 单选重置（清空后仅选该卡；点已选中卡 = 清空后重选自己，保持选中）；
    /// Ctrl = 加/减选切换（toggle）；
    /// Shift = 范围重置（清空后选锚点到目标卡片的呈现序列范围）；
    /// 锚点随每次点击更新；锚点或目标不在呈现集中时范围退化为切换。
    /// </summary>
    public void HandleCardTapped(GalleryItemViewModel viewModel, bool ctrl, bool shift)
    {
        if (viewModel is null)
        {
            return;
        }

        if (ctrl)
        {
            ToggleCardSelection(viewModel);
        }
        else if (shift && _lastClickedCard is not null)
        {
            SelectCardRange(_lastClickedCard, viewModel);
        }
        else
        {
            SelectSingleCard(viewModel);
        }

        _lastClickedCard = viewModel;
    }

    /// <summary>
    /// 无修饰点击的单选重置：清空已选后仅选中该卡（点已选中卡 = 清空后重选自己 = 保持选中）。
    /// </summary>
    private void SelectSingleCard(GalleryItemViewModel viewModel)
    {
        ClearCardSelection();
        _selectedCards.Add(viewModel);
        viewModel.IsSelected = true;
        SelectedCardCount = _selectedCards.Count;
    }

    /// <summary>
    /// Shift 范围重置：清空已选后选中锚点到目标卡片之间（当前呈现序列索引范围）的全部卡片；
    /// 范围不可解析（锚点/目标不在呈现集中）时退化为切换。
    /// </summary>
    private void SelectCardRange(GalleryItemViewModel anchor, GalleryItemViewModel target)
    {
        var items = _waterfall.Items;
        var anchorIndex = -1;
        var targetIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], anchor))
            {
                anchorIndex = i;
            }

            if (ReferenceEquals(items[i], target))
            {
                targetIndex = i;
            }
        }

        if (anchorIndex < 0 || targetIndex < 0)
        {
            ToggleCardSelection(target);
            return;
        }

        // 范围重置语义（2026-09-19 Explorer 心智）：先清空旧选中，再选整个范围
        //（此前为纯加选不清旧，多选意图只能靠 Ctrl 逐张累积）。
        ClearCardSelection();
        for (var i = Math.Min(anchorIndex, targetIndex); i <= Math.Max(anchorIndex, targetIndex); i++)
        {
            _selectedCards.Add(items[i]);
            items[i].IsSelected = true;
        }

        SelectedCardCount = _selectedCards.Count;
    }

    /// <summary>全选当前呈现集（Ctrl+A：选中“当前命中集”；MainWindow PreviewKeyDown 接线，Step 10）。</summary>
    public void SelectAllCards()
    {
        foreach (var viewModel in _waterfall.Items)
        {
            if (_selectedCards.Add(viewModel))
            {
                viewModel.IsSelected = true;
            }
        }

        SelectedCardCount = _selectedCards.Count;
    }

    /// <summary>当前呈现集是否已全部选中（全选态判定；呈现集为空恒 false）。</summary>
    private bool IsAllCardsSelected
        => _waterfall.Items.Count > 0 && SelectedCardCount >= _waterfall.Items.Count;

    /// <summary>
    /// 工具栏「选择」按钮（六轮用户需求，常用功能）：智能切换——未全选 → 全选当前呈现集
    /// （与 Ctrl+A 同管线）；已全选 → 清空（与 Esc 同管线）。2026-09-19 拍板移除过的
    /// 「清除选择」按钮是图库状态行内的一次性权衡，本按钮为工具栏常驻全选/取消一体入口，口径已由用户新决策覆盖。
    /// </summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (IsAllCardsSelected)
        {
            ClearCardSelection();
        }
        else
        {
            SelectAllCards();
        }
    }

    /// <summary>按选中数与呈现集刷新「选择」按钮文案（全选 ⇄ 取消全选）。</summary>
    private void UpdateSelectAllToggleText()
        => SelectAllToggleText = IsAllCardsSelected ? "取消全选" : "全选";

    /// <summary>
    /// 回车进入单图（PRD 需求 4「双击或回车进入单图」；MainWindow PreviewKeyDown 仿 Ctrl+A 口径接线，
    /// cr/P1-4）：取选中集首项——按当前呈现序找第一张选中卡（选中集为 HashSet 无序，呈现序口径确定）；
    /// 无选中项时取呈现集首项（实现期拍板：无选中 = 取首项，键盘用户从列表头开始浏览的直觉）。
    /// 与卡片双击共用 <see cref="OpenImageAsSingle"/> 既有管线（单图翻页列表 = 当前呈现集快照）。
    /// </summary>
    public Task OpenSelectionAsSingleAsync()
    {
        GalleryItemViewModel? target = null;
        foreach (var viewModel in _waterfall.Items)
        {
            if (_selectedCards.Contains(viewModel))
            {
                target = viewModel;
                break;
            }
        }

        target ??= _waterfall.Items.FirstOrDefault();
        return target is null ? Task.CompletedTask : OpenImageAsSingle(target.Item);
    }

    /// <summary>
    /// Ctrl+点击的加/减选切换（2026-09-19 Explorer 心智：无修饰点击已改为单选重置，toggle 仅归 Ctrl）。
    /// </summary>
    public void ToggleCardSelection(GalleryItemViewModel viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        if (_selectedCards.Remove(viewModel))
        {
            viewModel.IsSelected = false;
        }
        else
        {
            _selectedCards.Add(viewModel);
            viewModel.IsSelected = true;
        }

        SelectedCardCount = _selectedCards.Count;
    }

    /// <summary>
    /// 清空卡片选中集（Esc 路由 / 筛选切换 / 重开图库 / 单选与范围重置的前置清空）。
    /// 图库状态行「清除选择」按钮已移除（2026-09-19 用户权衡：Esc 即清空，按钮冗余）。
    /// </summary>
    public void ClearCardSelection()
    {
        foreach (var viewModel in _selectedCards)
        {
            viewModel.IsSelected = false;
        }

        _selectedCards.Clear();
        SelectedCardCount = 0;
    }

    // ==================== 打标入口与标签筛选（2026-09-19 交互重构） ====================

    /// <summary>批量操作分批粒度（每批一次 TagService 调用；批间 await 回 UI 线程推进进度与就地同步）。</summary>
    private const int TagBatchSize = 25;

    /// <summary>失败明细最多展示条数（超出折叠为“其余 N 项从略”）。</summary>
    private const int MaxFailureDetails = 20;

    /// <summary>
    /// 侧栏标签 chip 点击入口（2026-09-19 交互重构：点击一律 = 筛选；tag-filter-tree：QuickAdd 追加）：
    /// 单图模式下额外切回图库让筛选结果可见
    /// （CLI 直开无图库时保持单图——无索引可查，切回只会看到空态）。
    /// 打标入口已移交：拖拽卡片到标签行 / 单图详情右栏 / 快捷键（ApplyTagByShortcutAsync）；
    /// 批量移除入口 = 图库右栏 chip ✕（RemoveTagFromSelectionAsync，batch-tag-management Step 4
    /// 重启同名方法——语义为选中集批量移除标签，非旧 Shift+点击口径）。
    /// </summary>
    /// <param name="tagName">标签名。</param>
    public void HandleTagChipTapped(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName) || _isTagOperationRunning)
        {
            return;
        }

        if (CurrentMode == ViewerMode.Single && HasGallery)
        {
            CurrentMode = ViewerMode.Gallery;
        }

        ToggleTagFilter(tagName);
    }

    /// <summary>
    /// 快捷键打标入口（Step 12，决策 D7：命令 + TagId 参数）：
    /// 单图模式且有图 → 当前图打标/移除（toggle，复用 Step 10 单图管线）；
    /// 图库模式且选中集非空 → 批量打标（互斥语义走既有 TagSemantics）；选中集为空 → 无操作；
    /// tagId 解析失败（标签已被删除或设置未初始化）→ InfoBar 警告提示重新绑定。
    /// </summary>
    /// <param name="tagId">快捷键绑定引用的标签稳定 Id（<see cref="TagDefinition.Id"/>）。</param>
    public async Task ApplyTagByShortcutAsync(string? tagId)
    {
        if (string.IsNullOrWhiteSpace(tagId) || _isTagOperationRunning)
        {
            return;
        }

        var resolved = FindTagById(tagId);
        if (resolved is null)
        {
            ShowInstantTagFeedback(
                InfoBarSeverity.Warning,
                "打标签",
                "快捷键引用的标签不存在（可能已被删除），请在设置中重新绑定。",
                []);
            return;
        }

        var (group, tagName) = resolved.Value;

        if (CurrentMode == ViewerMode.Single && HasImage)
        {
            await ToggleTagOnCurrentImageAsync(group, tagName);
            return;
        }

        if (CurrentMode == ViewerMode.Gallery && SelectedCardCount > 0)
        {
            await ApplyTagToSelectionAsync(group, tagName);
        }

        // 图库模式且选中集为空：无操作（spec Step 12——空则忽略）。
    }

    /// <summary>
    /// 按稳定 Id 查找标签及其所属配置组（TagDefinition.Id 契约：重命名不改 Id，故引用不受重命名影响）。
    /// 返回 null 表示 Id 不再引用任何已配置标签。
    /// </summary>
    private (TagGroup Group, string TagName)? FindTagById(string tagId)
    {
        var groups = _settingsService?.Load().TagGroups ?? [];
        foreach (var group in groups)
        {
            foreach (var tag in group.Tags)
            {
                if (string.Equals(tag.Id, tagId, StringComparison.Ordinal))
                {
                    return (group, tag.Name);
                }
            }
        }

        return null;
    }

    /// <summary>选中集批量打标（快捷键路径；互斥组按语义替换；打标后选中集保持——卡片 VM 就地更新，路径换新）。
    /// 2026-09-19 交互重构后侧栏点击不再进此方法（cr/P2-2 改 private：唯一调用方 ApplyTagByShortcutAsync 在类内；
    /// 拖拽路径走 ApplyTagToDraggedCardsAsync，共用 ApplyTagToPathsAsync）。</summary>
    private async Task ApplyTagToSelectionAsync(TagGroup group, string tagName)
    {
        if (_selectedCards.Count == 0)
        {
            return;
        }

        await ApplyTagToPathsAsync(
            _selectedCards.Select(static vm => vm.Item.Path).ToList(),
            group,
            tagName);
    }

    // ==================== 拖拽卡片到标签行打标（2026-09-19 交互重构） ====================

    /// <summary>拖拽 payload 暂存（BeginCardDrag 写入，Drop 消费后清空；进程内路径集）。</summary>
    private IReadOnlyList<string>? _dragPayload;

    /// <summary>批量打标/移除防重入标志的只读视图（侧栏拖拽目标 DragOver 判定可接受态用）。</summary>
    public bool IsTagOperationRunning => _isTagOperationRunning;

    /// <summary>
    /// 当前拖拽 payload 的项数（2026-09-19 拖拽视觉：BeginCardDrag 写入、Drop 消费前有效；
    /// 无 payload 时为 0）。侧栏 DragOver 用其显示多选拖拽的计数 caption。
    /// </summary>
    public int DragPayloadCount => _dragPayload?.Count ?? 0;

    /// <summary>
    /// 开始卡片拖拽（WaterfallView.DragStarting 转发）：确定本次拖拽的路径集——
    /// 卡片在选中集内 = 整集（对齐“拖选中集内任一卡 = 整集打标”决策），否则仅该卡。
    /// 此时不改选中集（拖拽是打标手势不是选卡手势）；路径集暂存 VM 侧供 Drop 消费
    /// （DataPackage 只携带格式标记与计数文本，不重复序列化路径——同进程内以 VM 状态为准）。
    /// </summary>
    /// <param name="cardVm">被拖拽的卡片 VM（Tag 槽位回查所得）。</param>
    /// <returns>本次拖拽生效的路径集（空集表示无可打标项，调用方应取消拖拽）。</returns>
    public IReadOnlyList<string> BeginCardDrag(GalleryItemViewModel cardVm)
    {
        _dragPayload = _selectedCards.Contains(cardVm)
            ? _selectedCards.Select(static vm => vm.Item.Path).ToList()
            : [cardVm.Item.Path];
        return _dragPayload;
    }

    /// <summary>
    /// 对拖拽卡片集打标（侧栏标签行 Drop 转发）：消费 <see cref="BeginCardDrag"/> 暂存的路径集，
    /// 走统一批量管线（互斥语义/InfoBar 回执/就地同步/缓存迁移）。空 payload（外部拖入等）忽略。
    /// </summary>
    /// <param name="ownerGroup">标签所属配置组（chip.OwnerGroup 恒非空——侧栏仅展示配置组行，
    /// 2026-09-19 用户拍板移除「未分组」虚拟组后无组行可拖）。</param>
    /// <param name="tagName">标签名。</param>
    public async Task ApplyTagToDraggedCardsAsync(TagGroup? ownerGroup, string tagName)
    {
        var payload = _dragPayload;
        _dragPayload = null;
        if (payload is null || payload.Count == 0)
        {
            return;
        }

        // 2026-09-19 口径：原「未分组虚拟组」兜底（ownerGroup ?? 合成兼容组）随虚拟组移除成死分支已删；
        // 理论上 ownerGroup 必非空，防御性 null（调用方异常构造的 chip）直接忽略本次拖放。
        if (ownerGroup is null)
        {
            return;
        }

        await ApplyTagToPathsAsync(payload, ownerGroup, tagName);
    }

    /// <summary>
    /// 按路径集批量打标/移除（选中集快捷键、拖拽与图库右栏 chip ✕ 共用入口）：路径 → 候选 GalleryItem——
    /// 优先在当前呈现集中查同路径项（宽高/排序 key 继承，瀑布流卡片就地更新不失真）；不在呈现集
    /// （如拖拽中途瀑布流被重置）时解析文件名构造最小候选（未知宽高回退 1:1，索引行由后续对账重建纠正）。
    /// remove 变体（batch-tag-management Step 4）：组参数仅满足签名不消费组语义（RunTagOperationAsync
    /// remove 分支按名移除），调用方传 <see cref="FindGroupByTagName"/> 兜底组；回执标题用移除口径
    /// 「移除标签「X」」（与单图 ✕ :1348 同文案）。
    /// </summary>
    /// <param name="paths">目标路径集。</param>
    /// <param name="group">标签所属配置组（remove 路径仅占位）。</param>
    /// <param name="tagName">标签名。</param>
    /// <param name="remove">true = 移除标签（图库右栏 chip ✕）；false = 添加标签（默认，原行为）。</param>
    private async Task ApplyTagToPathsAsync(
        IReadOnlyList<string> paths, TagGroup group, string tagName, bool remove = false)
    {
        if (paths.Count == 0 || _isTagOperationRunning || string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        var candidates = new List<GalleryItem>(paths.Count);
        foreach (var path in paths)
        {
            var item = FindPresentedItemByPath(path) ?? TryBuildCandidateFromPath(path);
            if (item is not null)
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var title = remove
            ? $"移除标签「{tagName}」"
            : group.Exclusive
                ? $"互斥组设置「{tagName}」"
                : $"添加标签「{tagName}」";

        _isTagOperationRunning = true;
        try
        {
            BeginTagOperation(title, showProgress: true, candidates.Count);
            var (result, sync) = await RunTagOperationAsync(candidates, group, tagName, remove, showProgress: true);
            ShowTagOperationResult(result, sync);
            await RefreshTagDataAsync();
        }
        finally
        {
            ClearTagOperationRunning();
        }
    }

    /// <summary>按路径构造最小候选（文件名解析失败返回 null——目标名冲突或超长等不可打标项）。</summary>
    private GalleryItem? TryBuildCandidateFromPath(string path)
    {
        if (!_tagFilename.TryParse(Path.GetFileName(path), out var baseName, out var extension, out var tags))
        {
            return null;
        }

        return new GalleryItem
        {
            Path = path,
            DirectoryName = Path.GetDirectoryName(path) ?? string.Empty,
            BaseName = baseName,
            Extension = extension,
            Tags = tags,
            SortKey = GalleryItemNaturalComparer.Tokenize(baseName),
        };
    }

    /// <summary>
    /// 单图模式当前图打标（toggle 语义，对齐 demo viewerTags）：
    /// 已含该标签 → 移除；未含 → 打标（互斥组先剔除同组再追加）。
    /// 成功后同图改名不重载：保持 ImageSource/解码缓存与缩放态，仅按新路径重算文件名分段与右栏信息行
    ///（GIF 按 UriSource 特性完整重载一次；见 RefreshCurrentAfterRenameAsync）。
    /// </summary>
    public async Task ToggleTagOnCurrentImageAsync(TagGroup group, string tagName)
    {
        if (_isTagOperationRunning
            || _currentIndex < 0
            || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var path = _imageFiles[_currentIndex];
        if (!_tagFilename.TryParse(Path.GetFileName(path), out var baseName, out var extension, out var tags))
        {
            ShowInstantTagFeedback(
                InfoBarSeverity.Warning,
                "打标失败",
                "当前文件名无法解析（目标名冲突或超长）。",
                []);
            return;
        }

        var remove = tags.Contains(tagName, StringComparer.OrdinalIgnoreCase);

        // 打标方向预检文件名预算（即时拒绝，不进批量管线）：按互斥语义计算打标后的真实新标签集合，
        // 组件名超 Linux 255 UTF-8 字节即拒绝并附超出字节数（批量入口由 BuildNewPath 双口径预检
        // 拦截、失败明细自然带新文案；移除方向只减不加，天然不超，无需预检）。
        if (!remove)
        {
            var newTags = TagSemantics.Apply(tags, group, tagName);
            var budgetError = TagFilenameBudget.CheckFileNameBudget(baseName, extension, newTags);
            if (budgetError is not null)
            {
                ShowInstantTagFeedback(
                    InfoBarSeverity.Warning,
                    "打标失败",
                    budgetError + "可缩短标签名或改用更短的基名后重试。",
                    []);
                return;
            }
        }

        // 候选优先取呈现集中同路径项（宽高/排序 key 继承，瀑布流卡片就地更新不失真）；
        // 不在呈现集（如 CLI 直开）时解析文件名构造最小候选（上方 TryParse 已成功，必非 null）。
        var candidate = FindPresentedItemByPath(path) ?? TryBuildCandidateFromPath(path)!;

        _isTagOperationRunning = true;
        try
        {
            var title = remove ? $"移除标签「{tagName}」" : $"添加标签「{tagName}」";
            BeginTagOperation(title, showProgress: false, totalCount: 1);
            var (result, sync) = await RunTagOperationAsync(
                [candidate], group, tagName, remove, showProgress: false);
            ShowTagOperationResult(result, sync);
            await RefreshTagDataAsync();

            // 改名成功：同图改名不重载——图片字节未变，保持 ImageSource/_currentLoaded/缩放态
            //（2026-09-19 管线修复：旧实现走 LoadCurrentAsync，先置空 ImageSource 再按新路径
            // 全量重解码，造成闪空、解码缓存 miss 与缩放复位）。仅按新路径重算文件名分段与右栏信息行；
            // 解码缓存已在 SyncRenamedItemsAsync 阶段二迁移到新路径键（翻页回来命中）。
            if (sync.Synced > 0)
            {
                await RefreshCurrentAfterRenameAsync();
            }
        }
        finally
        {
            ClearTagOperationRunning();
        }
    }

    // ==================== 单图详情右栏（2026-09-19 交互重构：信息行 + 标签管理） ====================

    /// <summary>
    /// 移除当前图的一个标签（右栏 chip 的 ✕）：按标签名解析所属配置组（未命中 = 兜底组）后走单图
    /// toggle 管线——当前图必含该标签（chips 即当前标签集），toggle 即移除。
    /// 2026-09-19 用户拍板：右栏 chips 保留显示全部标签（含无组）且 ✕ 可移除——这是清理文件名中
    /// 无组脏数据的<b>唯一 UI 出口</b>（有意偏差 demo，demo 也跳过无组）；remove 分支按名操作文件、
    /// 不消费组语义，故兜底组仅作 toggle 管线的非空参数（见 <see cref="FindGroupByTagName"/>）。
    /// </summary>
    /// <param name="tagName">标签名。</param>
    public async Task RemoveCurrentImageTagAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        await ToggleTagOnCurrentImageAsync(FindGroupByTagName(tagName), tagName);
    }

    /// <summary>
    /// 打开标签目录选择器（右栏「＋」按钮）：宿主回调（MainWindow 注入 ShowTagCatalogAsync，
    /// ContentDialog + 快捷键屏蔽）展示两级行列表，点选标签走单图打标管线后关闭。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task OpenTagCatalogAsync()
    {
        if (ShowTagCatalogAsync is not null)
        {
            await ShowTagCatalogAsync();
        }
    }

    /// <summary>
    /// 按标签名解析所属配置组（配置 TagGroups 查名字；跨组重名已被校验拒绝，名字唯一）。
    /// 未命中（文件名中的无组脏标签）返回兜底兼容组——<b>必须保留</b>（2026-09-19 用户拍板）：
    /// 右栏 chips ✕ 传入的 toggle 管线需要非空 group；remove 分支按名操作文件、不消费组语义，
    /// 故兜底组名纯占位。无组标签的归组途径 = 在配置组内新建同名标签（按名匹配自然「收编」）。
    /// </summary>
    private TagGroup FindGroupByTagName(string tagName)
        => (_settingsService?.Load().TagGroups ?? []).FirstOrDefault(
               g => g.Tags.Any(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)))
            ?? new TagGroup
            {
                Name = "未分组",
                Exclusive = false,
            };

    // ==================== 未定义标签区执行管线（batch-tag-management Step 3：连锁删除 D3 / 收纳 D4） ====================

    /// <summary>
    /// 未定义标签连锁删除（D3，spec Step 3）：确认对话框（宿主回调，含影响张数与不可逆提示）→
    /// <see cref="ILibraryIndexService.QueryByTagsAsync"/> 取候选 → 空候选直接返回（计数竞态兜底）→
    /// 候选转 GalleryItem（照 <see cref="ApplyTagToPathsAsync"/> 的 FindPresentedItemByPath 口径）→
    /// 批量移除统一管线（RunTagOperationAsync remove:true，分批 25/就地同步/回执口径与批量打标一致）→
    /// 计数刷新。未定义标签必不在配置组——组参数用 <see cref="FindGroupByTagName"/> 兜底组占位
    /// （remove 分支按名操作文件、不消费组语义）。
    /// </summary>
    /// <param name="tagName">未定义标签名（计数键拼写）。</param>
    public async Task RemoveTagFromLibraryAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName) || _isTagOperationRunning)
        {
            return;
        }

        // 确认对话框：影响张数取计数快照（chip 上的计数同源，确认口径与所见一致）；
        // 宿主未注入回调（组装期防御）视为未确认直接返回。
        if (ConfirmUndefinedDeleteAsync is null
            || !await ConfirmUndefinedDeleteAsync(tagName, GetTagCount(tagName)))
        {
            return;
        }

        // 无索引守卫（对齐 RenameFilesAsync 现口径）：未开图库时未定义区本就为空（计数快照
        // 来自索引聚合），天然不触发；此处防御返回 + InfoBar 提示而非静默失败。
        if (_indexService is null)
        {
            ShowInstantTagFeedback(
                InfoBarSeverity.Warning,
                $"移除标签「{tagName}」",
                "尚未打开图库（无索引可用）。",
                []);
            return;
        }

        var queried = await _indexService.QueryByTagsAsync([tagName]);
        if (queried.Count == 0)
        {
            return; // 计数竞态兜底：确认期间引用已被其他路径清空，无需动作。
        }

        // 候选转换（ApplyTagToPathsAsync 同口径）：优先呈现集中同路径项（宽高/排序 key 继承，
        // 瀑布流卡片就地更新不失真）；索引行本身即完整 GalleryItem，未命中呈现集时直接可用。
        var candidates = new List<GalleryItem>(queried.Count);
        foreach (var queriedItem in queried)
        {
            candidates.Add(FindPresentedItemByPath(queriedItem.Path) ?? queriedItem);
        }

        _isTagOperationRunning = true;
        try
        {
            BeginTagOperation($"移除标签「{tagName}」", showProgress: true, candidates.Count);
            var (result, sync) = await RunTagOperationAsync(
                candidates, FindGroupByTagName(tagName), tagName, remove: true, showProgress: true);
            ShowTagOperationResult(result, sync);
            await RefreshTagDataAsync();
        }
        finally
        {
            ClearTagOperationRunning();
        }
    }

    /// <summary>
    /// 未定义标签收纳入口（D4 编排，侧栏 chip「收纳进组」命令目标）：弹目标组选择对话框
    /// （宿主回调，重名在对话框内即时提示）→ 取消/对话框侧拒绝则返回 → 执行配置层收纳。
    /// 执行侧错误（竞态防御，正常流程不可达）经 InfoBar 回显。
    /// </summary>
    /// <param name="tagName">未定义标签名（计数键拼写）。</param>
    public async Task AbsorbUndefinedTagAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        if (PickAbsorbGroupAsync is null)
        {
            return; // 宿主未注入回调（组装期防御）。
        }

        var (groupId, dialogError) = await PickAbsorbGroupAsync(tagName);
        if (dialogError is not null)
        {
            // 对话框侧已回显（如重名即时提示），此处不重复弹。
            return;
        }

        if (groupId is null)
        {
            return; // 用户取消。
        }

        var error = await AbsorbUndefinedTagAsync(tagName, groupId);
        if (error is not null)
        {
            ShowInstantTagFeedback(
                InfoBarSeverity.Warning,
                "收纳标签失败",
                error,
                []);
        }
    }

    /// <summary>
    /// 未定义标签收纳执行（D4）：Load settings → 找目标组 → 前置重名检查（任何组已有同名标签即拒
    /// ——对齐 <see cref="SettingsService.ValidateTagGroups"/> 的组内/跨组重名口径，未前置则 Save 时
    /// 被校验以更生硬文案拒绝）→ 组内新建 TagDefinition（新 Id 生成方式照 ExecuteAddTag 现状）→
    /// <see cref="SaveSettingsAndRebuildSidebar"/>。0 文件改名（收编语义：按名匹配配置自然生效）。
    /// 返回 null = 成功；非 null = 拒绝文案。
    /// </summary>
    /// <param name="tagName">未定义标签名（计数键拼写）。</param>
    /// <param name="targetGroupId">目标配置组 Id（对话框选定）。</param>
    public async Task<string?> AbsorbUndefinedTagAsync(string tagName, string targetGroupId)
    {
        if (_settingsService is null)
        {
            return "设置服务未初始化。";
        }

        // 纯配置操作无 await 点，保持 async 签名对齐编排入口（对话框确认后的续体语义）。
        await Task.CompletedTask;
        var settings = _settingsService.Load();
        var group = FindGroup(settings.TagGroups, targetGroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        // 前置重名检查（B3）：查全部组而非仅目标组——ValidateTagGroups 拒绝跨组重名，只查目标组
        // 会把拒绝推迟到 Save 时以「跨组标签重名」文案弹出。未定义标签理论上必不在任何组
        // （投影差集已排除），此检查是对话框快照与当前配置竞态的防御。
        var conflict = settings.TagGroups.FirstOrDefault(g => g.Tags.Any(t =>
            string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)));
        if (conflict is not null)
        {
            return $"组「{conflict.Name}」已存在同名标签「{tagName}」，文件名标签不允许跨组重名。";
        }

        group.Tags.Add(new TagDefinition { Id = Guid.NewGuid().ToString("N"), Name = tagName });
        return SaveSettingsAndRebuildSidebar(settings);
    }

    // ==================== 图库右栏：选中集标签并集（batch-tag-management Step 4，D6） ====================

    /// <summary>
    /// 图库右栏并集 chip 集合：选中集全部图片标签的并集（含未定义标签——C2 数据层），
    /// 计数 = 选中集内含该标签的图片数（如 30 张中 18 张含 Z → 「Z 18」）。计数降序、
    /// 同计数按名 Ordinal（TagProjection 排序口径）；随选中集变化与标签操作收口全量重建（UI 线程）。
    /// </summary>
    public ObservableCollection<TagCountEntry> SelectionTagUnion { get; } = [];

    /// <summary>选中集并集是否非空（右栏 chip 区/空态文案互斥可见性）。</summary>
    public bool HasSelectionTags => SelectionTagUnion.Count > 0;

    /// <summary>右栏标题行文本：「已选 N 张」（随选中数刷新）。</summary>
    public string SelectionPanelTitleText => $"已选 {SelectedCardCount} 张";

    /// <summary>
    /// 全量重算选中集标签并集（D6）：<see cref="TagProjection.ComputeSelectionTagUnion"/> 纯函数 →
    /// 集合先清后加重建。锁不需要——<see cref="_selectedCards"/> 只在 UI 线程动；容忍
    /// SelectSingleCard/SelectCardRange 先清后加的 0→n 中间态通知（重算幂等，中间态闪变可接受，D6）。
    /// 三个挂点（改标签的所有路径汇入这些收口；<b>不在</b> ReplaceGalleryItemState 内部逐文件触发
    /// ——防 N 次重算放大 O(N²)，D6 拍板）：
    /// ① <see cref="OnSelectedCardCountChanged"/>——选中集增减（点选/Ctrl/Shift/Ctrl+A/Esc 清空）；
    /// ② <see cref="RunTagOperationAsync"/> 返回前——批量打标/移除统一管线收口（单图 ✕、选中集快捷键
    ///    打标、拖拽打标、图库右栏 chip ✕、未定义区连锁删全汇入），完成后一次；
    /// ③ <see cref="RenameFilesAsync"/> 尾部——重命名标签连锁改标签，完成后一次。
    /// </summary>
    public void RecomputeSelectionTagUnion()
    {
        SelectionTagUnion.Clear();
        foreach (var entry in TagProjection.ComputeSelectionTagUnion(
                     _selectedCards.Select(static vm => vm.Item.Tags)))
        {
            SelectionTagUnion.Add(entry);
        }

        OnPropertyChanged(nameof(HasSelectionTags));
    }

    /// <summary>
    /// 图库右栏 chip ✕ 批量移除（C1）：选中集中 <see cref="Models.GalleryItem.Tags"/> 含该标签
    /// （OrdinalIgnoreCase）的文件路径 → <see cref="ApplyTagToPathsAsync"/> remove 变体（统一批量管线：
    /// 分批 25/就地同步/回执口径与批量打标一致）。仅选中集内命中的文件被移除；选中集保持——
    /// ReplaceGalleryItemState 卡片 VM 实例不变（UpdateFrom 就地更新）天然保选中。
    /// 无命中（计数竞态：chip 显示后标签已被其他路径移除）静默返回。
    /// </summary>
    /// <param name="tagName">标签名（chip 显示拼写）。</param>
    public async Task RemoveTagFromSelectionAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName) || _isTagOperationRunning)
        {
            return;
        }

        var paths = _selectedCards
            .Where(vm => vm.Item.Tags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            .Select(static vm => vm.Item.Path)
            .ToList();
        if (paths.Count == 0)
        {
            return; // 计数竞态兜底：选中集已无该标签，无需动作。
        }

        await ApplyTagToPathsAsync(paths, FindGroupByTagName(tagName), tagName, remove: true);
    }

    /// <summary>
    /// 图库右栏「＋ 添加标签」按钮（batch-tag-management Step 4 占位）：
    /// Step 5 接线批量标签目录（TagCatalogDialog 批量三态变体 + 宿主回调）。
    /// 本波次为 no-op——命令先行存在以稳定 XAML 绑定与 UIA 走查。
    /// </summary>
    [RelayCommand]
    private void OpenSelectionTagCatalog()
    {
        // Step 5 接线批量目录（当前 no-op 占位，勿在此添加逻辑）。
    }

    /// <summary>
    /// 标签目录快照（TagCatalogDialog 构造时一次性取用）：配置组序列 + 当前图标签集
    /// （判已选态）。快照口径——对话框生命周期内配置不变（编辑入口都在侧栏，对话框打开期间互斥）。
    /// </summary>
    public (IReadOnlyList<TagGroup> Groups, IReadOnlyCollection<string> CurrentTags, IReadOnlyDictionary<string, int> TagCounts) GetTagCatalogSnapshot()
        => (
            _settingsService?.Load().TagGroups ?? [],
            [.. CurrentImageTags],
            _latestTagCounts);

    /// <summary>
    /// 对当前图应用目录选中的标签（TagCatalogDialog 行点击转发）：走单图 toggle 管线的“添加”方向
    /// （已含标签的行在目录中禁点，此处 toggle 必然是添加；互斥组语义由 TagSemantics 处理）。
    /// </summary>
    public async Task ApplyCatalogTagAsync(TagGroup group, string tagName)
        => await ToggleTagOnCurrentImageAsync(group, tagName);

    /// <summary>
    /// 同图改名后的轻量刷新：不重走解码管线（字节未变），仅按新路径重算文件名分段与右栏信息行
    /// （CurrentImagePath 已被 ReplaceGalleryItemState 就地替换为新路径；改名事件已先行通知视图
    /// 保持缩放/平移态）。GIF 例外：其 ImageSource 以 UriSource 指向文件路径，改名后旧 Uri 失效
    /// 且 BitmapImage 无法用内存字节重建动画源——按旧策略完整重载一次（换源时视图路径对比
    /// 命中"同图"已追新的路径，缩放态仍保持，仅视觉上一次换源）。
    /// </summary>
    private async Task RefreshCurrentAfterRenameAsync()
    {
        var path = CurrentImagePath;
        if (path is null)
        {
            return;
        }

        if (_currentLoaded is not { IsGif: false } loaded)
        {
            await LoadCurrentAsync();
            return;
        }

        UpdateStatusText(loaded, path);
    }

    /// <summary>
    /// 批量打标/移除统一管线：分批 TagService 落盘（内部 Task.Run，不阻塞 UI）→
    /// 每批后就地同步成功项（索引行替换 + 卡片/单图列表更新，保滚动位置与选中态）→
    /// 分批推进 InfoBar 进度。返回聚合回执与同步统计。
    /// </summary>
    private async Task<(BatchOperationResult Result, SyncResult Sync)> RunTagOperationAsync(
        IReadOnlyList<GalleryItem> candidates,
        TagGroup group,
        string tagName,
        bool remove,
        bool showProgress)
    {
        var succeeded = 0;
        var failures = new List<TagOperationFailure>();
        var syncedTotal = 0;
        var failedTotal = 0;
        var transform = remove
            ? new Func<IReadOnlyList<string>, IReadOnlyList<string>>(tags => RemoveTag(tags, tagName))
            : tags => TagSemantics.Apply(tags, group, tagName);

        var processed = 0;
        for (var offset = 0; offset < candidates.Count; offset += TagBatchSize)
        {
            var batch = candidates.Skip(offset).Take(TagBatchSize).ToList();
            var batchPaths = batch.Select(static c => c.Path).ToList();
            var result = remove
                ? await _tagService.RemoveTagAsync(batchPaths, tagName)
                : await _tagService.ApplyTagAsync(batchPaths, new TagDefinition { Name = tagName }, group);
            succeeded += result.SucceededCount;
            failures.AddRange(result.Failures);

            var sync = await SyncRenamedItemsAsync(batch, transform);
            syncedTotal += sync.Synced;
            failedTotal += sync.Failed;

            processed += batch.Count;
            if (showProgress && processed < candidates.Count)
            {
                TagOperationProgress = (double)processed / candidates.Count;
                TagFeedbackMessage = $"正在处理 {processed}/{candidates.Count} 张";
            }
        }

        // 图库右栏挂点②（D6）：批量打标/移除统一管线收口——所有改标签路径（批量打标/单图右栏 ✕/
        // 图库右栏 chip ✕/未定义区连锁删）汇入本管线，完成后重算一次选中集并集
        // （SyncRenamedItemsAsync 已就地更新选中卡片的 Item.Tags；不在其内部逐文件触发，防 O(N²)）。
        RecomputeSelectionTagUnion();

        return (new BatchOperationResult(succeeded, failures), new SyncResult(syncedTotal, failedTotal));
    }

    /// <summary>
    /// 进入批量操作态（D13）：重置进度；showProgress=true（批量）时打开 InfoBar 显示进度
    ///（PRD 验收项）；showProgress=false（单图）不弹——“正在处理…”一闪无信息量
    ///（2026-09-19 成功静默拍板），失败终态由 <see cref="ShowTagOperationResult"/> 弹出。
    /// </summary>
    private void BeginTagOperation(string title, bool showProgress, int totalCount)
    {
        TagFeedbackSeverity = InfoBarSeverity.Informational;
        TagFeedbackTitle = title;
        TagFeedbackMessage = showProgress ? $"正在处理 0/{totalCount} 张" : "正在处理…";
        _tagFeedbackDetails = [];
        OnPropertyChanged(nameof(HasTagFeedbackDetails));
        OnPropertyChanged(nameof(TagFeedbackDetailsVisibility));
        IsTagOperationInProgress = showProgress;
        TagOperationProgress = 0;
        if (showProgress)
        {
            IsTagFeedbackOpen = true;
        }
    }

    /// <summary>
    /// 终态回执（2026-09-19 成功静默拍板）：全成功一律不弹——就地反馈已充分
    ///（卡片角标/右栏 chips/侧栏计数），并顺手关掉可能残留的上次失败回执；
    /// 互斥替换括注随成功静默消失（chips 就地可见替换结果）；
    /// 含失败才弹 Warning（部分）/Error（全部），失败明细可展开（成功项不回滚）。
    /// </summary>
    private void ShowTagOperationResult(BatchOperationResult result, SyncResult sync)
    {
        IsTagOperationInProgress = false;
        var failedCount = Math.Max(result.Failures.Count, sync.Failed);
        if (failedCount == 0)
        {
            _tagFeedbackDetails = [];
            OnPropertyChanged(nameof(HasTagFeedbackDetails));
            OnPropertyChanged(nameof(TagFeedbackDetailsVisibility));
            IsTagFeedbackOpen = false;
            return;
        }

        TagFeedbackSeverity = result.SucceededCount > 0 || sync.Synced > 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Error;
        TagFeedbackMessage =
            $"成功 {result.SucceededCount} 张，失败 {failedCount} 张（成功项不回滚，失败项可重试）。";
        _tagFeedbackDetails = BuildFailureDetails(result.Failures);

        OnPropertyChanged(nameof(HasTagFeedbackDetails));
        OnPropertyChanged(nameof(TagFeedbackDetailsVisibility));
        IsTagFeedbackOpen = true;
    }

    /// <summary>
    /// 即时回执（不经过批量管线）：单张失败前置校验、标签目录解析失败等 tag 场景；
    /// 状态栏移除后（2026-09-19）也承载非 tag 场景的即时错误提示——
    /// 删除/移动/加载失败与标签编辑批量部分失败（原 StatusText 写入点的迁移归宿）。
    /// </summary>
    private void ShowInstantTagFeedback(
        InfoBarSeverity severity, string title, string message, IReadOnlyList<string> details)
    {
        IsTagOperationInProgress = false;
        TagFeedbackSeverity = severity;
        TagFeedbackTitle = title;
        TagFeedbackMessage = message;
        _tagFeedbackDetails = details;
        OnPropertyChanged(nameof(HasTagFeedbackDetails));
        OnPropertyChanged(nameof(TagFeedbackDetailsVisibility));
        IsTagFeedbackOpen = true;
    }

    /// <summary>失败明细格式化（文件名：原因；整体拒绝为纯原因；超过上限折叠）。</summary>
    private static IReadOnlyList<string> BuildFailureDetails(List<TagOperationFailure> failures)
    {
        var details = new List<string>();
        foreach (var failure in failures)
        {
            details.Add(string.IsNullOrEmpty(failure.Path)
                ? failure.Reason
                : $"{Path.GetFileName(failure.Path)}：{failure.Reason}");
            if (details.Count >= MaxFailureDetails)
            {
                break;
            }
        }

        if (failures.Count > MaxFailureDetails)
        {
            details.Add($"……其余 {failures.Count - MaxFailureDetails} 项从略");
        }

        return details;
    }

    /// <summary>在当前呈现集中查找同路径项（单图打标的宽高/排序 key 继承源；找不到返回 null）。</summary>
    private GalleryItem? FindPresentedItemByPath(string path)
    {
        foreach (var viewModel in _waterfall.Items)
        {
            if (string.Equals(viewModel.Item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return viewModel.Item;
            }
        }

        return null;
    }

    // ==================== 标签筛选（tag-filter-tree：条件树单一内存求值 + 表达式段筛选条；untagged ∅ 独立位互斥） ====================

    /// <summary>任一筛选是否激活（条件树有有效条件或无标签模式；筛选条可见性与扫描追加块过滤依据）。
    /// 「树有有效条件」= CollectReferencedTags 非空（空 Values 条件 = 未启用，不约束不计数）。</summary>
    public bool HasAnyFilter
        => TagFilterState.CollectReferencedTags(_filterRoot).Count > 0 || IsUntaggedFilterActive;

    /// <summary>条件树当前条件行数（含未启用行；BuildExpression 条件段计数，工具栏徽章 Step 5 接线用）。</summary>
    public int ActiveConditionCount
        => TagFilterState.BuildExpression(_filterRoot).OfType<CondSegment>().Count();

    /// <summary>
    /// 瀑布流追加块的筛选谓词（spec D1 单一求值器：筛选应用与扫描追加块过滤共用同一树求值）：
    /// 无标签态 = MatchesUntagged；否则条件树 Evaluate（无有效条件时树恒真 = 全过，等价不过滤）。
    /// </summary>
    public bool MatchesTagFilter(GalleryItem item)
        => IsUntaggedFilterActive
            ? TagFilterState.MatchesUntagged(item.Tags)
            : TagFilterState.Evaluate(_filterRoot, item.Tags);

    /// <summary>从最近一次计数快照取指定标签计数（编辑对话框影响张数）。</summary>
    public int GetTagCount(string tagName)
        => _latestTagCounts.TryGetValue(tagName, out var count) ? count : 0;

    /// <summary>筛选条 chip 集合（表达式段形态；随筛选态/配置组变化全量重建，UI 线程）。</summary>
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];

    /// <summary>筛选条可见性（任一筛选激活——条件树或无标签）。</summary>
    public Visibility FilterBarVisibility =>
        HasAnyFilter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>筛选统计文本：命中 X / 已发现 Y 张（命中 = 当前呈现数；已发现 = 扫描全量暂存数）。</summary>
    public string FilterStatsText =>
        $"命中 {_waterfall.Items.Count} / 已发现 {_galleryItems.Count} 张";

    /// <summary>删除单个条件行（筛选条条件 chip 的 ✕；按节点引用删除，RemoveNode）。</summary>
    private void RemoveTagFilter(FilterConditionNode node)
    {
        if (TagFilterState.RemoveNode(_filterRoot, node))
        {
            ApplyTagFilter();
        }
    }

    /// <summary>清空全部筛选（筛选条右侧「清空」按钮，demo 形态）：条件树清空 + 无标签位复位回全量。</summary>
    [RelayCommand]
    private void ClearTagFilter()
    {
        TagFilterState.Clear(_filterRoot);
        IsUntaggedFilterActive = false;
        ApplyTagFilter();
    }

    /// <summary>
    /// 切换「无标签」筛选（untagged-filter-entry；侧栏标题行 ∅ 按钮入口）：
    /// 激活 = 清空条件树（互斥）并只显示无任何标签的图片；再点取消回全量。
    /// 单图模式下先切回图库让筛选结果可见（对齐 HandleTagChipTapped 行为）。
    /// </summary>
    [RelayCommand]
    private void ToggleUntaggedFilter()
    {
        if (_isTagOperationRunning)
        {
            return;
        }

        if (CurrentMode == ViewerMode.Single && HasGallery)
        {
            CurrentMode = ViewerMode.Gallery;
        }

        // 状态机语义（TagFilterState.ToggleUntagged）：未激活 → 激活 = 互斥清树；
        // 已激活 → 再点关闭回全量（激活期间树恒被清空，无需恢复）。
        IsUntaggedFilterActive = TagFilterState.ToggleUntagged(_filterRoot, IsUntaggedFilterActive);

        ApplyTagFilter();
    }

    /// <summary>
    /// 点击侧栏标签 = 快捷追加筛选条件（tag-filter-tree，demo addQuickCond 拍板语义）：
    /// 往根组追加一条单值 in 条件；根组 Or 且已有单值 in 行则合并进该行；
    /// 已存在含该值的 in 条件（全树）则忽略（返回 false 静默——「已在筛选中」）。
    /// 旧三分支语义（单选重置/Ctrl 加减选/唯一选中再点取消）随条件树化废弃，不再读修饰键。
    /// </summary>
    /// <param name="tagName">标签名。</param>
    public void ToggleTagFilter(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        if (TagFilterState.QuickAdd(_filterRoot, tagName))
        {
            // 树编辑成功：互斥清无标签位（spec D4 反方向互斥由本类持有该标志实现）。
            IsUntaggedFilterActive = false;
            ApplyTagFilter();
        }
    }

    // ==================== 筛选面板编辑入口（tag-filter-tree Step 5：薄包装集中一处，不动既有筛选逻辑） ====================

    /// <summary>
    /// 筛选面板树编辑统一管线（Step 5 集中入口，demo refreshAll 同构）：
    /// <paramref name="editAction"/> 收到根组引用，在其闭包内完成本次树编辑
    /// （TagFilterState 编辑函数 + 面板持有的节点引用定位目标）；
    /// 编辑后统一互斥清无标签位（spec D4 反方向互斥）并 <see cref="ApplyTagFilter"/>
    /// （chips / 侧栏 / 瀑布流 / 命中数一次到位，条件实时生效无应用按钮）。
    /// 面板不得缓存根组引用绕过本管线改树（只读读取走 <see cref="ReadFilter"/>）。
    /// </summary>
    /// <param name="editAction">本次树编辑动作（参数 = 根组引用，仅闭包内使用）。</param>
    public void EditFilter(Action<FilterGroupNode> editAction)
    {
        editAction(_filterRoot);
        IsUntaggedFilterActive = false;
        ApplyTagFilter();
    }

    /// <summary>
    /// 筛选面板只读快照构建入口（面板 Rebuild 用）：<paramref name="readFunc"/> 收到根组引用
    /// 仅做遍历读取（构建不可变渲染快照 / 判空）；树修改一律经 <see cref="EditFilter"/>。
    /// </summary>
    /// <typeparam name="T">读取结果类型。</typeparam>
    /// <param name="readFunc">只读函数（参数 = 根组引用）。</param>
    public T ReadFilter<T>(Func<FilterGroupNode, T> readFunc) => readFunc(_filterRoot);

    /// <summary>
    /// 筛选面板值选择候选快照（与侧栏 RebuildTagSidebar / TagCatalogDialog 同源）：
    /// 配置组序列（组名 / 互斥标记 / 组内标签）+ 最近一次索引标签计数（_latestTagCounts）。
    /// </summary>
    public (IReadOnlyList<TagGroup> Groups, IReadOnlyDictionary<string, int> Counts) GetFilterTagChoices()
        => (_settingsService?.Load().TagGroups ?? [], _latestTagCounts);

    /// <summary>
    /// 取 <see cref="_galleryItems"/> 稳定快照（tag-filter-tree Step 6 扫描实时性收口）：
    /// 扫描进行中后台线程仍在逐项 Add（面板编辑实时生效的前提是随时可重应用筛选），
    /// UI 线程筛选管线直接枚举活集合会触发 List 枚举版本冲突（InvalidOperationException，
    /// UI 线程未捕获即崩溃）；锁内拷贝后枚举快照——扫描中面板每次编辑（EditFilter →
    /// ApplyTagFilter）即取一次，与后台 Add 短暂互斥（拷贝 10 万量级毫秒级，攒批投递
    /// 300ms 粒度不受感）。WaterfallViewModel.ResetFrom 的「快照后复用」只保证自身两次
    /// 枚举一致，不消除枚举中的并发修改，故收口在本层完成。
    /// </summary>
    private List<GalleryItem> SnapshotGalleryItems()
    {
        lock (_galleryItemsLock)
        {
            return [.. _galleryItems];
        }
    }

    /// <summary>
    /// 按当前筛选态刷新瀑布流。三分支全内存化（spec D1，索引层零改动）：无标签 →
    /// MatchesUntagged 谓词；树有有效条件 → Evaluate 谓词；否则全量（发现序）。命中结果按
    /// SortKey 自然序排序（对齐原索引查询分支口径；GalleryItemNaturalComparer 与索引查询同款）。
    /// 枚举源统一为锁内快照（Step 6）：扫描进行中后台 Add 与本管线枚举的竞态收口。
    /// 命中序列与当前呈现完全一致时跳过整体重置（实机走查修复）：无变化的重置会整墙
    /// 闪跳（卡片 VM 全部重建、缩略图重新加载）+ 无谓清空选中集——面板添加空条件、
    /// 切换未启用条件的操作符等编辑不应惊动图墙（demo renderWall 平滑重排同因：内容不变无感知）。
    /// </summary>
    private void ApplyTagFilter()
    {
        var gallery = SnapshotGalleryItems();

        List<GalleryItem> target;
        if (IsUntaggedFilterActive)
        {
            target = gallery
                .Where(i => TagFilterState.MatchesUntagged(i.Tags))
                .OrderBy(static i => i, GalleryItemNaturalComparer.Instance)
                .ToList();
        }
        else if (TagFilterState.CollectReferencedTags(_filterRoot).Count > 0)
        {
            target = gallery
                .Where(i => TagFilterState.Evaluate(_filterRoot, i.Tags))
                .OrderBy(static i => i, GalleryItemNaturalComparer.Instance)
                .ToList();
        }
        else
        {
            // 无有效条件：回到扫描全量（发现顺序，D15）。
            target = gallery;
        }

        if (!_waterfall.PresentsExactly(target))
        {
            ClearCardSelection();
            _waterfall.ResetFrom(target);
        }

        WaterfallEmptyText = target.Count == 0
            ? (IsUntaggedFilterActive || TagFilterState.CollectReferencedTags(_filterRoot).Count > 0
                ? "当前筛选条件下没有命中图片"
                : (HasGallery && !IsScanning ? "未在所选目录发现图片" : string.Empty))
            : string.Empty;

        OnPropertyChanged(nameof(GalleryStatusText));
        OnPropertyChanged(nameof(FilterBarVisibility));
        OnPropertyChanged(nameof(FilterStatsText));
        RebuildTagSidebar();
    }

    // ==================== 标签栏数据刷新（计数快照 + 侧栏重建） ====================

    /// <summary>
    /// 启动时初始化标签栏（读配置组 + 空计数重建；MainWindow 构造后调用一次）。
    /// </summary>
    public Task InitializeTagSidebarAsync()
    {
        RebuildTagSidebar();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 刷新标签计数快照并重建侧栏（扫描节流点/扫描结束/编辑完成后调用；
    /// 可从后台线程调用——重建经 DispatcherQueue 回投 UI 线程）。
    /// </summary>
    private async Task RefreshTagDataAsync(ILibraryIndexService? indexService = null)
    {
        var service = indexService ?? _indexService;
        if (service is not null)
        {
            _latestTagCounts = await service.TagCountsAsync();
        }

        RebuildTagSidebar();
    }

    /// <summary>
    /// 重建侧栏（读配置组 + 计数快照 + 筛选高亮；ObservableCollection 写操作回投 UI 线程）。
    /// 公开给 TagSidebarViewModel：组头展开/折叠切换（ToggleGroupExpansion）后触发全量重建。
    /// </summary>
    public void RebuildTagSidebar()
    {
        var configGroups = _settingsService?.Load().TagGroups ?? [];
        var counts = _latestTagCounts;
        // 侧栏高亮 = 条件树引用标签快照（spec D4：CollectReferencedTags，OrdinalIgnoreCase 集合，
        // 侧栏 Rebuild 的 Contains 判定直接可用；全量重建惯例——不持有树内集合引用）。
        var filters = TagFilterState.CollectReferencedTags(_filterRoot);

        void Rebuild()
        {
            var tagHuesChanged = TagSidebar.Rebuild(configGroups, counts, filters);
            RebuildFilterChips();

            if (tagHuesChanged)
            {
                // 组色相索引已更新：对已呈现卡片补发 Badges 重通知。打标时序为 UpdateFrom（先）
                // → RefreshTagDataAsync → Rebuild 更新索引（后），UpdateFrom 通知的 Badges 用的
                // 是旧索引——不补发则角标集合滞后一轮（新标签未映射到组被过滤、不出角标，
                // 2026-09-19 忽略无组标签口径）。索引未变化时跳过（扫描期节流刷新频繁 Rebuild，免打扰）。
                foreach (var viewModel in _waterfall.Items)
                {
                    viewModel.NotifyBadgeHuesChanged();
                }
            }
        }

        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Rebuild);
        }
        else
        {
            Rebuild();
        }
    }

    /// <summary>主题切换后的视觉刷新入口（公开给 MainWindow）：重建侧栏与筛选条，使 x:Bind 颜色函数按新主题重算。</summary>
    public void RefreshThemeDependentVisuals() => RebuildTagSidebar();

    /// <summary>
    /// 重建筛选条 chip 集合（tag-filter-tree：表达式段形态，demo exprChips 同构）：
    /// 条件段 = 「标签：a / b」胶囊（否定 NotIn 加「非」前缀 + 红前景，✕ 删该条件节点）；
    /// 连接词段「且/或」与括号段「( )」为轻量文本；均在 UI 线程调用。
    /// 空值条件仍产段（「标签：未选」——面板可见可再赋值，demo 同构）。
    /// untagged：无标签模式激活时在最前插入「无标签」chip（与条件树互斥，两者不同时存在），
    /// ✕ = 再点取消（绑 ToggleUntaggedFilterCommand）。
    /// </summary>
    private void RebuildFilterChips()
    {
        FilterChips.Clear();
        if (IsUntaggedFilterActive)
        {
            FilterChips.Add(FilterChipViewModel.Untagged(ToggleUntaggedFilterCommand));
        }

        foreach (var segment in TagFilterState.BuildExpression(_filterRoot))
        {
            switch (segment)
            {
                case CondSegment cond:
                    FilterChips.Add(FilterChipViewModel.Condition(
                        cond.Node,
                        CondChipText(cond),
                        cond.Negated,
                        new RelayCommand(() => RemoveTagFilter(cond.Node))));
                    break;
                case OpSegment op:
                    FilterChips.Add(FilterChipViewModel.Op(op.Op));
                    break;
                case ParenSegment paren:
                    FilterChips.Add(FilterChipViewModel.Paren(paren.Open));
                    break;
            }
        }

        OnPropertyChanged(nameof(ActiveConditionCount));
    }

    /// <summary>条件 chip 文本（demo chipLabel 同构）：「标签：a / b」，空值集显示「未选」，否定加「非」前缀。</summary>
    private static string CondChipText(CondSegment segment)
    {
        var label = segment.Values.Count == 0
            ? "标签：未选"
            : $"标签：{string.Join(" / ", segment.Values)}";
        return segment.Negated ? $"非 {label}" : label;
    }

    // ==================== 标签/组编辑执行（Step 9：TagEditDialog 的执行委托） ====================

    /// <summary>
    /// 执行标签/组编辑（TagEditDialog.TrySaveAsync 的委托目标）。
    /// 返回 null 表示成功（对话框关闭）；返回错误消息表示拒绝（显示于对话框且不关闭）。
    /// 批量文件操作的部分失败不视为拒绝：成功项生效（不回滚），失败回执弹 InfoBar（Warning）。
    /// </summary>
    public async Task<string?> ExecuteTagEditAsync(TagEditRequest request, TagEditInput input)
    {
        if (_settingsService is null)
        {
            return "设置服务未初始化。";
        }

        try
        {
            return request.Kind switch
            {
                TagEditKind.AddGroup => ExecuteAddGroup(input),
                TagEditKind.AddTag => ExecuteAddTag(request, input),
                TagEditKind.RenameGroup => ExecuteRenameGroup(request, input),
                TagEditKind.ToggleExclusive => ExecuteToggleExclusive(request),
                TagEditKind.RenameTag => await ExecuteRenameTagAsync(request, input),
                TagEditKind.DeleteTag => ExecuteDeleteTag(request),
                TagEditKind.DeleteGroup => ExecuteDeleteGroup(request),
                _ => "未知操作。",
            };
        }
        catch (InvalidOperationException ex)
        {
            // SettingsService 校验拒绝（标签组/绑定合法性）：用户可读消息，对话框内显示。
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"操作失败：{ex.Message}";
        }
    }

    /// <summary>新建标签组（纯配置操作，不动文件）。</summary>
    private string? ExecuteAddGroup(TagEditInput input)
    {
        var settings = _settingsService!.Load();
        settings.TagGroups.Add(new TagGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = input.Name,
            Exclusive = input.Exclusive,
        });
        return SaveSettingsAndRebuildSidebar(settings);
    }

    /// <summary>在指定组中新建标签（纯配置操作，不动文件）。</summary>
    private string? ExecuteAddTag(TagEditRequest request, TagEditInput input)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        group.Tags.Add(new TagDefinition { Id = Guid.NewGuid().ToString("N"), Name = input.Name });
        return SaveSettingsAndRebuildSidebar(settings);
    }

    /// <summary>重命名标签组（组名仅用于展示，不影响文件名）。</summary>
    private string? ExecuteRenameGroup(TagEditRequest request, TagEditInput input)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        group.Name = input.Name;
        return SaveSettingsAndRebuildSidebar(settings);
    }

    /// <summary>组「互斥 ⇄ 兼容」切换：仅改 TagGroups 配置并保存，不改任何已落盘标签。</summary>
    private string? ExecuteToggleExclusive(TagEditRequest request)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        group.Exclusive = !group.Exclusive;
        return SaveSettingsAndRebuildSidebar(settings);
    }

    /// <summary>
    /// 重命名标签：候选（索引命中）→ 前置配置预检（防"文件已改、配置被拒"分裂）→ TagService 落盘
    /// → 逐文件同步索引与瀑布流卡片 → 配置改名保存 → 计数刷新。
    /// </summary>
    private async Task<string?> ExecuteRenameTagAsync(TagEditRequest request, TagEditInput input)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        TagDefinition? tag = group?.Tags.FirstOrDefault(t =>
            string.Equals(t.Name, request.TagName, StringComparison.Ordinal));

        // 配置预检（dry-run）：先在副本口径上校验，通过后才动文件。
        if (tag is not null)
        {
            tag.Name = input.Name;
            var previewError = TryValidate(settings);
            if (previewError is not null)
            {
                return previewError;
            }
        }

        var renameResult = await RenameFilesAsync(
            statusPrefix: $"重命名「{request.TagName}」为「{input.Name}」",
            candidateTagNames: [request.TagName],
            executeAsync: paths => _tagService.RenameTagAsync(paths, request.TagName, input.Name),
            transform: tags => ReplaceTag(tags, request.TagName, input.Name));

        if (renameResult is not null)
        {
            return renameResult; // 整体拒绝。
        }

        // 筛选树联动（tag-filter-tree 新增能力）：树内旧名引用统一改新拼写，有改动则重应用筛选
        // （旧 HashSet 时代无此联动——重命名后筛选集残留旧名静默失效，条件树化后按名联动收口）。
        var filterChanged = TagFilterState.RenameTagReferences(
            _filterRoot, request.TagName, input.Name);

        // 原「未分组标签重命名 → ForgetUngroupedTag」分支已删（2026-09-19 口径：侧栏移除未分组
        // 虚拟组与「曾见即留」记忆后，GroupId 仅由配置组行构造，group 必非空、无记忆可摘）。
        if (group is not null && tag is not null)
        {
            var saveError = SaveSettingsAndRebuildSidebar(settings);
            if (saveError is not null)
            {
                return saveError;
            }
        }

        if (filterChanged)
        {
            ApplyTagFilter();
        }

        return null;
    }

    /// <summary>
    /// 删除标签定义（batch-tag-management Step 2 纯化，D2）：纯配置操作、0 文件改名——
    /// 文件上的该标签保留，之后出现在未定义标签区。前置快捷键绑定引用整体拒绝（A2 口径：
    /// 文案沿用原 TagService.DeleteTagAsync 原文；不前移则 ValidateBindings 会在 Save 时以
    /// 「引用的标签不存在」错误文案拒绝悬空绑定，口径不符）。
    /// 通过后：筛选树引用摘除 → 配置移除标签定义 → 保存重建侧栏 → 筛选有变化则重应用。
    /// </summary>
    private string? ExecuteDeleteTag(TagEditRequest request)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        var tag = group?.Tags.FirstOrDefault(t =>
            string.Equals(t.Name, request.TagName, StringComparison.Ordinal));

        // 前置绑定引用拒绝：整体拒绝（TagEditDialog 保持打开回显错误，不走任何文件管线）。
        if (tag is not null && IsTagReferencedByBindings(settings, tag.Id))
        {
            return $"标签“{tag.Name}”被快捷键绑定引用，请先修改或移除相关绑定再删除。";
        }

        // 筛选树联动：已删除的标签引用全树移除（被清空条件保留为未启用恒真行），
        // 有改动则重应用筛选（命中集可能变化）。
        var filterChanged = TagFilterState.RemoveTagReferences(_filterRoot, request.TagName);

        // 原「未分组标签显式删除 → ForgetUngroupedTag」分支已删（2026-09-19 口径：侧栏移除未分组
        // 虚拟组与「曾见即留」记忆后，GroupId 仅由配置组行构造，group 必非空、无记忆可摘）。
        if (group is not null && tag is not null)
        {
            group.Tags.Remove(tag);
            var saveError = SaveSettingsAndRebuildSidebar(settings);
            if (saveError is not null)
            {
                return saveError;
            }
        }

        if (filterChanged)
        {
            ApplyTagFilter();
        }

        return null;
    }

    /// <summary>
    /// 删除标签组定义（batch-tag-management Step 2 纯化，D2）：纯配置操作、0 文件改名——
    /// 组内标签在文件上保留，之后出现在未定义标签区。组内任一标签被快捷键绑定引用即整体拒绝
    /// （D2 新增：原 DeleteGroupAsync 无绑定校验，不前置将落进 ValidateBindings 悬空绑定错误文案）。
    /// 通过后：逐标签摘筛选树引用（聚合 changed）→ 配置移除组 → 保存重建侧栏 → 筛选有变化则重应用。
    /// </summary>
    private string? ExecuteDeleteGroup(TagEditRequest request)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        // 前置绑定引用拒绝：组内任一标签被引用即整体拒绝（与删除标签同一文案口径）。
        var referencedTag = group.Tags.FirstOrDefault(t => IsTagReferencedByBindings(settings, t.Id));
        if (referencedTag is not null)
        {
            return $"标签“{referencedTag.Name}”被快捷键绑定引用，请先修改或移除相关绑定再删除。";
        }

        // 筛选树联动：组内全部标签的引用逐一移除（RemoveTagReferences 幂等，聚合有改动标记）。
        var filterChanged = false;
        foreach (var tagName in group.Tags.Select(t => t.Name).ToList())
        {
            filterChanged |= TagFilterState.RemoveTagReferences(_filterRoot, tagName);
        }

        settings.TagGroups.Remove(group);
        var saveError = SaveSettingsAndRebuildSidebar(settings);
        if (saveError is not null)
        {
            return saveError;
        }

        if (filterChanged)
        {
            ApplyTagFilter();
        }

        return null;
    }

    /// <summary>
    /// 判定标签 Id 是否被快捷键绑定引用（ApplyTag + TagId 命中；原 TagService 构造注入的同名
    /// 谓词随 DeleteTagAsync 删除迁此）。A2 口径：拒绝文案沿用原 DeleteTagAsync 原文；
    /// 不前移则 ValidateBindings 以「引用的标签不存在」错误文案拒绝悬空绑定。
    /// </summary>
    private static bool IsTagReferencedByBindings(AppSettings settings, string tagId)
        => settings.Shortcuts.Any(b =>
            b.Command == ViewerCommand.ApplyTag
            && string.Equals(b.TagId, tagId, StringComparison.Ordinal));

    /// <summary>
    /// 统一的重命名落盘管线（batch-tag-management Step 2 起为重命名连锁专用——删除标签/组已纯化为
    /// 配置删除、不再走文件管线，唯一调用方 ExecuteRenameTagAsync）：索引取候选 → TagService 批量执行
    /// （成功不回滚）→ 逐文件同步索引行与瀑布流卡片（就地更新，保滚动位置与选中态，D15）→ InfoBar 回执
    ///（全成功静默、有失败弹 Warning；原状态栏回执已随状态栏移除迁移至此，2026-09-19）。
    /// 返回 null = 已执行（含部分失败，回执进 InfoBar）；非 null = 整体拒绝（对话框内显示；
    /// 重命名管线现状不产生整体拒绝——Reject 语义已随 DeleteTagAsync 删除迁 VM，识别分支保留作防御）。
    /// </summary>
    /// <param name="statusPrefix">InfoBar 回执前缀（操作名）。</param>
    /// <param name="candidateTagNames">候选集的标签（OR 命中）。</param>
    /// <param name="executeAsync">批量执行委托（调用对应 TagService 方法）。</param>
    /// <param name="transform">当前标签集合 → 新标签集合（预测新路径用，语义对齐 TagService 内部纯函数）。</param>
    private async Task<string?> RenameFilesAsync(
        string statusPrefix,
        IReadOnlyList<string> candidateTagNames,
        Func<IReadOnlyList<string>, Task<BatchOperationResult>> executeAsync,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        if (_indexService is null)
        {
            return "尚未打开图库（无索引可用）。";
        }

        var candidates = await _indexService.QueryByTagsAsync(candidateTagNames);
        if (candidates.Count == 0)
        {
            return null; // 无引用：直接成功（配置操作继续）。
        }

        var paths = candidates.Select(c => c.Path).ToList();
        var result = await executeAsync(paths);
        if (result.Failures.Count == 1 && result.Failures[0].Path.Length == 0)
        {
            return result.Failures[0].Reason; // 整体拒绝（拒绝语义已随 DeleteTagAsync 迁 VM，重命名现状不产生；防御保留）。
        }

        // 索引与瀑布流就地同步：仅当旧路径消失且预测新路径存在（该文件实际改名成功）。
        var sync = await SyncRenamedItemsAsync(candidates, transform);

        // 状态栏已移除（2026-09-19 用户实测反馈）：回执迁 InfoBar——全成功静默（8874d93 口径，
        // 重命名结果就地可见：卡片角标/侧栏计数均已刷新），有失败才弹 Warning。
        var failedCount = Math.Max(result.Failures.Count, sync.Failed);
        if (failedCount > 0)
        {
            ShowInstantTagFeedback(
                InfoBarSeverity.Warning,
                statusPrefix,
                $"成功 {result.SucceededCount} 张，失败 {failedCount} 张（成功项不回滚，失败项可重试）。",
                []);
        }

        // 索引已同步：刷新计数快照并重建侧栏（后续配置保存路径的 Rebuild 复用新快照）。
        await RefreshTagDataAsync();

        // 图库右栏挂点③（D6）：重命名连锁改标签（唯一改标签而不经 RunTagOperationAsync 的路径），
        // 尾部重算一次选中集并集（SyncRenamedItemsAsync 已就地更新选中卡片的 Item.Tags）。
        RecomputeSelectionTagUnion();
        return null;
    }

    /// <summary>改名后就地同步的统计（成功同步数与未改名失败数）。</summary>
    private readonly record struct SyncResult(int Synced, int Failed);

    /// <summary>
    /// 批量改名后就地同步成功项：预测新路径 → 旧路径消失且新路径存在（实际改名成功）时
    /// 索引行替换（ReplacePathAsync）+ 全量暂存/瀑布流卡片/单图翻页列表更新 + 解码/缩略图缓存迁移。
    /// 幂等命中（新旧路径一致）不计数也不失败；预测失败或旧文件仍在（未改名）计 Failed
    /// （失败原因已由 TagService 回执聚合）。
    /// 三阶段执行（2026-09-19 打标管线修复）：① UI 线程宽松预测（纯字符串计算）；
    /// ② 后台线程磁盘事实判定 + 缓存迁移——File.Exists 每项两次的同步探测与磁盘缓存复制
    /// 在 UI 续体上批量执行曾是可感知卡顿源；③ UI 线程就地同步（集合/卡片/索引写操作）。
    /// </summary>
    private async Task<SyncResult> SyncRenamedItemsAsync(
        IReadOnlyList<GalleryItem> candidates,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        // 阶段一（UI 线程）：宽松预测新路径。
        // 宽松口径（2026-09-17 走查修复）：本阶段运行在 TagService 改名落盘之后，
        // 预测出的目标路径必然已存在——若沿用 BuildNewPath 的目标存在性冲突预检，
        // 每一次成功的改名都会被误判为冲突并跳过同步（索引/卡片停留旧路径、计数失真、
        // 回执误报"失败 N 张"）。此处只做语法与长度预检，改名的成败以磁盘事实判定。
        var predicted = new List<(GalleryItem Candidate, string NewPath)>();
        var failed = 0;
        foreach (var candidate in candidates)
        {
            var newPath = TryComposeNewPath(candidate, transform);
            if (newPath is null)
            {
                failed++; // 解析/标签校验/超长等预测失败。
                continue;
            }

            if (string.Equals(newPath, candidate.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 幂等命中：文件名不变，无需同步。
            }

            predicted.Add((candidate, newPath));
        }

        // 阶段二（后台线程）：磁盘事实判定 + 同图改名缓存迁移。
        // 旧路径消失 + 新路径存在 = 改名已落盘；此时迁移解码 LRU 与缩略图缓存到新路径键
        // （字节未变，翻页/重置后直接命中，避免旧条目成白占容量的孤儿、旧磁盘缓存永久失效）。
        var renamed = await Task.Run(() =>
        {
            var results = new List<(GalleryItem Candidate, string NewPath)>();
            foreach (var (candidate, newPath) in predicted)
            {
                if (File.Exists(candidate.Path))
                {
                    continue; // 旧路径仍在 = 该文件未被改名（TagService 回执已聚合原因）。
                }

                if (!File.Exists(newPath))
                {
                    continue; // 旧不在、新也不在：文件被外部移动/删除的异常态。
                }

                _imageLoader.MigrateCache(candidate.Path, newPath);
                _thumbnailService.MigrateCache(candidate.Path, newPath, GalleryItemViewModel.ThumbnailBucket);
                results.Add((candidate, newPath));
            }

            return results;
        });

        // 阶段三（UI 线程续体）：确认改名项就地同步索引与瀑布流/单图列表。
        var synced = 0;
        foreach (var (candidate, newPath) in renamed)
        {
            var newItem = BuildGalleryItem(newPath, candidate);
            if (_indexService is not null)
            {
                await _indexService.ReplacePathAsync(candidate.Path, newItem);
            }

            ReplaceGalleryItemState(candidate.Path, newItem);
            synced++;
        }

        failed += predicted.Count - renamed.Count;
        return new SyncResult(synced, failed);
    }

    /// <summary>配置保存 + 侧栏重建（Save 内部执行 ValidateTagGroups/ValidateBindings 校验）。</summary>
    private string? SaveSettingsAndRebuildSidebar(AppSettings settings)
    {
        var error = TryValidate(settings);
        if (error is not null)
        {
            return error;
        }

        _settingsService!.Save(settings);
        RebuildTagSidebar();
        return null;
    }

    /// <summary>校验设置（与 SettingsService.Save 同口径的前置 dry-run）。</summary>
    private static string? TryValidate(AppSettings settings)
    {
        try
        {
            SettingsService.ValidateTagGroups(settings);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private static TagGroup? FindGroup(List<TagGroup> groups, string? groupId)
        => groupId is null
            ? null
            : groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));

    /// <summary>标签集合保序替换（语义对齐 TagService.ReplaceTagCore：大小写不敏感、防重复追加）。</summary>
    private static IReadOnlyList<string> ReplaceTag(IReadOnlyList<string> tags, string oldName, string newName)
    {
        if (!tags.Contains(oldName, StringComparer.OrdinalIgnoreCase))
        {
            return tags;
        }

        var result = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            if (string.Equals(tag, oldName, StringComparison.OrdinalIgnoreCase))
            {
                if (!result.Contains(newName, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(newName);
                }
            }
            else
            {
                result.Add(tag);
            }
        }

        return result;
    }

    /// <summary>标签集合移除（语义对齐 TagService.RemoveTagCore：大小写不敏感全移除）。</summary>
    private static IReadOnlyList<string> RemoveTag(IReadOnlyList<string> tags, string tagName)
        => tags.Where(t => !string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// 预测重命名后的新全路径（仅语法校验与 260 长度预检，**不做目标存在性冲突预检**）：
    /// 同步阶段运行在 TagService 改名之后，目标文件存在恰是改名成功的证据（见 SyncRenamedItemsAsync 注释）。
    /// </summary>
    private string? TryComposeNewPath(GalleryItem item, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var fileName = Path.GetFileName(item.Path);
        if (!_tagFilename.TryParse(fileName, out var baseName, out var extension, out var tags))
        {
            return null;
        }

        string newName;
        try
        {
            newName = _tagFilename.Compose(baseName, extension, transform(tags));
        }
        catch (ArgumentException)
        {
            return null; // transform 产生了非法标签（调用方语义 bug 的兜底）。
        }

        var directory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var fullPath = Path.Combine(directory, newName);
        return fullPath.Length <= TagFilenameService.MaxPathLength ? fullPath : null;
    }

    /// <summary>
    /// 由新全路径构建新 GalleryItem（宽高/排序 key 沿用旧值——重命名不改变显示名与行序，D15；
    /// 基名/扩展名/标签从新文件名重新解析）。
    /// </summary>
    private GalleryItem BuildGalleryItem(string newPath, GalleryItem oldItem)
    {
        _tagFilename.TryParse(Path.GetFileName(newPath), out var baseName, out var extension, out var tags);
        return new GalleryItem
        {
            Path = newPath,
            DirectoryName = oldItem.DirectoryName,
            BaseName = baseName,
            Extension = extension,
            Tags = tags,
            Width = oldItem.Width,
            Height = oldItem.Height,
            FileSizeBytes = oldItem.FileSizeBytes,
            SortKey = oldItem.SortKey,
        };
    }

    /// <summary>
    /// 同步全量暂存列表、瀑布流卡片与单图翻页列表（就地更新，保滚动位置与选中态，D15）。
    /// 卡片 VM 实例不变（UpdateFrom 而非替换）——选中集引用稳定，Step 10“打标后选中集保持”由此达成；
    /// 单图翻页列表同步替换路径（当前呈现集语义下翻页路径正确，Step 11）。
    /// </summary>
    private void ReplaceGalleryItemState(string oldPath, GalleryItem newItem)
    {
        // 索引替换与后台扫描 Add 的数组扩容竞态经锁互斥（Step 6；扫描中打标/改名走本路径；
        // for 索引访问虽无枚举版本检查，但与 Add 的内部数组重分配并发写会丢更新）。
        lock (_galleryItemsLock)
        {
            for (var i = 0; i < _galleryItems.Count; i++)
            {
                if (string.Equals(_galleryItems[i].Path, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    _galleryItems[i] = newItem;
                    break;
                }
            }
        }

        // 瀑布流卡片：就地更新（不换 VM 实例；宽高/排序 key 经 BuildGalleryItem 继承旧值）。
        foreach (var viewModel in _waterfall.Items)
        {
            if (string.Equals(viewModel.Item.Path, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                viewModel.UpdateFrom(newItem);
                break;
            }
        }

        // 单图翻页列表：路径替换（若当前正显示该图，调用方负责刷新文件名分段/轻量刷新或重载）。
        for (var i = 0; i < _imageFiles.Count; i++)
        {
            if (string.Equals(_imageFiles[i], oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _imageFiles[i] = newItem.Path;
                if (i == _currentIndex)
                {
                    // 当前显示图被改名：通知视图"同图改名"（交互态归属路径追新，不触发切图复位缩放）。
                    CurrentImageRenamed?.Invoke(oldPath, newItem.Path);
                }

                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateImages))]
    private async Task PrevAsync()
    {
        if (_imageFiles.Count == 0)
        {
            return;
        }

        RotationAngle = 0;
        _currentIndex = _currentIndex <= 0 ? _imageFiles.Count - 1 : _currentIndex - 1;
        await LoadCurrentAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateImages))]
    private async Task NextAsync()
    {
        if (_imageFiles.Count == 0)
        {
            return;
        }

        RotationAngle = 0;
        _currentIndex = _currentIndex >= _imageFiles.Count - 1 ? 0 : _currentIndex + 1;
        await LoadCurrentAsync();
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void RotateLeft()
    {
        RotationAngle = (RotationAngle - 90 + 360) % 360;
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void RotateRight()
    {
        RotationAngle = (RotationAngle + 90) % 360;
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task DeleteAsync()
    {
        if (ConfirmDeleteAsync is not null && !await ConfirmDeleteAsync())
        {
            return;
        }

        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var path = _imageFiles[_currentIndex];
        try
        {
            _fileOperations.DeleteToRecycleBin(path);
        }
        catch (Exception ex)
        {
            // 状态栏已移除（2026-09-19）：即时错误提示走 InfoBar（单张操作失败无成功项，用 Error）。
            ShowInstantTagFeedback(InfoBarSeverity.Error, "删除失败", ex.Message, []);
            return;
        }

        // 磁盘事实已变（2026-09-18 走查修复：旧实现只删内存列表，文件从未进回收站，
        // 重开图库"已删"图片复活）。图库打开时同步列表/索引/瀑布流呈现，事实源永远是磁盘。
        // FindIndex + RemoveAt 为原子段：与后台扫描 Add 经锁互斥（Step 6，扫描中单图删除场景）。
        bool removedFromGallery;
        lock (_galleryItemsLock)
        {
            var galleryIndex = _galleryItems.FindIndex(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            removedFromGallery = galleryIndex >= 0;
            if (removedFromGallery)
            {
                _galleryItems.RemoveAt(galleryIndex);
            }
        }

        if (removedFromGallery)
        {
            if (_indexService is not null)
            {
                await _indexService.RemovePathAsync(path);
            }

            // 扫描计数文案同步（ApplyTagFilter 只重建瀑布流不刷新 ScanStatusText，
            // 否则状态栏残留删除前的"共 N 张"）。
            ScanStatusText = $"共 {_galleryItems.Count} 张";
            ApplyTagFilter();
        }

        await RemoveCurrentImageAfterFileOperationAsync();
    }

    /// <summary>
    /// 将当前图片移动到 <paramref name="destinationDirectory"/> 并显示下一张。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task MoveToFolderAsync(string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            ShowInstantTagFeedback(InfoBarSeverity.Warning, "移动到文件夹", "未配置目标路径。", []);
            return;
        }

        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var path = _imageFiles[_currentIndex];
        try
        {
            _fileOperations.MoveToFolder(path, destinationDirectory);
            await RemoveCurrentImageAfterFileOperationAsync();
        }
        catch (Exception ex)
        {
            // 状态栏已移除（2026-09-19）：即时错误提示走 InfoBar。
            ShowInstantTagFeedback(InfoBarSeverity.Error, "移动失败", ex.Message, []);
        }
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (OpenSettingsAsync is not null)
        {
            await OpenSettingsAsync();
        }
    }

    /// <summary>请求退出应用（快捷键或未来菜单）。</summary>
    public void RequestExit()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        FullscreenChanged?.Invoke(this, value);
    }

    partial void OnCurrentModeChanged(ViewerMode value)
    {
        OnPropertyChanged(nameof(SingleVisibility));
        OnPropertyChanged(nameof(GalleryVisibility));
    }

    partial void OnHasGalleryChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanStatusVisibility));
        OnPropertyChanged(nameof(GalleryHintVisibility));
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
        BackToGalleryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanStatusVisibility));
        OnPropertyChanged(nameof(GalleryHintVisibility));
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarExpandedVisibility));
        OnPropertyChanged(nameof(SidebarCollapsedVisibility));
    }

    partial void OnIsUntaggedFilterActiveChanged(bool value)
    {
        // 无标签筛选开关影响筛选条可见性（HasAnyFilter 派生）与统计文本；
        // 侧栏 ∅ 按钮的激活配色经 x:Bind IsUntaggedFilterActive 自行重算。
        OnPropertyChanged(nameof(FilterBarVisibility));
        OnPropertyChanged(nameof(FilterStatsText));
    }

    partial void OnIsInfoPanelCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(InfoPanelExpandedVisibility));
        OnPropertyChanged(nameof(InfoPanelCollapsedVisibility));
    }

    partial void OnIsGallerySelectionPanelCollapsedChanged(bool value)
    {
        // 图库右栏收展派生可见性（batch-tag-management Step 4，全仿单图右栏先例）。
        OnPropertyChanged(nameof(GallerySelectionPanelExpandedVisibility));
        OnPropertyChanged(nameof(GallerySelectionPanelCollapsedVisibility));
    }

    public Visibility ImageVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => HasImage ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FileNameVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>单图视图可见性（D14：与 GalleryVisibility 互斥）。</summary>
    public Visibility SingleVisibility =>
        CurrentMode == ViewerMode.Single ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>图库视图可见性（D14：与 SingleVisibility 互斥）。</summary>
    public Visibility GalleryVisibility =>
        CurrentMode == ViewerMode.Gallery ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>扫描状态文本可见性：已打开图库或扫描进行中时显示。</summary>
    public Visibility ScanStatusVisibility =>
        HasGallery || IsScanning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>图库空态引导可见性：未打开图库且未在扫描时显示（首次启动引导）。</summary>
    public Visibility GalleryHintVisibility =>
        !HasGallery && !IsScanning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>左栏展开态可见性。</summary>
    public Visibility SidebarExpandedVisibility =>
        IsSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>左栏折叠窄条可见性。</summary>
    public Visibility SidebarCollapsedVisibility =>
        IsSidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>单图详情右栏展开态可见性（仿左栏先例；仅单图模式整体可见，随宿主显隐）。</summary>
    public Visibility InfoPanelExpandedVisibility =>
        IsInfoPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>单图详情右栏折叠窄条可见性。</summary>
    public Visibility InfoPanelCollapsedVisibility =>
        IsInfoPanelCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 图库右栏（选中集标签面板）展开态可见性（batch-tag-management Step 4，仿单图右栏先例；
    /// 仅图库模式整体可见——面板宿主挂 GalleryVisibility 那层 Grid 内，单图模式天然隐藏）。
    /// </summary>
    public Visibility GallerySelectionPanelExpandedVisibility =>
        IsGallerySelectionPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>图库右栏折叠窄条可见性（36 窄条 + ◀ 展开按钮）。</summary>
    public Visibility GallerySelectionPanelCollapsedVisibility =>
        IsGallerySelectionPanelCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>图库状态行文本：扫描/共 N 张 + 已选 N 张（筛选命中数在筛选条显示，Step 11 起）。</summary>
    public string GalleryStatusText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ScanStatusText))
            {
                parts.Add(ScanStatusText);
            }

            if (SelectedCardCount > 0)
            {
                parts.Add($"已选 {SelectedCardCount} 张");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>瀑布流空态可见性：已打开图库、非扫描中且瀑布流无项时显示（首次启动引导在 MainWindow 覆盖层）。</summary>
    public Visibility WaterfallEmptyVisibility =>
        HasGallery && !IsScanning && _waterfall.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnScanStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(GalleryStatusText));
        OnPropertyChanged(nameof(FilterStatsText));
    }

    partial void OnSelectedCardCountChanged(int value)
    {
        // 选中数变化刷新图库状态行「已选 N 张」后缀（GalleryStatusText 拼接依赖）与工具栏「选择」按钮文案；
        // 图库右栏挂点①（D6）：选中集增减 → 并集全量重算 + 右栏标题行刷新（batch-tag-management Step 4）。
        OnPropertyChanged(nameof(GalleryStatusText));
        OnPropertyChanged(nameof(SelectionPanelTitleText));
        RecomputeSelectionTagUnion();
        UpdateSelectAllToggleText();
    }

    partial void OnIsTagOperationInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(TagOperationProgressVisibility));
    }

    partial void OnWaterfallEmptyTextChanged(string value)
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
    }

    /// <summary>瀑布流项集合变化（渐进追加/重置/就地替换）时刷新空态可见性、状态行、筛选统计与「选择」按钮文案。</summary>
    private void OnWaterfallItemsChanged()
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
        OnPropertyChanged(nameof(GalleryStatusText));
        OnPropertyChanged(nameof(FilterStatsText));
        UpdateSelectAllToggleText();
    }

    partial void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(FileNameVisibility));
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveToFolderCommand.NotifyCanExecuteChanged();
        // 右栏「＋」打开标签目录（tag-interaction-rework 新增，CanExecute 同样挂 HasImage）。
        OpenTagCatalogCommand.NotifyCanExecuteChanged();
    }

    private void SetImageList(IReadOnlyList<string> files, int index)
    {
        var newDirectory = files.Count > 0 ? Path.GetDirectoryName(files[index]) : null;
        if (!string.Equals(newDirectory, _currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _imageLoader.ClearCache();
            _currentDirectory = newDirectory;
        }

        _imageFiles.Clear();
        _imageFiles.AddRange(files);
        _currentIndex = index;
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadCurrentAsync()
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            ClearViewer();
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        var path = _imageFiles[_currentIndex];

        // 切图（路径变了）先清空源——避免旧图残影误导；同图重载（侧栏/右栏收展、窗口 resize
        // 引起的解码尺寸变化）保持旧源显示直到新源就绪后一次性换上，消除"收起/展开闪一下"
        // 的刷新感（2026-09-19 走查；对齐 EnsureFullResolutionAsync 的同图换源先例）。
        if (_currentLoaded is null
            || !string.Equals(_currentLoaded.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseCurrentImageSource();
        }
        // 打标即改名（TagSpaces 协议）：加载期间文件可能被就地重命名（打标管线或外部改名），
        // 按旧路径打开会抛"文件不存在"。竞态自愈——仅当当前索引指向的路径已变化时按新路径
        // 重试一次（路径变化是改名落盘的强信号，避免无意义重试）；重试仍失败走通用失败分支。
        // 2026-09-19 走查实锤：无此兜底时，竞态会把 HasImage 置 false、清空 ImageSource
        //（图片消失/空态出现/删除旋转禁用），且随后打标链路的信息刷新会掩盖"加载失败"提示。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var loaded = await _imageLoader.LoadAsync(path, _decodeSize, rotationBucket: 0, token);
                token.ThrowIfCancellationRequested();

                // 同图重载（未预清空）：换源后释放被替换旧源的 GIF 句柄（UriSource 指向文件，
                // 持有会锁文件阻碍打标改名；异图路径已在加载前 ReleaseCurrentImageSource 清过）。
                var replacedSource = ImageSource;
                var newSource = ImageSourceHelper.FromLoadedImage(loaded);

                // GIF 顺修（2026-09-19）：GIF 源是 BitmapImage+UriSource（异步打开，ImageOpened 前
                // 无像素）且不入解码缓存——直接换源则打开完成前 Image 空窗（resize 触发的 GIF 重载
                // 每次都闪）。同图场景等 ImageOpened 再提交（带超时兜底），旧源在等待期保持显示；
                // 非 GIF（WriteableBitmap 同步有像素）维持原状直接提交。
                if (loaded.IsGif
                    && newSource is Microsoft.UI.Xaml.Media.Imaging.BitmapImage gifBitmap)
                {
                    await WaitForGifSourceOpenedAsync(gifBitmap, token);
                    token.ThrowIfCancellationRequested();
                }

                ImageSource = newSource;
                if (!ReferenceEquals(replacedSource, newSource)
                    && replacedSource is Microsoft.UI.Xaml.Media.Imaging.BitmapImage replacedBitmap)
                {
                    replacedBitmap.UriSource = null;
                }

                HasImage = true;
                _lastAppliedDecodeSize = _decodeSize ?? 0;
                _currentLoaded = loaded;
                _fullResLoadedForCurrent = false;
                UpdateStatusText(loaded);

                _imageLoader.PrefetchAdjacent(_imageFiles, _currentIndex, _decodeSize);
                return;
            }
            catch (OperationCanceledException)
            {
                // 已被更新的导航或尺寸重载取代。
                return;
            }
            catch (Exception) when (attempt == 0
                && _currentIndex >= 0
                && _currentIndex < _imageFiles.Count
                && (!string.Equals(_imageFiles[_currentIndex], path, StringComparison.OrdinalIgnoreCase)
                    || (_isTagOperationRunning && !File.Exists(path))))
            {
                // 文件在加载期间被就地改名：按当前列表中的新路径重试一次。
                // 路径未变但文件消失且打标在途：同步阶段即将替换路径——短暂等待后重读再试
                //（诊断日志实锤过此窗口：异常时 _imageFiles 尚未替换，路径比对过滤器单独不命中）。
                if (string.Equals(_imageFiles[_currentIndex], path, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(80, CancellationToken.None);
                }

                path = _imageFiles[_currentIndex];
            }
            catch (Exception ex)
            {
                ImageSource = null;
                HasImage = false;
                ClearFileNameSegments();
                // 状态栏已移除（2026-09-19）：即时错误提示走 InfoBar。
                ShowInstantTagFeedback(InfoBarSeverity.Error, "加载失败", ex.Message, []);
                return;
            }
        }
    }

    private void ReleaseCurrentImageSource()
    {
        if (ImageSource is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap)
        {
            bitmap.UriSource = null;
        }

        ImageSource = null;
    }

    /// <summary>
    /// 等待 GIF 新源（BitmapImage+UriSource）异步打开完成（GIF 顺修 2026-09-19）：
    /// ImageOpened / ImageFailed / 超时（~2s 防挂）/ 取消任一即返回——超时与失败也放行提交
    ///（旧源已等待多时，短暂空窗优于永久卡住；失败后续链路自会呈现）。防快照竞态：订阅时若
    /// 像素已就绪（PixelWidth&gt;0，极快打开场景）直接返回，不等一个永不触发的 ImageOpened。
    /// </summary>
    private static async Task WaitForGifSourceOpenedAsync(
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap,
        CancellationToken token)
    {
        if (bitmap.PixelWidth > 0)
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // WinUI 3 投影：ImageOpened 为 RoutedEventHandler，ImageFailed 为 ExceptionRoutedEventHandler
        //（与 UWP 的 TypedEventHandler 签名不同，须分别声明）。
        RoutedEventHandler opened = (_, _) => completion.TrySetResult(true);
        ExceptionRoutedEventHandler failed = (_, _) => completion.TrySetResult(false);

        bitmap.ImageOpened += opened;
        bitmap.ImageFailed += failed;
        try
        {
            using (token.Register(() => completion.TrySetResult(false)))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (timeout.Token.Register(() => completion.TrySetResult(false)))
            {
                await completion.Task;
            }
        }
        finally
        {
            bitmap.ImageOpened -= opened;
            bitmap.ImageFailed -= failed;
        }
    }

    private async Task RemoveCurrentImageAfterFileOperationAsync()
    {
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        _imageFiles.RemoveAt(_currentIndex);
        _imageLoader.ClearCache();

        if (_imageFiles.Count == 0)
        {
            ClearViewer();
            return;
        }

        if (_currentIndex >= _imageFiles.Count)
        {
            _currentIndex = _imageFiles.Count - 1;
        }

        RotationAngle = 0;
        await LoadCurrentAsync();
    }

    /// <summary>
    /// 刷新当前图信息（原状态栏拼接串已随状态栏移除而删，2026-09-19）：负责文件名分段/chips 重建
    /// 与单图详情右栏三个结构化信息行的写入。
    /// </summary>
    /// <param name="loaded">当前解码结果。</param>
    /// <param name="displayPath">
    /// 文件名分段与信息行的取值路径。同图改名后 loaded.Path 停留旧路径
    ///（复用已解码结果不重建 LoadedImage），显示信息须按新路径计算（2026-09-19 管线修复）。
    /// </param>
    private void UpdateStatusText(LoadedImage loaded, string? displayPath = null)
    {
        var path = displayPath ?? loaded.Path;

        // 先算文件名分段（2026-09-19 统一口径）：显示名用剥离标签段的基名，
        // 与瀑布流卡片一致；完整名只在 tooltip（CurrentFileFullName）。
        UpdateFileNameSegments(path);

        var sizeText = FormatFileSize(loaded.FileSizeBytes);

        // 单图详情右栏的结构化信息行（2026-09-19）：独立字段（非拼接串），OneWay 绑定各自刷新；
        // 值为纯文本（行标签「文件大小/像素尺寸/序号」由视图承担）。
        CurrentImageFileSizeText = sizeText;
        CurrentImageDimensionsText = $"{loaded.PixelWidth} × {loaded.PixelHeight}";
        CurrentImageIndexText = _imageFiles.Count > 0
            ? $"{_currentIndex + 1} / {_imageFiles.Count}"
            : string.Empty;
    }

    /// <summary>
    /// 依据文件名尾部标签段解析显示信息：显示名 <see cref="CurrentImageDisplayName"/>（剥离标签段，
    /// 单图/瀑布流统一口径）与完整名 <see cref="CurrentFileFullName"/>（tooltip 用）；同时重建右栏
    /// 当前标签 chips（同一解析结果，显示名与 chips 永不分裂）。
    /// 原三段高亮属性（FileNamePrefix/TagSegment/Suffix）已随底部文件名栏移除删除（cr/P1-3）。
    /// </summary>
    private void UpdateFileNameSegments(string path)
    {
        var fileName = Path.GetFileName(path);
        if (_tagFilename.TryParse(fileName, out var baseName, out var extension, out var tags)
            && tags.Count > 0)
        {
            // 显示名 = 剥离标签段（2026-09-19 统一口径：与瀑布流卡片一致；完整名进 tooltip）。
            CurrentImageDisplayName = baseName + extension;
        }
        else
        {
            // 无标签：完整文件名即显示名。
            CurrentImageDisplayName = fileName;
            tags = [];
        }

        CurrentFileFullName = fileName;
        RebuildCurrentImageTags(tags);
    }

    /// <summary>重建当前图标签 chips 集合（全量替换；文件名解析口径，保序）。</summary>
    private void RebuildCurrentImageTags(IReadOnlyList<string> tags)
    {
        CurrentImageTags.Clear();
        foreach (var tag in tags)
        {
            CurrentImageTags.Add(tag);
        }

        OnPropertyChanged(nameof(HasCurrentImageTags));
    }

    /// <summary>当前图是否有标签（右栏空态文案可见性）。</summary>
    public bool HasCurrentImageTags => CurrentImageTags.Count > 0;

    private void ClearFileNameSegments()
    {
        CurrentImageDisplayName = string.Empty;
        CurrentFileFullName = string.Empty;
        CurrentImageFileSizeText = string.Empty;
        CurrentImageDimensionsText = string.Empty;
        CurrentImageIndexText = string.Empty;
        RebuildCurrentImageTags([]);
    }

    private static string FormatFileSize(long bytes)
    {
        var kb = bytes / 1024.0;
        if (kb > 1024)
        {
            return $"{kb / 1024:F2} MB";
        }

        return $"{kb:F2} KB";
    }

    private void ClearViewer()
    {
        _loadCts?.Cancel();
        ReleaseCurrentImageSource();
        HasImage = false;
        _currentLoaded = null;
        _fullResLoadedForCurrent = false;
        ClearFileNameSegments();
        RotationAngle = 0;
        _currentIndex = -1;
        _imageFiles.Clear();
        _currentDirectory = null;
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 依显示区尺寸计算解码上限边（2026-09-19：尺寸源改为 SingleImageView.ImageHost 实际显示区，
    /// 不再扣 chrome——旧口径按整窗解码导致显示层二次缩小，重新引入缩小锯齿/毛刺）。
    /// </summary>
    private static int? CalculateDecodeSize(int viewportWidth, int viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        return Math.Max(viewportWidth, viewportHeight);
    }
}

/// <summary>筛选条 chip 形态（tag-filter-tree）：条件段 / 连接词段 / 括号段 / 无标签独立 chip。</summary>
public enum FilterChipKind
{
    /// <summary>无标签 chip（untagged ∅ 独立入口激活时插最前）。</summary>
    Untagged,

    /// <summary>条件段（人话表达式的一个条件行，✕ 删该节点）。</summary>
    Condition,

    /// <summary>连接词段（且 / 或）。</summary>
    Op,

    /// <summary>括号段（左 / 右，非根多部件组包裹）。</summary>
    Paren,
}

/// <summary>
/// 筛选条 chip 展示模型（tag-filter-tree，demo exprChips 同构）：BuildExpression 段序列的
/// 不可变快照，经 MainViewModel.RebuildFilterChips 全量重建（对齐侧栏 chip 惯例）。
/// 条件 chip 文本「标签：a / b」（否定 NotIn 加「非」前缀，UI 渲染红前景），✕ 按节点引用删除；
/// 连接词/括号段为轻量文本（无 ✕、无胶囊底）。
/// </summary>
public sealed class FilterChipViewModel
{
    private FilterChipViewModel(FilterChipKind kind, string text, bool negated)
    {
        Kind = kind;
        Text = text;
        Negated = negated;
    }

    /// <summary>「无标签」chip（untagged 激活时插最前；✕ = 再点取消）。</summary>
    public static FilterChipViewModel Untagged(ICommand toggleCommand)
        => new(FilterChipKind.Untagged, "无标签", negated: false)
        {
            RemoveCommand = toggleCommand,
        };

    /// <summary>条件段 chip：文本「标签：a / b」（demo chipLabel 同构；空值集显示「标签：未选」），
    /// 否定段加「非」前缀；✕ 删除 <paramref name="node"/> 引用的条件行。</summary>
    public static FilterChipViewModel Condition(
        FilterConditionNode node, string text, bool negated, ICommand removeCommand)
        => new(FilterChipKind.Condition, text, negated)
        {
            Node = node,
            RemoveCommand = removeCommand,
        };

    /// <summary>连接词段：And → 「且」、Or → 「或」。</summary>
    public static FilterChipViewModel Op(FilterOp op)
        => new(FilterChipKind.Op, op == FilterOp.And ? "且" : "或", negated: false);

    /// <summary>括号段：左「(」/ 右「)」。</summary>
    public static FilterChipViewModel Paren(bool open)
        => new(FilterChipKind.Paren, open ? "(" : ")", negated: false);

    /// <summary>chip 形态（决定 XAML 模板渲染分支：胶囊 + ✕ 或轻量文本）。</summary>
    public FilterChipKind Kind { get; }

    /// <summary>chip 文本（条件/无标签完整文本；且/或/括号单字符或双字）。</summary>
    public string Text { get; }

    /// <summary>否定段（条件 NotIn）：UI 渲染红前景。</summary>
    public bool Negated { get; }

    /// <summary>条件 chip 的源条件节点（快照渲染下编辑后整体重建，引用恒有效；其余形态为 null）。</summary>
    public FilterConditionNode? Node { get; private init; }

    /// <summary>条件 chip 的 ✕ 删除命令（无标签 chip 为取消命令；且/或/括号段为 null）。</summary>
    public ICommand? RemoveCommand { get; private init; }
}
