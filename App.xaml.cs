// 职责：WinUI 应用入口——组装服务、主窗口与启动初始化。
// 不变量：CLI（viewer <file> / -d -i）行为不变、不触发图库扫描；扫描仅由工具栏「打开图库」触发；
//         LibraryIndexService 待选定图库根目录后由 MainViewModel 延迟创建（按根路径派生库文件）。

using Microsoft.UI.Xaml;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
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

    /// <summary>诊断日志路径（启动与未处理异常落盘，便于崩溃定位）。</summary>
    public static string DiagnosticLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleViewer", "logs", "startup.log");

    public App()
    {
        InitializeComponent();

        // 全局未处理异常落盘：WinUI 异步续体的托管异常默认以 0xc000027b 沉默崩溃，
        // 不接住就拿不到堆栈（现场诊断与后续线上排障都依赖这份日志）。
        UnhandledException += (s, e) =>
        {
            WriteDiagnosticLog($"[UnhandledException] {e.Message}", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteDiagnosticLog("[UnobservedTaskException]", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>追加诊断日志（含内层异常链与堆栈；失败静默——诊断代码不许再抛）。</summary>
    public static void WriteDiagnosticLog(string headline, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLogPath)!);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {headline}");
            for (var ex = exception; ex is not null; ex = ex.InnerException)
            {
                sb.AppendLine($"  {ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine(ex.StackTrace ?? "  <无堆栈>");
            }
            File.AppendAllText(DiagnosticLogPath, sb.ToString());
        }
        catch
        {
            // 诊断日志失败静默
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LaunchCore(args);
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog("[OnLaunched 致命异常]", ex);
            throw;
        }
    }

    private void LaunchCore(LaunchActivatedEventArgs args)
    {
        var settingsService = new SettingsService();
        settingsService.Load();

        var shortcutService = new ShortcutService(settingsService);
        var fileBrowser = new FileBrowserService();
        var imageLoader = new ImageLoaderService();
        var fileOperations = new FileOperationService();

        // 打标与图库服务（Step 7 组装；LibraryIndexService 由 MainViewModel 在选定图库根目录后延迟创建）。
        var tagFilenameService = new TagFilenameService();

        // Step 12：删除标签前的“被快捷键绑定引用”谓词接线（读设置绑定列表判断 TagId 引用；
        // 删除操作低频，直接 Load 读盘即可，与 TagService 契约一致）。
        var tagService = new TagService(
            tagFilenameService,
            isTagReferencedByBindings: tagId => settingsService.Load().Shortcuts.Any(b =>
                b.Command == ViewerCommand.ApplyTag
                && string.Equals(b.TagId, tagId, StringComparison.Ordinal)));
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
