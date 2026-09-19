// 职责：主查看器状态——单图导航/旋转/显示、双模式（图库/单图）互斥切换、图库扫描驱动、文件名标签分段、
//       瀑布流数据源驱动（渐进追加）与卡片选中集（Ctrl/Shift 连选/Ctrl+A，Step 10）、
//       打标管线（快捷键 / 拖拽卡片到标签行 / 单图详情右栏；2026-09-19 交互重构后侧栏点击不再打标）
//       与 InfoBar 进度/回执状态（Step 10，D13）、
//       标签筛选（OR 语义）与筛选条状态（chip/单删/清空/命中统计，Step 11）、标签栏数据/编辑执行（Step 9）、
//       单图详情右栏数据（结构化信息行 + 当前图标签 chips）。
// 不变量：Prev/Next 环绕且重置旋转；仅视口解码尺寸变化时重载；
//         单图翻页列表 = 进入单图时的瀑布流呈现集快照（Step 11：筛选态翻页在命中集内环绕循环）；
//         ScanAsync 为同步磁盘 IO 迭代器，一律 Task.Run 后台消费、UI 线程仅经 Progress 收进度/扫描块（几十万张不假死口径）；
//         扫描块经 Progress 回投 UI 线程后追加进 WaterfallViewModel（虚拟化数据源，绝不一次性同步灌入）；
//         卡片选中集状态在本类（WaterfallViewModel 仅转发）；Shift 连选基于当前呈现序列范围加选、锚点随点击更新；
//         批量打标分批走 TagService（内部 Task.Run），批间回 UI 线程推进 InfoBar 进度（D13）；成功不回滚；
//         打标后就地同步（索引 ReplacePath + 卡片 VM UpdateFrom + 单图列表路径替换）——卡片 VM 实例不变，
//         选中集引用天然保持（Step 10：打标后选中集不丢，路径换新）；
//         标签筛选集与命中数在本类：筛选变化 → 索引 QueryByTagsAsync → 瀑布流整体替换（筛选态不渐进追加），
//         清空筛选恢复扫描全量（不清空索引）；筛选条 chip（组名：标签名）随侧栏重建同步重建；
//         扫描期间追加的块经筛选谓词过滤后入瀑布流（筛选态与渐进追加互不干扰）；
//         标签/组编辑（重命名/删除）前置 ValidateTagGroups 预检（同口径）再动文件，避免“文件已改、配置被拒”分裂；
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

    // 图库状态：_galleryItems 为扫描全量暂存（后台线程写，UI 消费走 Waterfall 渐进追加）。
    private readonly List<GalleryItem> _galleryItems = [];
    private CancellationTokenSource? _scanCts;
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

    // 标签筛选集（标签名，OR 语义；命中数与筛选条 UI 属 Step 11，本步最小反馈见 GalleryStatusText）。
    private readonly HashSet<string> _activeFilterTags = new(StringComparer.OrdinalIgnoreCase);

    // 最近一次索引标签计数快照（侧栏计数与编辑对话框影响张数的共享数据源）。
    private IReadOnlyDictionary<string, int> _latestTagCounts = new Dictionary<string, int>();

    private readonly WaterfallViewModel _waterfall;
    private TagSidebarViewModel? _tagSidebar;
    private ISettingsService? _settingsService;

    /// <summary>UI 线程调度器（构造捕获；侧栏重建等 UI 写操作从后台路径回投）。</summary>
    private readonly DispatcherQueue? _dispatcher;

    /// <summary>扫描期间标签计数节流刷新间隔（毫秒）。</summary>
    /// <summary>
    /// 扫描期间标签计数刷新间隔（2026-09-17 走查修复：TagCounts 为全表聚合，大库扫描中
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
    private string _statusText = string.Empty;

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
    /// 当前图是否处于放大态（2026-09-19 层级走查）：SingleImageView 缩放时写入，
    /// 宿主（MainWindow）据此把内容区 ZIndex 提到工具栏/状态栏之上——放大后的图片
    /// 溢出显示区时遮盖左栏/右栏/工具栏/状态栏（用户拍板"最高层"）；复位/切图/回图库时
    /// 置回 false（侧栏恢复可交互）。视图内部对右栏的遮盖由 SingleImageView 直接
    /// 提升 ImageArea 的 ZIndex 完成（同父兄弟）。
    /// </summary>
    [ObservableProperty]
    private bool _isCurrentImageZoomed;

    /// <summary>
    /// chrome 遮盖层顶部行总高（工具栏 + InfoBar，含 InfoBar 上下 Margin；2026-09-19 遮挡修复）：
    /// MainWindow 依各行 SizeChanged 写入。画布层浮层（右栏/折叠条）位于 chrome 层之下，
    /// 顶部可点区必须让出这段高度（右栏收起按钮曾被工具栏横行遮盖、鼠标点不到）。
    /// </summary>
    [ObservableProperty]
    private double _topChromeHeight;

    /// <summary>
    /// chrome 遮盖层底部行总高（状态栏）：同 <see cref="TopChromeHeight"/>，浮层底部避让用
    ///（原文件名栏硬编码 34 的动态替代）。
    /// </summary>
    [ObservableProperty]
    private double _bottomChromeHeight;


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

    /// <summary>是否存在卡片选中（「清除选择」按钮的可用性）。</summary>
    public bool HasSelection => SelectedCardCount > 0;

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

    /// <summary>当前文件名的标签前前缀段（含“[”，无标签时为完整文件名）。</summary>
    [ObservableProperty]
    private string _fileNamePrefix = string.Empty;

    /// <summary>当前文件名的标签段（方括号内文本；无标签为空串）。</summary>
    [ObservableProperty]
    private string _fileNameTagSegment = string.Empty;

    /// <summary>当前文件名的标签后后缀段（“]”+ 扩展名；无标签为空串）。</summary>
    [ObservableProperty]
    private string _fileNameSuffix = string.Empty;

    /// <summary>
    /// 当前图显示名（剥离方括号标签段的 base 名 + 扩展名；解析失败回退完整文件名）。
    /// 2026-09-19 统一口径：单图模式（底部文件名栏/右栏信息区/状态行）与瀑布流卡片
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
    /// 启动/重启图库递归扫描：取消既有扫描、按根目录重建索引服务、后台消费扫描流。
    /// </summary>
    private async Task StartLibraryScanAsync(string root)
    {
        SimpleViewer.Services.DiagnosticTrace.Mark($"scan:start {root}");
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        _libraryRootPath = root;
        HasGallery = true;
        CurrentMode = ViewerMode.Gallery;

        // 索引服务按根目录派生库文件名（根目录后建，可延迟创建——D2 口径）；
        // 局部变量捕获，避免扫描中被替换/释放的实例与后台任务竞态。
        var indexService = new LibraryIndexService(root, scanService: _scanService);
        _indexService?.Dispose();
        _indexService = indexService;

        // 索引缓存全量重建（2026-09-17 走查修复）：同一根目录复用旧库文件时，
        // 上一轮的孤儿行（打标改名前的旧 path）会污染候选集与标签计数；
        // 事实源是文件名，每次打开图库即清表重灌。
        await indexService.ClearAllItemsAsync(token);

        _galleryItems.Clear();
        ClearCardSelection();
        _activeFilterTags.Clear();
        _latestTagCounts = new Dictionary<string, int>();
        _waterfall.ResetFrom([]);
        WaterfallEmptyText = string.Empty;
        IsScanning = true;
        ScanStatusText = "扫描中 · 已发现 0 张";
        OnPropertyChanged(nameof(FilterBarVisibility));
        OnPropertyChanged(nameof(OrBadgeVisibility));
        OnPropertyChanged(nameof(FilterStatsText));

        // Progress 构造于 UI 线程：Report 回调自动回投 UI 线程（仅更新状态文本与渐进追加瀑布流）。
        var progress = new Progress<int>(count => ScanStatusText = $"扫描中 · 已发现 {count} 张");
        IProgress<IReadOnlyList<GalleryItem>> chunkProgress =
            new Progress<IReadOnlyList<GalleryItem>>(_waterfall.AppendChunkFromScan);

        // 扫描期间标签计数节流刷新（TagCounts 全表聚合较重，不宜每块刷）。
        var lastTagRefresh = Stopwatch.StartNew();

        try
        {
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
                    _galleryItems.Add(item);
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

            ScanStatusText = $"共 {_galleryItems.Count} 张";
            SimpleViewer.Services.DiagnosticTrace.Mark($"scan:end {_galleryItems.Count}");
            if (_galleryItems.Count == 0)
            {
                WaterfallEmptyText = "未在所选目录发现图片";
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = $"扫描已取消 · 已发现 {_galleryItems.Count} 张";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            // 结束态（成功/取消/失败）统一做一次终态计数刷新，保证侧栏计数与索引一致。
            await RefreshTagDataAsync(indexService);
            IsScanning = false;
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
    /// 清空卡片选中集（Esc 路由 / 筛切换 / 重开图库 / 状态行「清除选择」按钮）。
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

    /// <summary>
    /// 状态行「清除选择」按钮命令（2026-09-19 Explorer 心智：多选入口收窄后补显式清除）：
    /// 转发 <see cref="ClearCardSelection"/>；无选中时禁用（常显灰态，非隐藏）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection()
    {
        ClearCardSelection();
    }

    // ==================== 打标入口与标签筛选（2026-09-19 交互重构） ====================

    /// <summary>批量操作分批粒度（每批一次 TagService 调用；批间 await 回 UI 线程推进进度与就地同步）。</summary>
    private const int TagBatchSize = 25;

    /// <summary>失败明细最多展示条数（超出折叠为“其余 N 项从略”）。</summary>
    private const int MaxFailureDetails = 20;

    /// <summary>
    /// 侧栏标签 chip 点击入口（2026-09-19 交互重构：点击一律 = 筛选）：
    /// 切换该标签的筛选（OR 语义，再点取消）；单图模式下额外切回图库让筛选结果可见
    /// （CLI 直开无图库时保持单图——无索引可查，切回只会看到空态）。
    /// 打标入口已移交：拖拽卡片到标签行 / 单图详情右栏 / 快捷键（ApplyTagByShortcutAsync）。
    /// 原 Shift+点击“从选中集移除”入口随之取消——移除走详情页右栏 chip 的 ✕（原
    /// RemoveTagFromSelectionAsync 已删除，需要时 git 历史可找回）。
    /// </summary>
    /// <param name="tagName">标签名。</param>
    public async Task HandleTagChipTappedAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName) || _isTagOperationRunning)
        {
            return;
        }

        if (CurrentMode == ViewerMode.Single && HasGallery)
        {
            CurrentMode = ViewerMode.Gallery;
        }

        await ToggleTagFilterAsync(tagName);
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
    /// 2026-09-19 交互重构后侧栏点击不再进此方法；拖拽路径走 ApplyTagToDraggedCardsAsync，共用 ApplyTagToPathsAsync。</summary>
    public async Task ApplyTagToSelectionAsync(TagGroup group, string tagName)
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
    /// <param name="ownerGroup">标签所属配置组（未分组虚拟组为 null：按兼容组叠加语义）。</param>
    /// <param name="tagName">标签名。</param>
    public async Task ApplyTagToDraggedCardsAsync(TagGroup? ownerGroup, string tagName)
    {
        var payload = _dragPayload;
        _dragPayload = null;
        if (payload is null || payload.Count == 0)
        {
            return;
        }

        // 未分组/无组上下文兜底（与原侧栏点击打标同口径）：兼容组（非互斥）叠加。
        var group = ownerGroup ?? new TagGroup
        {
            Name = TagSidebarViewModel.UngroupedGroupName,
            Exclusive = false,
        };

        await ApplyTagToPathsAsync(payload, group, tagName);
    }

    /// <summary>
    /// 按路径集批量打标（选中集快捷键与拖拽共用入口）：路径 → 候选 GalleryItem——优先在当前呈现集中
    /// 查同路径项（宽高/排序 key 继承，瀑布流卡片就地更新不失真）；不在呈现集（如拖拽中途瀑布流被重置）
    /// 时解析文件名构造最小候选（未知宽高回退 1:1，索引行由后续对账重建纠正）。
    /// </summary>
    private async Task ApplyTagToPathsAsync(IReadOnlyList<string> paths, TagGroup group, string tagName)
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

        var title = group.Exclusive
            ? $"互斥组设置「{tagName}」"
            : $"添加标签「{tagName}」";

        _isTagOperationRunning = true;
        try
        {
            BeginTagOperation(title, showProgress: true, candidates.Count);
            var (result, sync) = await RunTagOperationAsync(candidates, group, tagName, remove: false, showProgress: true);
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
    /// 成功后同图改名不重载：保持 ImageSource/解码缓存与缩放态，仅按新路径重算文件名分段/状态行
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
            // 全量重解码，造成闪空、解码缓存 miss 与缩放复位）。仅按新路径重算状态行与文件名分段；
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
    /// 移除当前图的一个标签（右栏 chip 的 ✕）：按标签名解析所属配置组（未命中 = 未分组兜底组，
    /// 兼容叠加语义）后走单图 toggle 管线——当前图必含该标签（chips 即当前标签集），toggle 即移除。
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
    /// 未命中（历史遗留的未分组标签）返回未分组兜底组（兼容叠加语义，与拖拽/侧栏同口径）。
    /// </summary>
    private TagGroup FindGroupByTagName(string tagName)
        => (_settingsService?.Load().TagGroups ?? []).FirstOrDefault(
               g => g.Tags.Any(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)))
            ?? new TagGroup
            {
                Name = TagSidebarViewModel.UngroupedGroupName,
                Exclusive = false,
            };

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
    /// 同图改名后的轻量刷新：不重走解码管线（字节未变），仅按新路径重算状态行与文件名分段
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

    /// <summary>即时回执（单张失败前置校验等，不经过批量管线）。</summary>
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

    // ==================== 标签筛选（Step 9：点击侧栏标签 = 切换筛选，OR 语义；Step 11：筛选条 UI） ====================

    /// <summary>标签筛选是否激活（激活时扫描追加块按谓词过滤后入瀑布流）。</summary>
    public bool IsTagFilterActive => _activeFilterTags.Count > 0;

    /// <summary>瀑布流追加块的筛选谓词（OR 命中任一激活标签）。</summary>
    public bool MatchesTagFilter(GalleryItem item)
        => item.Tags.Any(tag => _activeFilterTags.Contains(tag));

    /// <summary>当前激活的筛选标签集快照（侧栏 chip 高亮依据）。</summary>
    public IReadOnlyCollection<string> ActiveFilterTags => _activeFilterTags;

    /// <summary>从最近一次计数快照取指定标签计数（编辑对话框影响张数）。</summary>
    public int GetTagCount(string tagName)
        => _latestTagCounts.TryGetValue(tagName, out var count) ? count : 0;

    /// <summary>筛选条 chip 集合（激活标签；随筛选集/配置组变化全量重建，UI 线程）。</summary>
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];

    /// <summary>筛选条可见性（任一筛选激活；Step 11）。</summary>
    public Visibility FilterBarVisibility =>
        IsTagFilterActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>多标签 OR 语义徽章可见性（两个及以上激活标签）。</summary>
    public Visibility OrBadgeVisibility =>
        _activeFilterTags.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>筛选统计文本：命中 X / 已发现 Y 张（命中 = 当前呈现数；已发现 = 扫描全量暂存数）。</summary>
    public string FilterStatsText =>
        $"命中 {_waterfall.Items.Count} / 已发现 {_galleryItems.Count} 张";

    /// <summary>移除单个筛选标签（筛选条 chip 的 ✕；Step 11 单删）。</summary>
    public async Task RemoveTagFilterAsync(string tagName)
    {
        if (!string.IsNullOrWhiteSpace(tagName) && _activeFilterTags.Remove(tagName))
        {
            await ApplyTagFilterAsync();
        }
    }

    /// <summary>清空全部筛选（筛选条「清空筛选」按钮；恢复图库全量，不清空索引）。</summary>
    [RelayCommand]
    private async Task ClearTagFiltersAsync()
    {
        if (_activeFilterTags.Count > 0)
        {
            _activeFilterTags.Clear();
            await ApplyTagFilterAsync();
        }
    }

    /// <summary>
    /// 点击侧栏标签 = 切换筛选：再次点击取消；多标签 OR 语义。
    /// 筛选态下瀑布流只显示命中（索引 QueryByTags 全量命中集整体替换，不渐进追加）；
    /// 命中数经筛选条反馈（Step 11）。
    /// </summary>
    public async Task ToggleTagFilterAsync(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        if (!_activeFilterTags.Remove(tagName))
        {
            _activeFilterTags.Add(tagName);
        }

        await ApplyTagFilterAsync();
    }

    /// <summary>按当前筛选集刷新瀑布流（重置视图；选中集清空——卡片 VM 将全部重建）。</summary>
    private async Task ApplyTagFilterAsync()
    {
        if (_indexService is not null && IsTagFilterActive)
        {
            var hits = await _indexService.QueryByTagsAsync([.. _activeFilterTags]);
            ClearCardSelection();
            _waterfall.ResetFrom(hits);
            WaterfallEmptyText = hits.Count == 0 ? "当前筛选条件下没有命中图片" : string.Empty;
        }
        else
        {
            // 无筛选（或尚未建立索引）：回到扫描全量（发现顺序，D15）。
            ClearCardSelection();
            _waterfall.ResetFrom(_galleryItems);
            WaterfallEmptyText = HasGallery && _galleryItems.Count == 0 && !IsScanning
                ? "未在所选目录发现图片"
                : string.Empty;
        }

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
        var filters = _activeFilterTags;

        void Rebuild()
        {
            var tagHuesChanged = TagSidebar.Rebuild(configGroups, counts, filters);
            RebuildFilterChips(configGroups);

            if (tagHuesChanged)
            {
                // 组色相索引已更新：对已呈现卡片补发 Badges 重通知。打标时序为 UpdateFrom（先）
                // → RefreshTagDataAsync → Rebuild 更新索引（后），UpdateFrom 通知的 Badges 用的
                // 是旧索引——不补发则角标底色滞后一轮（新标签误显示未分组灰蓝底，
                // 2026-09-19 管线修复）。索引未变化时跳过（扫描期节流刷新频繁 Rebuild，免打扰）。
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
    /// 重建筛选条 chip 集合（Step 11）：激活标签 → 「组名：标签名」chip + 单删命令；
    /// 组名取配置组（跨组重名已被校验拒绝），不在任何配置组的标签归「未分组」；
    /// 呈现顺序按组名 + 标签名稳定排序（筛选集为 HashSet，需确定序）。仅在 UI 线程调用。
    /// </summary>
    private void RebuildFilterChips(List<TagGroup> configGroups)
    {
        FilterChips.Clear();
        foreach (var tagName in _activeFilterTags
                     .OrderBy(t => t, StringComparer.CurrentCulture))
        {
            var groupName = configGroups
                .FirstOrDefault(g => g.Tags.Any(t =>
                    string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)))
                ?.Name
                ?? TagSidebarViewModel.UngroupedGroupName;
            var capturedName = tagName;
            FilterChips.Add(new FilterChipViewModel(
                groupName,
                capturedName,
                new AsyncRelayCommand(() => RemoveTagFilterAsync(capturedName))));
        }

        OnPropertyChanged(nameof(OrBadgeVisibility));
    }

    // ==================== 标签/组编辑执行（Step 9：TagEditDialog 的执行委托） ====================

    /// <summary>
    /// 执行标签/组编辑（TagEditDialog.TrySaveAsync 的委托目标）。
    /// 返回 null 表示成功（对话框关闭）；返回错误消息表示拒绝（显示于对话框且不关闭）。
    /// 批量文件操作的部分失败不视为拒绝：成功项生效（不回滚），失败明细写入状态栏。
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
                TagEditKind.DeleteTag => await ExecuteDeleteTagAsync(request),
                TagEditKind.DeleteGroup => await ExecuteDeleteGroupAsync(request),
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

        if (group is null)
        {
            // 未分组标签重命名：旧名从「曾见即留」记忆摘除（新名经计数自然出现），
            // 避免 0 计数旧行残留（见 TagSidebarViewModel._knownUngroupedTags 注释）。
            TagSidebar.ForgetUngroupedTag(request.TagName);
        }

        if (group is not null && tag is not null)
        {
            var saveError = SaveSettingsAndRebuildSidebar(settings);
            if (saveError is not null)
            {
                return saveError;
            }
        }

        return null;
    }

    /// <summary>删除标签：候选（索引命中）→ TagService 落盘移除 → 同步索引/瀑布流/筛选集 → 配置移除保存。</summary>
    private async Task<string?> ExecuteDeleteTagAsync(TagEditRequest request)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        var tag = group?.Tags.FirstOrDefault(t =>
            string.Equals(t.Name, request.TagName, StringComparison.Ordinal));

        var deleteResult = await RenameFilesAsync(
            statusPrefix: $"删除标签「{request.TagName}」",
            candidateTagNames: [request.TagName],
            executeAsync: paths => _tagService.DeleteTagAsync(
                paths, tag ?? new TagDefinition { Name = request.TagName }),
            transform: tags => RemoveTag(tags, request.TagName));

        if (deleteResult is not null)
        {
            return deleteResult; // 整体拒绝（如被快捷键绑定引用）。
        }

        // 筛选集清理：已删除的标签不再可筛选（在筛选集中则重查）。
        var filterChanged = _activeFilterTags.Remove(request.TagName);

        if (group is null)
        {
            // 未分组标签显式删除：从「曾见即留」记忆摘除，使其从标签库消失
            //（避免已删除标签以 0 计数行残留；见 TagSidebarViewModel._knownUngroupedTags 注释）。
            TagSidebar.ForgetUngroupedTag(request.TagName);
        }

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
            await ApplyTagFilterAsync();
        }

        return null;
    }

    /// <summary>删除标签组：候选（组内全部标签 OR 命中）→ 级联落盘移除 → 同步 → 配置移除组保存。</summary>
    private async Task<string?> ExecuteDeleteGroupAsync(TagEditRequest request)
    {
        var settings = _settingsService!.Load();
        var group = FindGroup(settings.TagGroups, request.GroupId);
        if (group is null)
        {
            return "目标标签组不存在（配置可能已被外部修改）。";
        }

        var groupTagNames = group.Tags.Select(t => t.Name).ToList();
        var groupSnapshot = group; // 执行时组标签名单快照（防迭代中被修改）。
        var deleteResult = await RenameFilesAsync(
            statusPrefix: $"删除标签组「{request.GroupName}」",
            candidateTagNames: groupTagNames,
            executeAsync: paths => _tagService.DeleteGroupAsync(paths, groupSnapshot),
            transform: tags => tags.Where(t => !groupTagNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray());

        if (deleteResult is not null)
        {
            return deleteResult;
        }

        var filterChanged = false;
        foreach (var tagName in groupTagNames)
        {
            filterChanged |= _activeFilterTags.Remove(tagName);
        }

        settings.TagGroups.Remove(group);
        var saveError = SaveSettingsAndRebuildSidebar(settings);
        if (saveError is not null)
        {
            return saveError;
        }

        if (filterChanged)
        {
            await ApplyTagFilterAsync();
        }

        return null;
    }

    /// <summary>
    /// 统一的重命名落盘管线：索引取候选 → TagService 批量执行（成功不回滚）→
    /// 逐文件同步索引行与瀑布流卡片（就地更新，保滚动位置与选中态，D15）→ 状态栏回执。
    /// 返回 null = 已执行（含部分失败，明细进状态栏）；非 null = 整体拒绝（对话框内显示）。
    /// </summary>
    /// <param name="statusPrefix">状态栏回执前缀。</param>
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
            return result.Failures[0].Reason; // 整体拒绝（如标签被快捷键绑定引用）。
        }

        // 索引与瀑布流就地同步：仅当旧路径消失且预测新路径存在（该文件实际改名成功）。
        var sync = await SyncRenamedItemsAsync(candidates, transform);
        var failures = sync.Failed;

        StatusText = result.SucceededCount > 0 || failures > 0
            ? $"{statusPrefix}：成功 {result.SucceededCount} 张，失败 {failures} 张（失败项可重试）"
            : string.Empty;

        // 索引已同步：刷新计数快照并重建侧栏（后续配置保存路径的 Rebuild 复用新快照）。
        await RefreshTagDataAsync();
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
        for (var i = 0; i < _galleryItems.Count; i++)
        {
            if (string.Equals(_galleryItems[i].Path, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _galleryItems[i] = newItem;
                break;
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
            StatusText = $"删除失败：{ex.Message}";
            return;
        }

        // 磁盘事实已变（2026-09-18 走查修复：旧实现只删内存列表，文件从未进回收站，
        // 重开图库"已删"图片复活）。图库打开时同步列表/索引/瀑布流呈现，事实源永远是磁盘。
        var galleryIndex = _galleryItems.FindIndex(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (galleryIndex >= 0)
        {
            _galleryItems.RemoveAt(galleryIndex);
            if (_indexService is not null)
            {
                await _indexService.RemovePathAsync(path);
            }

            // 扫描计数文案同步（ApplyTagFilterAsync 只重建瀑布流不刷新 ScanStatusText，
            // 否则状态栏残留删除前的"共 N 张"）。
            ScanStatusText = $"共 {_galleryItems.Count} 张";
            await ApplyTagFilterAsync();
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
            StatusText = "移动到文件夹：未配置目标路径。";
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
            StatusText = $"移动失败：{ex.Message}";
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

        // 回图库时清放大置顶态（残留 true 会让画布层盖住侧栏，图库模式下左栏不可交互）。
        if (value == ViewerMode.Gallery)
        {
            IsCurrentImageZoomed = false;
        }
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

    partial void OnIsInfoPanelCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(InfoPanelExpandedVisibility));
        OnPropertyChanged(nameof(InfoPanelCollapsedVisibility));
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
        OnPropertyChanged(nameof(GalleryStatusText));

        // 选中数变化联动「清除选择」按钮可用性（无选中时禁用灰态）。
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTagOperationInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(TagOperationProgressVisibility));
    }

    partial void OnWaterfallEmptyTextChanged(string value)
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
    }

    /// <summary>瀑布流项集合变化（渐进追加/重置/就地替换）时刷新空态可见性、状态行与筛选统计。</summary>
    private void OnWaterfallItemsChanged()
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
        OnPropertyChanged(nameof(GalleryStatusText));
        OnPropertyChanged(nameof(FilterStatsText));
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
        //（图片消失/空态出现/删除旋转禁用），且随后打标链路的状态行刷新会掩盖"加载失败"文案。
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
                StatusText = $"加载失败：{ex.Message}";
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

    private void UpdateStatusText(LoadedImage loaded, string? displayPath = null)
    {
        // displayPath：状态行与文件名分段的取值路径。同图改名后 loaded.Path 停留旧路径
        //（复用已解码结果不重建 LoadedImage），显示信息须按新路径计算（2026-09-19 管线修复）。
        var path = displayPath ?? loaded.Path;

        // 先算文件名分段（2026-09-19 统一口径）：状态行“名称”用剥离标签段的显示名，
        // 与瀑布流卡片一致；完整名只在 tooltip（CurrentFileFullName）。
        UpdateFileNameSegments(path);
        var displayName = CurrentImageDisplayName.Length > 0 ? CurrentImageDisplayName : Path.GetFileName(path);

        var sizeText = FormatFileSize(loaded.FileSizeBytes);
        var dimensions = $"{loaded.PixelWidth}x{loaded.PixelHeight}";
        var indexInfo = $"{_currentIndex + 1}/{_imageFiles.Count}";
        StatusText = $"名称：{displayName} | 大小：{sizeText} | 尺寸：{dimensions} | 序号：{indexInfo}";
        // 单图详情右栏的结构化信息行（2026-09-19）：独立字段（非拼接串），OneWay 绑定各自刷新；
        // 值为纯文本（行标签「文件大小/像素尺寸/序号」由视图承担）。
        CurrentImageFileSizeText = sizeText;
        CurrentImageDimensionsText = $"{loaded.PixelWidth} × {loaded.PixelHeight}";
        CurrentImageIndexText = _imageFiles.Count > 0
            ? $"{_currentIndex + 1} / {_imageFiles.Count}"
            : string.Empty;
    }

    /// <summary>
    /// 依据文件名尾部标签段解析显示信息：三段式属性（prefix + 标签段 + suffix，2026-09-19 起仅作
    /// 解析结果保留）、显示名 <see cref="CurrentImageDisplayName"/>（剥离标签段，单图/瀑布流统一口径）
    /// 与完整名 <see cref="CurrentFileFullName"/>（tooltip 用）；同时重建右栏当前标签 chips
    /// （同一解析结果，分段与 chips 永不分裂）。
    /// </summary>
    private void UpdateFileNameSegments(string path)
    {
        var fileName = Path.GetFileName(path);
        if (_tagFilename.TryParse(fileName, out var baseName, out var extension, out var tags)
            && tags.Count > 0)
        {
            FileNamePrefix = baseName + "[";
            FileNameTagSegment = string.Join(" ", tags);
            FileNameSuffix = "]" + extension;
            // 显示名 = 剥离标签段（2026-09-19 统一口径：与瀑布流卡片一致；完整名进 tooltip）。
            CurrentImageDisplayName = baseName + extension;
        }
        else
        {
            // 无标签：完整文件名作为前缀，标签段与后缀为空。
            FileNamePrefix = fileName;
            FileNameTagSegment = string.Empty;
            FileNameSuffix = string.Empty;
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
        FileNamePrefix = string.Empty;
        FileNameTagSegment = string.Empty;
        FileNameSuffix = string.Empty;
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
        StatusText = string.Empty;
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

/// <summary>
/// 筛选条 chip 展示模型（spec Step 11）：「组名：标签名」+ 单删命令。
/// 不可变快照，经 MainViewModel.RebuildFilterChips 全量重建（对齐侧栏 chip 惯例）。
/// </summary>
public sealed class FilterChipViewModel
{
    public FilterChipViewModel(string groupName, string tagName, IAsyncRelayCommand removeFilterCommand)
    {
        GroupName = groupName;
        TagName = tagName;
        RemoveFilterCommand = removeFilterCommand;
    }

    /// <summary>标签所属组显示名（不属于任何配置组时为「未分组」）。</summary>
    public string GroupName { get; }

    /// <summary>标签名。</summary>
    public string TagName { get; }

    /// <summary>单删命令（从筛选集移除该标签并刷新瀑布流）。</summary>
    public IAsyncRelayCommand RemoveFilterCommand { get; }
}
