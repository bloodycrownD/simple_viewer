using Microsoft.UI.Xaml.Media;

namespace SimpleViewer.Helpers;

/// <summary>
/// XAML 图像源的 UI 线程确定性退役队列（2026-09-24 崩溃根治）。
/// 背景：BitmapImage / SoftwareBitmapSource 属 DependencyObject 族，native 析构必须发生在
/// 创建线程（UI STA）。此前旧源（瀑布流缩略图回收、单图换源替换）仅「置 null 失去引用」，
/// 由 GC 终结器线程执行 <c>WinRT.IObjectReference.Finalize</c> 跨线程 Release——大图库打标
/// 重排时成批退休触发终结风暴，实机 native 堆损坏闪退（fastfail 0xc0000409 + 托管堆不可遍历
/// + 终结器线程停在 Finalize 的 dump 实锤；另一表现为 XAML stowed 0xc000027b）。
/// 约定：所有调用（<see cref="Retire"/> 与 <see cref="Drain"/>）必须在 UI 线程——调用点
/// （ReleaseVisuals/单图换源续体）本身在 UI 线程。退役源入队持强引用（GC 永不触碰），
/// <see cref="Drain"/> 超出保留窗口的旧源在 UI 线程显式 Dispose（合成器已换帧，纹理不再被引用）。
/// 保留语义（2026-09-26 tag-op-finalizer-crash 修复 E：双缓冲边界，取代原「计数窗口」）：
/// 原实现是纯计数窗口（Drain 时 <c>Count &gt; KeepCount</c> 即 Dispose 队头）——一次批量退役
/// （筛选 Reset / 打开图库的整体重置）会立刻 Dispose 掉超窗的几十个源，只留最后 8 个，
/// 「余量」由队列长度而非「帧/操作边界」保证，批量场景下等于没有余量。
/// 现语义：**只释放「上一次 Drain 时已在队列中」且超出保留窗口的条目**——每个源至少活过一次
/// Drain 边界（一次操作边界 = 合成器换帧窗口），此后按 FIFO 释放；队列峰值 ≤ KeepCount + 本轮新增。
/// </summary>
internal static class ImageSourceRetirement
{
    /// <summary>保留窗口：最近若干个退休源暂不 Dispose（合成器异步上传/换帧的安全余量）。</summary>
    private const int KeepCount = 8;

    /// <summary>
    /// 队列硬上限（防御性，非正常路径）：<see cref="Retire"/> 与 <see cref="Drain"/> 约定成对调用
    /// （全部现有调用点已配对），故队列峰值 ≤ KeepCount + 本轮新增。若未来出现「只 Retire 不 Drain」
    /// 的路径，队列会无界增长（每个源承载的位图可达数百 KB）——超上限时按 FIFO 释放最老条目
    /// （牺牲一个边界的余量保底不泄漏）并落一条诊断日志，让约定破坏在 startup.log 可见。
    /// </summary>
    private const int HardCap = 512;

    private static readonly Queue<ImageSource> _pending = new();

    /// <summary>
    /// 队列头部「已跨过一次 Drain 边界」的条目数（双缓冲语义的唯一状态）：
    /// Drain 只在这段窗口内释放超窗条目；Drain 收尾把全部剩余条目纳入本窗口（它们就此跨过本次边界）。
    /// 不变量：0 ≤ _seenCount ≤ _pending.Count（Drain 末尾取等，之后只增不减由 Retire 抬高 Count）。
    /// </summary>
    private static int _seenCount;

    /// <summary>累计退役入队数（诊断用；只增）。</summary>
    private static long _retiredCount;

    /// <summary>累计显式 Dispose 数（诊断用；只增）。</summary>
    private static long _releasedCount;

    /// <summary>队列统计快照（入队数, 释放数, 当前队列长, 当前边界窗口长）——诊断埋点用。</summary>
    internal static (long Retired, long Released, int Pending, int Seen) Snapshot()
        => (_retiredCount, _releasedCount, _pending.Count, _seenCount);

    /// <summary>退役一个图像源（可空安全）。之后调用 <see cref="Drain"/> 收尾超窗旧源。</summary>
    internal static void Retire(ImageSource? source)
    {
        // 防御断言（2026-09-24 审计）：静态队列零防护，未来若在池线程误调即引入跨线程 Dispose——
        // Debug 构建立即暴露，Release 静默（与既有四个接入点的 UI 线程约定一致）。
        System.Diagnostics.Debug.Assert(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not null,
            "ImageSourceRetirement 必须在 UI 线程调用（XAML 对象须在创建线程析构）");
        if (source is not null)
        {
            _pending.Enqueue(source);
            _retiredCount++;
        }
    }

    /// <summary>
    /// 收尾：把「上一次 Drain 时已在队列中」且超出保留窗口的退休源在 UI 线程显式 Dispose（IClosable）。
    /// 双缓冲推导（2026-09-26 修复 E，勿改回计数窗口）：
    ///   设 Drain 时刻队列 = [已跨过上次边界的 S 条（头部）] + [自上次 Drain 起新入队的 N 条（尾部）]。
    ///   ① 本轮只从头部 S 段释放超出 KeepCount 的部分（FIFO：老的最先走，最新 KeepCount 条继续留）；
    ///   ② 尾部 N 条一律不释放——它们尚未跨过任何边界，至少活到下一次 Drain；
    ///   ③ 收尾把剩余全部条目计入边界窗口（_seenCount = Count），于是下一轮它们成为「可释放」的 S 段。
    ///   由 ③ 归纳可得：任一源从入队到被 Dispose 至少经历一次 Drain 边界（一次操作边界 = 换帧窗口），
    ///   批量退役（如 ResetFrom 逐项 ReleaseVisuals）不再出现「只留最后 8 条、其余立即 Dispose」。
    ///   队列有界性：③ 保证边界窗口内保留 ≤ KeepCount 条，加上本轮新增 N 条，峰值 ≤ KeepCount + N。
    /// </summary>
    internal static void Drain()
    {
        System.Diagnostics.Debug.Assert(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not null,
            "ImageSourceRetirement 必须在 UI 线程调用（XAML 对象须在创建线程析构）");

        while (_seenCount > KeepCount)
        {
            Release(_pending.Dequeue());
            _seenCount--;
        }

        // 约定破坏兜底（见 HardCap 注释）：只 Retire 不 Drain 的路径会让队列无界增长。
        if (_pending.Count > HardCap)
        {
            App.WriteDiagnosticLog(
                $"[退役队列超上限] pending={_pending.Count} > {HardCap}——Retire 与 Drain 未成对调用？"
                + "按 FIFO 释放最老条目保底（ImageSourceRetirement 调用约定见类注释）。");
            while (_pending.Count > HardCap)
            {
                Release(_pending.Dequeue());
                if (_seenCount > 0)
                {
                    _seenCount--;
                }
            }
        }

        // 本次边界：全部剩余条目（含本轮新增）就此跨过一次操作边界。
        _seenCount = _pending.Count;
    }

    /// <summary>显式 Dispose 一个退役源（IClosable；计数供诊断）。</summary>
    private static void Release(ImageSource source)
    {
        (source as IDisposable)?.Dispose();
        _releasedCount++;
    }
}
