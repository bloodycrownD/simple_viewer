using Microsoft.UI.Xaml;
using SimpleViewer.Models;
using SimpleViewer.Services;
using SimpleViewer.ViewModels;

namespace SimpleViewer;

/// <summary>
/// WinUI application entry point. Wires services, main window, and launch initialization.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var fileBrowser = new FileBrowserService();
        var imageLoader = new ImageLoaderService();
        var fileOperations = new FileOperationService();
        var viewModel = new MainViewModel(fileBrowser, imageLoader, fileOperations);

        // CLI help/console output is phase 5; parse now for directory/file launch only.
        var cli = new CommandLineService();
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var launchOptions = cli.Parse(argv);

        if (launchOptions.ShowHelp)
        {
            // TODO(phase-5): AttachConsole and print CommandLineService.GetHelpText(), then exit.
            launchOptions = new LaunchOptions();
        }

        var window = new MainWindow(viewModel);
        window.Activate();

        _ = viewModel.InitializeAsync(launchOptions);
    }
}
