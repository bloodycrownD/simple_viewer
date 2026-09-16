// 职责：主查看器状态——单图导航/旋转/显示、双模式（图库/单图）互斥切换、图库扫描驱动、文件名标签分段。
// 不变量：Prev/Next 环绕且重置旋转；仅视口解码尺寸变化时重载；
//         ScanAsync 为同步磁盘 IO 迭代器，一律 Task.Run 后台消费、UI 线程仅经 Progress 收进度（几十万张不假死口径）；
//         扫描项暂存 List<GalleryItem>（不灌 ObservableCollection，几十万项会爆；Step 8 换虚拟化数据源）；
//         CLI/单图直开不触发图库扫描，扫描仅由「打开图库」触发。
// 调用链：App → MainWindow → MainViewModel → FileBrowser / ImageLoader / FileOperation / TagFilename / LibraryScan / LibraryIndex 服务。

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>查看模式（D14 双模式互斥切换）：Gallery=瀑布流图库，Single=单图查看。</summary>
public enum ViewerMode
{
    /// <summary>瀑布流图库模式（本步为占位壳与扫描状态，Step 8 接入瀑布流本体）。</summary>
    Gallery,

    /// <summary>单图查看模式。</summary>
    Single,
}

/// <summary>
/// 主窗口视图模型：单图查看状态、双模式切换与图库扫描驱动。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IFileBrowserService _fileBrowser;
    private readonly IImageLoaderService _imageLoader;
    private readonly IFileOperationService _fileOperations;
    private readonly ITagFilenameService _tagFilename;

    // TagService/ThumbnailService 由后续步骤消费（Step 10 打标 / Step 8 瀑布流缩略图）；
    // 本步先行注入以稳定构造签名，显式压制“赋值未使用”告警保住 0 警告口径。
#pragma warning disable CS0414
    private readonly ITagService _tagService;
    private readonly IThumbnailService _thumbnailService;
#pragma warning restore CS0414

    private readonly ILibraryScanService _scanService;

    private readonly List<string> _imageFiles = [];
    private int _currentIndex = -1;
    private int? _decodeSize;
    private int _lastAppliedDecodeSize = -1;
    private string? _currentDirectory;
    private CancellationTokenSource? _loadCts;

    // 图库状态：扫描项仅由后台任务写（Step 8 前无 UI 消费方，Step 8 起换虚拟化数据源接管）。
    private readonly List<GalleryItem> _galleryItems = [];
    private CancellationTokenSource? _scanCts;
    private ILibraryIndexService? _indexService;
    private string? _libraryRootPath;

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
        IsScanning = true;
        ScanStatusText = "扫描中 · 已发现 0 张";

        // Progress 构造于 UI 线程：Report 回调自动回投 UI 线程，仅更新状态文本。
        var progress = new Progress<int>(count => ScanStatusText = $"扫描中 · 已发现 {count} 张");

        try
        {
            // ScanAsync 为同步磁盘 IO 迭代器（MoveNextAsync 在消费线程上同步执行磁盘枚举）：
            // 必须 Task.Run 后台消费，UI 线程只收进度/结果——几十万张不假死的硬性口径。
            await Task.Run(async () =>
            {
                var chunk = new List<GalleryItem>(LibraryScanService.ChunkSize);
                await foreach (var item in _scanService.ScanAsync(root, progress, token))
                {
                    // 本阶段仅暂存 List（虚拟化数据源 Step 8 接管）；绝不灌 ObservableCollection。
                    _galleryItems.Add(item);
                    chunk.Add(item);
                    if (chunk.Count >= LibraryScanService.ChunkSize)
                    {
                        await indexService.UpsertChunkAsync(chunk, token);
                        chunk.Clear();
                    }
                }

                if (chunk.Count > 0)
                {
                    await indexService.UpsertChunkAsync(chunk, token);
                }
            }, token);

            ScanStatusText = $"共 {_galleryItems.Count} 张";
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
    /// 瀑布流模式且有选中集 → 清空选中并返回 true（选中集 Step 10 才有，本步预留分支）；
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

        if (CurrentMode == ViewerMode.Gallery)
        {
            // Step 10 接入：瀑布流选中集非空时清空选中并 return true；
            // 本步无选中集，落到下述 false（维持 ExitApp 原行为）。
        }

        return false;
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
        BackToGalleryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanStatusVisibility));
        OnPropertyChanged(nameof(GalleryHintVisibility));
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
