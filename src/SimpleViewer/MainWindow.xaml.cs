using Microsoft.UI.Xaml;
using SimpleViewer.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SimpleViewer;

/// <summary>
/// Primary shell window hosting the image viewer UI and view model bindings.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinWidth = 800;
    private const int MinHeight = 600;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.PickImageFileAsync = PickImageFileAsync;
        ViewModel.FullscreenChanged += OnFullscreenChanged;

        InitializeComponent();
        ConfigureWindowChrome();
        ApplySystemBackdrop();
    }

    private void ConfigureWindowChrome()
    {
        const int defaultWidth = 1280;
        const int defaultHeight = 800;

        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(defaultWidth, defaultHeight));
        appWindow.Changed += OnAppWindowChanged;

        Title = "Simple Viewer";
    }

    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        if (size.Width >= MinWidth && size.Height >= MinHeight)
        {
            return;
        }

        sender.Resize(new Windows.Graphics.SizeInt32(
            Math.Max(size.Width, MinWidth),
            Math.Max(size.Height, MinHeight)));
    }

    private void ApplySystemBackdrop()
    {
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = (int)e.NewSize.Width;
        var height = (int)e.NewSize.Height;
        _ = ViewModel.OnViewportSizeChangedAsync(width, height);
    }

    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        AppWindow.SetPresenter(
            isFullscreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
    }

    private async Task<string?> PickImageFileAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };

        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".gif");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
