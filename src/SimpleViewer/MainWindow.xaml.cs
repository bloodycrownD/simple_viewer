using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SimpleViewer;

/// <summary>
/// Primary shell window. Hosts the image viewer UI in later phases.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinWidth = 800;
    private const int MinHeight = 600;

    public MainWindow()
    {
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

        // Enforce minimum client size (spec: 800×600) when platform presenter lacks min-size API.
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
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }
}
