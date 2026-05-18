// Responsibility: Main viewer state, navigation, rotation, and image display binding.
// Invariants: Prev/Next wrap and reset rotation; decode reload only when viewport decode size changes.
// Call chain: App → MainWindow → MainViewModel → FileBrowser / ImageLoader / FileOperation services.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>
/// View model for the primary image viewer window.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IFileBrowserService _fileBrowser;
    private readonly IImageLoaderService _imageLoader;
    private readonly IFileOperationService _fileOperations;

    private readonly List<string> _imageFiles = [];
    private int _currentIndex = -1;
    private int? _decodeSize;
    private int _lastAppliedDecodeSize = -1;
    private string? _currentDirectory;
    private CancellationTokenSource? _loadCts;

    public MainViewModel(
        IFileBrowserService fileBrowser,
        IImageLoaderService imageLoader,
        IFileOperationService fileOperations)
    {
        _fileBrowser = fileBrowser;
        _imageLoader = imageLoader;
        _fileOperations = fileOperations;
    }

    /// <summary>Host-provided file picker (WinRT); set by <see cref="MainWindow"/>.</summary>
    public Func<Task<string?>>? PickImageFileAsync { get; set; }

    /// <summary>Host-provided delete confirmation; returns true to proceed.</summary>
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    /// <summary>Host-provided settings dialog opener.</summary>
    public Func<Task>? OpenSettingsAsync { get; set; }

    /// <summary>Raised when <see cref="IsFullscreen"/> changes so the window can update chrome.</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>Raised when the user triggers the ExitApp shortcut.</summary>
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

    public bool CanNavigateImages => _imageFiles.Count > 0;

    /// <summary>
    /// Applies launch options (file or directory+index). Help is handled in App before the window opens.
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
        await LoadCurrentAsync();
    }

    /// <summary>
    /// Opens a file: refreshes the directory list (natural sort) and displays it.
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
        await LoadCurrentAsync();
    }

    /// <summary>
    /// Updates viewport dimensions and reloads when decode size changes (fit-to-window).
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
    /// Moves the current image to <paramref name="destinationDirectory"/> and shows the next image.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task MoveToFolderAsync(string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            StatusText = "Move to folder: no target path configured.";
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
            StatusText = $"Move failed: {ex.Message}";
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

    /// <summary>Requests application exit (shortcut or future menu).</summary>
    public void RequestExit()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        FullscreenChanged?.Invoke(this, value);
    }

    public Visibility ImageVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => HasImage ? Visibility.Collapsed : Visibility.Visible;

    partial void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
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
            // Superseded by a newer navigation or resize reload.
        }
        catch (Exception ex)
        {
            ImageSource = null;
            HasImage = false;
            StatusText = $"Load failed: {ex.Message}";
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
        StatusText = $"Name: {fileName} | Size: {sizeText} | Dimensions: {dimensions} | Index: {indexInfo}";
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
