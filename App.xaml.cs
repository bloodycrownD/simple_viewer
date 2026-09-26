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

    /// <summary>
    /// 滚轮缩放诊断开关（排查「放大不动点」用）：置环境变量 SIMPLEVIEWER_ZOOM_DIAG=1 时，
    /// 每次滚轮缩放向 startup.log 落一行锚点/元素盒/变换现场；默认关（正常使用零开销）。
    /// </summary>
    public static bool ZoomDiagnosticsEnabled { get; } =
        Environment.GetEnvironmentVariable("SIMPLEVIEWER_ZOOM_DIAG") == "1";

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

    /// <summary>心跳计时器根引用（DispatcherQueueTimer 不被队列强持有，GC 后静默停跳）。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _heartbeatTimer;

    /// <summary>
    /// UI 心跳看门狗（2026-09-17 走查诊断）：UI 线程每秒自增心跳并记录自身托管堆栈快照；
    /// 后台线程每 5s 检查，连续 15s 无心跳视为无响应，把最近操作追踪 + 最后一份 UI 线程堆栈
    /// 转储进诊断日志（定位"打开图库卡死"类问题的现场）。每次无响应只记录一次，恢复后重置。
    /// 2026-09-24 卡死取证升级：100ms 栈快照只能拍到「空闲时刻」（快照在 timer 回调里拍，
    /// 真正的阻塞帧永远不会被拍到——此前日志里的「UI 线程堆栈」恒为 timer 自身栈，无现场价值）。
    /// 检测到无响应时由后台线程双路取证：①外部 dotnet-stack（诊断管道）抓全部托管线程真实栈；
    /// ②dbghelp MiniDumpWriteDump 兜底（本机安全软件拦截概率高）。每次无响应周期至多一套。
    /// 同线程每 30s 采样 GC 停顿数据（代数/提交量/停顿占比）——2026-09-24 实机多次无响应的
    /// 现场指纹是「池线程解码打点与 UI 心跳同时静默」= 全进程托管线程齐停，GC 全停是头号嫌疑，
    /// 此采样即裁决数据（环形追踪常驻、Gen2 变化/停顿抬升时落 startup.log）。
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

        // 根引用（2026-09-24 看门狗假警报根治）：DispatcherQueueTimer 不被队列强持有，
        // 局部变量在冷启动分配风暴中被 GC 后计时器静默停跳——此后每次会话都在 ~15-20s 处
        // 报一条假「UI 无响应」（当日 43 条假警报、恢复条目 0 条的实锤），且真正的后续卡死
        // 反而全部漏报。静态根引用保证心跳与会话同生命周期。
        _heartbeatTimer = timer;

        _ = Task.Run(async () =>
        {
            var lastSeen = Interlocked.Read(ref heartbeat);
            var stalled = 0;
            var dumpedThisStall = false;

            // GC 停顿采样（2026-09-24 用户实机"卡顿是不是 GC"取证）：30s 一次把 GC 代数/提交量/
            // 停顿占比写入环形追踪（无响应转储时 GC 现场随之可见）；Gen2 新增或停顿占比抬升时
            // 同步落 startup.log——今夜 7 次无响应的环形缓冲均呈「解码打点（池线程）与 UI 心跳
            // 同时静默」= 全进程托管线程齐停的 GC 全停指纹，此采样即该假说的裁决数据。
            var lastGen2 = -1;
            var lastPausePct = -1.0;
            var gcTick = 0;

            while (true)
            {
                await Task.Delay(5000);
                var current = Interlocked.Read(ref heartbeat);

                gcTick++;
                if (gcTick >= 6)
                {
                    gcTick = 0;
                    try
                    {
                        var gen2 = GC.CollectionCount(2);
                        var info = GC.GetGCMemoryInfo();
                        var pausePct = Math.Round(info.PauseTimePercentage, 2);
                        var recentPauses = FormatRecentGcPauses(info);
                        var line = $"gc g0/g1/g2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{gen2}"
                            + $" committed={info.TotalCommittedBytes >> 20}MB"
                            + $" pause%={pausePct:F2} last5=[{recentPauses}]ms";
                        DiagnosticTrace.Mark(line);
                        if (gen2 != lastGen2 || Math.Abs(pausePct - lastPausePct) >= 0.5)
                        {
                            lastGen2 = gen2;
                            lastPausePct = pausePct;
                            WriteDiagnosticLog($"[GC 采样] {line}");
                        }
                    }
                    catch
                    {
                        // 诊断代码静默
                    }
                }

                if (current == lastSeen)
                {
                    stalled++;
                    if (stalled == 3)
                    {
                        WriteDiagnosticLog(
                            $"[UI 无响应] 心跳停止 ≥15s（疑似卡死）。最近操作追踪：{Environment.NewLine}{DiagnosticTrace.Dump()}"
                            + $"{Environment.NewLine}最后一份 UI 线程堆栈（注意：快照仅能拍到空闲时刻，真实现场见 hang 栈/dump）：{Environment.NewLine}{lastUiStack}");
                    }

                    // ≥20s 仍无响应且本周期未取证：外部 dotnet-stack 抓全部托管线程栈（诊断管道，
                    // 不依赖 dbghelp——本机 MiniDumpWriteDump 五连败 E_HANDLE/E_ACCESSDENIED，实测
                    // dotnet-stack/dotnet-dump 经诊断管道可用），MiniDump 兜底保留。
                    if (stalled >= 4 && !dumpedThisStall)
                    {
                        dumpedThisStall = true;
                        CaptureHangStacks();
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

    /// <summary>
    /// 外部 dotnet-stack 抓当前进程全部托管线程栈到日志目录（2026-09-24）：UI 心跳停止时唯一能拍到
    /// 「阻塞帧」的取证（100ms 栈快照在 timer 回调里拍，真正的阻塞帧永远拍不到；dbghelp 自 dump 被
    /// 本机安全软件拦）。dotnet-stack 为全局 dotnet 工具（%USERPROFILE%\.dotnet\tools），不在 PATH
    /// 或超时则静默失败，MiniDump 兜底。卡死期间后台线程照常运行，此调用不受 UI 阻塞影响。
    /// </summary>
    private static void CaptureHangStacks()
    {
        try
        {
            var pid = Environment.ProcessId;
            Directory.CreateDirectory(HangDumpDirectory);
            var path = Path.Combine(HangDumpDirectory, $"hang-stack-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet-stack",
                Arguments = $"report -p {pid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                WriteDiagnosticLog("[卡死现场托管栈失败] Process.Start 返回 null（dotnet-stack 不可用？）");
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45_000))
            {
                try { process.Kill(); } catch { /* 尽力回收 */ }
                WriteDiagnosticLog("[卡死现场托管栈失败] dotnet-stack 超时 45s");
                return;
            }

            var stdout = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;
            File.WriteAllText(path, stdout + Environment.NewLine + "--- stderr ---" + Environment.NewLine + stderr);
            WriteDiagnosticLog($"[卡死现场托管栈] exit={process.ExitCode} bytes={stdout.Length} {path}");
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog("[卡死现场托管栈异常]", ex);
        }
    }

    /// <summary>格式化最近 5 次 GC 停顿（毫秒）。PauseDurations 是 ReadOnlySpan（ref 结构体），
    /// 不得进入 async 方法体——提取为本同步辅助。</summary>
    private static string FormatRecentGcPauses(System.GCMemoryInfo info)
    {
        try
        {
            var pauses = info.PauseDurations;
            var last = pauses.Length <= 5 ? pauses.ToArray() : pauses[^5..].ToArray();
            return string.Join(",", last.Select(d => $"{d.TotalMilliseconds:F0}"));
        }
        catch
        {
            return "?";
        }
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
