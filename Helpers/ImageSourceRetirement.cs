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
/// </summary>
internal static class ImageSourceRetirement
{
    /// <summary>保留窗口：最近若干个退休源暂不 Dispose（合成器异步上传/换帧的安全余量）。</summary>
    private const int KeepCount = 8;

    private static readonly Queue<ImageSource> _pending = new();

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
        }
    }

    /// <summary>收尾：超出保留窗口的退休源在 UI 线程显式 Dispose（IClosable）。</summary>
    internal static void Drain()
    {
        while (_pending.Count > KeepCount)
        {
            (_pending.Dequeue() as IDisposable)?.Dispose();
        }
    }
}
