// 职责：WinUI 应用入口——组装服务、主窗口与启动初始化。
// 不变量：CLI（viewer <file> / -d -i）行为不变、不触发图库扫描；扫描仅由工具栏「打开图库」触发；
//         LibraryIndexService 待选定图库根目录后由 MainViewModel 延迟创建（按根路径派生库文件）。

using Microsoft.UI.Xaml;
using SimpleViewer.Helpers;
using SimpleViewer.Services;
using SimpleViewer.ViewModels;

namespace SimpleViewer;

/// <summary>
/// WinUI 应用入口：组装服务、主窗口与启动初始化。
/// </summary>
public partial class App : Application
{
    /// <summary>当前主窗口（WinRT 选择器与焦点检查用）。</summary>
    public static Window? CurrentWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settingsService = new SettingsService();
        settingsService.Load();

        var shortcutService = new ShortcutService(settingsService);
        var fileBrowser = new FileBrowserService();
        var imageLoader = new ImageLoaderService();
        var fileOperations = new FileOperationService();

        // 打标与图库服务（Step 7 组装；LibraryIndexService 由 MainViewModel 在选定图库根目录后延迟创建）。
        var tagFilenameService = new TagFilenameService();
        var tagService = new TagService(tagFilenameService);
        var scanService = new LibraryScanService(tagFilenameService);
        var thumbnailService = new ThumbnailService();

        var viewModel = new MainViewModel(
            fileBrowser,
            imageLoader,
            fileOperations,
            tagFilenameService,
            tagService,
            scanService,
            thumbnailService);

        var cli = new CommandLineService();
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var launchOptions = cli.Parse(argv);

        if (launchOptions.ShowHelp)
        {
            ConsoleHelper.WriteHelpAndExit(CommandLineService.GetHelpText());
        }

        var window = new MainWindow(viewModel, shortcutService, settingsService);
        CurrentWindow = window;
        window.Activate();

        _ = viewModel.InitializeAsync(launchOptions);
    }
}
