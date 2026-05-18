using Microsoft.UI.Xaml;

namespace SimpleViewer;

/// <summary>
/// WinUI application entry point. CLI parsing and window creation are wired in later phases.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}
