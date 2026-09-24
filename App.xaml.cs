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

    /// <summary>主窗口显示缩放（DPI/96，如 150% = 1.5；MainWindow 初始化时写入，缩略图分桶用）。</summary>
    public static double DisplayScale { get; internal set; } = 1.0;

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
        // 原“删除标签前绑定引用谓词”注入已删（batch-tag-management Step 2：删除纯化为配置操作，
        // 绑定引用拒绝前移 MainViewModel.IsTagReferencedByBindings）。
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

        StartUiHeartbeatWatchdog();

        // 无 CLI 参数且记录了上次图库根目录 → 自动恢复（目录失效则静默跳过）。
        // 冷启动口径不变：CLI（viewer <file> / -d -i）路径不触发扫描，仅此自动恢复路径例外。
        if (string.IsNullOrEmpty(launchOptions.FilePath)
            && string.IsNullOrEmpty(launchOptions.DirectoryPath))
        {
            var lastRoot = settingsService.Load().LastLibraryRoot;
            if (!string.IsNullOrWhiteSpace(lastRoot) && Directory.Exists(lastRoot))
            {
                // fire-and-forget 补异常观察（cr/P2-1）：启动恢复失败原先完全静默——
                // 异常死在任务里既无日志也无状态提示；ContinueWith 仅在故障时记诊断日志。
                _ = viewModel.OpenLibraryRootAsync(lastRoot).ContinueWith(
                    t => WriteDiagnosticLog($"[启动恢复上次图库失败] root={lastRoot}", t.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        _ = viewModel.InitializeAsync(launchOptions);
    }

    /// <summary>
    /// UI 心跳看门狗（2026-09-17 走查诊断）：UI 线程每秒自增心跳并记录自身托管堆栈快照；
    /// 后台线程每 5s 检查，连续 15s 无心跳视为无响应，把最近操作追踪 + 最后一份 UI 线程堆栈
    /// 转储进诊断日志（定位"打开图库卡死"类问题的现场）。每次无响应只记录一次，恢复后重置。
    /// 2026-09-24 卡死取证升级：100ms 栈快照只能拍到「空闲时刻」（快照在 timer 回调里拍，
    /// 真正的阻塞帧永远不会被拍到——此前日志里的「UI 线程堆栈」恒为 timer 自身栈，无现场价值）。
    /// 检测到无响应时由后台线程写进程 MiniDump（dbghelp MiniDumpWriteDump，单元无要求），
    /// 事后 dotnet-dump 分析 dump 即得 UI 线程真实阻塞栈。每次无响应周期至多一份 dump。
    /// </summary>
    private static void StartUiHeartbeatWatchdog()
    {
        var heartbeat = 0L;
        var lastUiStack = "<尚未采样>";
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            return;
        }

        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) =>
        {
            // 心跳同时留一份 UI 线程栈快照：卡死时它就是"卡在哪"的现场。
            // 100ms 间隔：走查发现首帧渲染期（<1s）即可冻死，1s 采样来不及。
            lastUiStack = Environment.StackTrace;
            Interlocked.Increment(ref heartbeat);
        };
        timer.Start();

        _ = Task.Run(async () =>
        {
            var lastSeen = Interlocked.Read(ref heartbeat);
            var stalled = 0;
            var dumpedThisStall = false;
            while (true)
            {
                await Task.Delay(5000);
                var current = Interlocked.Read(ref heartbeat);
                if (current == lastSeen)
                {
                    stalled++;
                    if (stalled == 3)
                    {
                        WriteDiagnosticLog(
                            $"[UI 无响应] 心跳停止 ≥15s（疑似卡死）。最近操作追踪：{Environment.NewLine}{DiagnosticTrace.Dump()}"
                            + $"{Environment.NewLine}最后一份 UI 线程堆栈（注意：快照仅能拍到空闲时刻，真实现场见 hang dump）：{Environment.NewLine}{lastUiStack}");
                    }

                    // ≥20s 仍无响应且本周期未 dump：写卡死现场 MiniDump（后台线程，UI 卡着不受影响）。
                    if (stalled >= 4 && !dumpedThisStall)
                    {
                        dumpedThisStall = true;
                        WriteHangDump();
                    }
                }
                else
                {
                    if (stalled >= 3)
                    {
                        WriteDiagnosticLog("[UI 恢复响应]");
                    }

                    stalled = 0;
                    dumpedThisStall = false;
                    lastSeen = current;
                }
            }
        });
    }

    /// <summary>卡死现场 MiniDump 落盘路径目录（与诊断日志同目录）。</summary>
    private static string HangDumpDirectory
        => Path.Combine(Path.GetDirectoryName(DiagnosticLogPath)!, "hangdumps");

    /// <summary>
    /// 后台线程写当前进程 MiniDump（dbghelp）：卡死现场的唯一可靠取证（100ms 栈快照拍不到阻塞帧）。
    /// WithPrivateReadWriteMemory 档含托管堆，事后 dotnet-dump analyze 可得全部线程真实栈。
    /// 失败静默（诊断代码不许再抛）；保留最近 5 份防磁盘膨胀。
    /// </summary>
    private static void WriteHangDump()
    {
        try
        {
            // 先落 %TEMP%（LocalAppData 下曾报 E_ACCESSDENIED 0x80070005，2026-09-24 首次实战未取到现场；
            // TEMP 权限最宽松，成功后再考虑回收站式挪到 logs 目录）。
            var dir = Path.Combine(Path.GetTempPath(), "SimpleViewerHangDumps");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            if (MiniDumpWriteDump(process.Handle, (uint)process.Id, path, MiniDumpWithPrivateReadWriteMemory,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                WriteDiagnosticLog($"[卡死现场 dump] {path}");
                foreach (var stale in Directory.GetFiles(dir, "hang-*.dmp")
                             .OrderByDescending(f => f)
                             .Skip(5))
                {
                    try { File.Delete(stale); } catch { /* 清理失败忽略 */ }
                }
            }
            else
            {
                WriteDiagnosticLog($"[卡死现场 dump 失败] GetLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog("[卡死现场 dump 异常]", ex);
        }
    }

    private const uint MiniDumpWithPrivateReadWriteMemory = 0x00000200;

    [System.Runtime.InteropServices.DllImport("dbghelp.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string dumpPath,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
