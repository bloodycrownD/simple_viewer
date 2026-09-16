// 职责：主查看器状态——单图导航/旋转/显示、双模式（图库/单图）互斥切换、图库扫描驱动、文件名标签分段、
//       瀑布流数据源驱动（渐进追加）与卡片选中集、标签筛选（OR 语义）与标签栏数据/编辑执行（Step 9）。
// 不变量：Prev/Next 环绕且重置旋转；仅视口解码尺寸变化时重载；
//         ScanAsync 为同步磁盘 IO 迭代器，一律 Task.Run 后台消费、UI 线程仅经 Progress 收进度/扫描块（几十万张不假死口径）；
//         扫描块经 Progress 回投 UI 线程后追加进 WaterfallViewModel（虚拟化数据源，绝不一次性同步灌入）；
//         卡片选中集状态在本类（WaterfallViewModel 仅转发）；Ctrl/Shift 连选与 Ctrl+A 属 Step 10；
//         标签筛选集与命中数在本类（筛选条 UI 属 Step 11，最小反馈 = 侧栏 chip 高亮 + 状态行命中数）；
//         扫描期间追加的块经筛选谓词过滤后入瀑布流（筛选态与渐进追加互不干扰）；
//         标签/组编辑（重命名/删除）前置 ValidateTagGroups 预检（同口径）再动文件，避免"文件已改、配置被拒"分裂；
//         侧栏重建（ObservableCollection 写）一律经 DispatcherQueue 回投 UI 线程；
//         CLI/单图直开不触发图库扫描，扫描仅由「打开图库」触发。
// 调用链：App → MainWindow → MainViewModel → FileBrowser / ImageLoader / FileOperation / TagFilename / TagService /
//         LibraryScan / LibraryIndex / Settings / Thumbnail 服务。

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
using SimpleViewer.Services;
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

    // TagService 由 Step 10 批量打标消费；本步先行注入以稳定构造签名，
    // 显式压制“赋值未使用”告警保住 0 警告口径。
#pragma warning disable CS0414
    private readonly ITagService _tagService;
#pragma warning restore CS0414

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
    private const int TagDataRefreshIntervalMs = 1500;

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

    /// <summary>宿主提供的图库根目录选择器（FolderPicker）；由 <see cref="MainWindow"/> 注入。</summary>
    public Func<Task<string?>>? PickLibraryFolderAsync { get; set; }

    /// <summary>宿主提供的删除确认对话框；返回 true 表示继续删除。</summary>
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>宿主提供的设置对话框打开回调。</summary>
    public Func<Task>? OpenSettingsAsync { get; set; }

    /// <summary><see cref="IsFullscreen"/> 变化时通知窗口切换 chrome。</summary>
    public event EventHandler<bool>? FullscreenChanged;

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

    public bool CanNavigateImages => _imageFiles.Count > 0;

    /// <summary>当前图库根目录（未打开图库时为 null）。</summary>
    public string? LibraryRootPath => _libraryRootPath;

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
    /// </summary>
    public async Task OnViewportSizeChangedAsync(int width, int height)
    {
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
    /// 扫描仅由此命令触发（CLI/单图直开绝不扫描，保冷启动口径）。
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

        await StartLibraryScanAsync(folder);
    }

    /// <summary>从瀑布流进入单图模式（Step 8 双击卡片入口）：加载目标图片并切换为 Single。</summary>
    public async Task OpenImageAsSingle(GalleryItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        await OpenAsync(item.Path);
        if (HasImage)
        {
            CurrentMode = ViewerMode.Single;
        }
    }

    /// <summary>
    /// 启动/重启图库递归扫描：取消既有扫描、按根目录重建索引服务、后台消费扫描流。
    /// </summary>
    private async Task StartLibraryScanAsync(string root)
    {
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

        _galleryItems.Clear();
        ClearCardSelection();
        _activeFilterTags.Clear();
        _latestTagCounts = new Dictionary<string, int>();
        _waterfall.ResetFrom([]);
        WaterfallEmptyText = string.Empty;
        IsScanning = true;
        ScanStatusText = "扫描中 · 已发现 0 张";

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
                await foreach (var item in _scanService.ScanAsync(root, progress, token))
                {
                    // 全量暂存 List + 分块 upsert 索引；瀑布流经 chunkProgress 渐进追加（UI 线程）。
                    _galleryItems.Add(item);
                    chunk.Add(item);
                    if (chunk.Count >= LibraryScanService.ChunkSize)
                    {
                        await indexService.UpsertChunkAsync(chunk, token);
                        // Report 引用会被异步消费，复用 List 前必须快照。
                        chunkProgress.Report(chunk.ToArray());
                        chunk.Clear();

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
                    chunkProgress.Report(chunk.ToArray());
                }
            }, token);

            ScanStatusText = $"共 {_galleryItems.Count} 张";
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

    /// <summary>卡片单击 = 选中/取消选中（WaterfallViewModel 转发；Ctrl/Shift 连选与 Ctrl+A 属 Step 10）。</summary>
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

    /// <summary>清空卡片选中集（Esc 路由 / 筛切换 / 重开图库）。</summary>
    public void ClearCardSelection()
    {
        foreach (var viewModel in _selectedCards)
        {
            viewModel.IsSelected = false;
        }

        _selectedCards.Clear();
        SelectedCardCount = 0;
    }

    // ==================== 标签筛选（Step 9：点击侧栏标签 = 切换筛选，OR 语义） ====================

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

    /// <summary>
    /// 点击侧栏标签 = 切换筛选：再次点击取消；多标签 OR 语义。
    /// 筛选态下瀑布流只显示命中；命中数经状态行反馈（筛选条 UI 属 Step 11）。
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

    /// <summary>重建侧栏（读配置组 + 计数快照 + 筛选高亮；ObservableCollection 写操作回投 UI 线程）。</summary>
    private void RebuildTagSidebar()
    {
        var configGroups = _settingsService?.Load().TagGroups ?? [];
        var counts = _latestTagCounts;
        var filters = _activeFilterTags;

        void Rebuild() => TagSidebar.Rebuild(configGroups, counts, filters);
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Rebuild);
        }
        else
        {
            Rebuild();
        }
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

    /// <summary>组「互斥 ⇄ 多选」切换：仅改 TagGroups 配置并保存，不改任何已落盘标签。</summary>
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
        var failures = 0;
        foreach (var candidate in candidates)
        {
            var newPath = TryBuildNewPath(candidate, transform);
            if (newPath is null || File.Exists(candidate.Path) || !File.Exists(newPath))
            {
                if (File.Exists(candidate.Path))
                {
                    failures++; // 未改名（批量失败明细之一；TagService 回执已聚合原因）。
                }

                continue;
            }

            var newItem = BuildGalleryItem(newPath, candidate);
            await _indexService.ReplacePathAsync(candidate.Path, newItem);
            ReplaceGalleryItemState(candidate.Path, newItem);
        }

        StatusText = result.SucceededCount > 0 || failures > 0
            ? $"{statusPrefix}：成功 {result.SucceededCount} 张，失败 {failures} 张（失败项可重试）"
            : string.Empty;

        // 索引已同步：刷新计数快照并重建侧栏（后续配置保存路径的 Rebuild 复用新快照）。
        await RefreshTagDataAsync();
        return null;
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

    /// <summary>预测重命名后的新全路径（TagFilenameService.BuildNewPath 预检；预测失败返回 null）。</summary>
    private string? TryBuildNewPath(GalleryItem item, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var fileName = Path.GetFileName(item.Path);
        if (!_tagFilename.TryParse(fileName, out _, out _, out var tags))
        {
            return null;
        }

        var build = _tagFilename.BuildNewPath(item.Path, transform(tags));
        return build.Success ? build.NewFullPath : null;
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

    /// <summary>同步全量暂存列表与瀑布流卡片（就地替换，保滚动位置与选中态）。</summary>
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

        _waterfall.UpdateItem(oldPath, newItem);
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

    /// <summary>图库状态行文本：扫描/共 N 张 + 筛选命中 + 已选 N 张（最小可见反馈；筛选条 UI 属 Step 11）。</summary>
    public string GalleryStatusText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ScanStatusText))
            {
                parts.Add(ScanStatusText);
            }

            if (IsTagFilterActive)
            {
                parts.Add($"筛选命中 {_waterfall.Items.Count} 张（任一命中 · {_activeFilterTags.Count} 个标签）");
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
    }

    partial void OnSelectedCardCountChanged(int value)
    {
        OnPropertyChanged(nameof(GalleryStatusText));
    }

    partial void OnWaterfallEmptyTextChanged(string value)
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
    }

    /// <summary>瀑布流项集合变化（渐进追加/重置/就地替换）时刷新空态可见性与状态行命中数。</summary>
    private void OnWaterfallItemsChanged()
    {
        OnPropertyChanged(nameof(WaterfallEmptyVisibility));
        OnPropertyChanged(nameof(GalleryStatusText));
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

        ReleaseCurrentImageSource();

        var path = _imageFiles[_currentIndex];
        try
        {
            var loaded = await _imageLoader.LoadAsync(path, _decodeSize, rotationBucket: 0, token);
            token.ThrowIfCancellationRequested();

            ImageSource = ImageSourceHelper.FromLoadedImage(loaded);
            HasImage = true;
            _lastAppliedDecodeSize = _decodeSize ?? 0;
            UpdateStatusText(loaded);

            _imageLoader.PrefetchAdjacent(_imageFiles, _currentIndex, _decodeSize);
        }
        catch (OperationCanceledException)
        {
            // 已被更新的导航或尺寸重载取代。
        }
        catch (Exception ex)
        {
            ImageSource = null;
            HasImage = false;
            ClearFileNameSegments();
            StatusText = $"加载失败：{ex.Message}";
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

    private void UpdateStatusText(LoadedImage loaded)
    {
        var fileName = Path.GetFileName(loaded.Path);
        var sizeText = FormatFileSize(loaded.FileSizeBytes);
        var dimensions = $"{loaded.PixelWidth}x{loaded.PixelHeight}";
        var indexInfo = $"{_currentIndex + 1}/{_imageFiles.Count}";
        StatusText = $"名称：{fileName} | 大小：{sizeText} | 尺寸：{dimensions} | 序号：{indexInfo}";
        UpdateFileNameSegments(loaded.Path);
    }

    /// <summary>
    /// 依据文件名尾部标签段计算三段式显示信息（prefix + 标签段 + suffix），
    /// 供单图视图完整显示文件名并高亮方括号标签段（Step 7 口径）。
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
        }
        else
        {
            // 无标签：完整文件名作为前缀，标签段与后缀为空。
            FileNamePrefix = fileName;
            FileNameTagSegment = string.Empty;
            FileNameSuffix = string.Empty;
        }
    }

    private void ClearFileNameSegments()
    {
        FileNamePrefix = string.Empty;
        FileNameTagSegment = string.Empty;
        FileNameSuffix = string.Empty;
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
        ClearFileNameSegments();
        StatusText = string.Empty;
        RotationAngle = 0;
        _currentIndex = -1;
        _imageFiles.Clear();
        _currentDirectory = null;
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private static int? CalculateDecodeSize(int viewportWidth, int viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        const int chromeHeight = 96;
        var contentHeight = Math.Max(1, viewportHeight - chromeHeight);
        var contentWidth = Math.Max(1, viewportWidth);
        return Math.Max(contentWidth, contentHeight);
    }
}
