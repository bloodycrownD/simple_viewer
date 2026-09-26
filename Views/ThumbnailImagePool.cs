// 职责：瀑布流缩略图 BitmapImage 的 UI 线程有界对象池（2026-09-26 xaml-finalizer-residuals 修复 P0-1）——
//       卡片每次 Realize 不再「new BitmapImage」，ReleaseVisuals 退役改为还池复用。
// 背景：BitmapImage 不实现 IClosable/IDisposable（WinUI 3 投影中 BitmapImage/BitmapSource/ImageSource
//       均无 Close/Dispose 成员，仅 SoftwareBitmapSource 有 Close/Dispose）——ImageSourceRetirement.Release
//       的 (source as IDisposable)?.Dispose() 对它恒为 null：退役队列只是把「裸交 GC」的时刻往后挪过
//       保留窗口，引用一落仍由 GC 终结器线程执行 WinRT.IObjectReference.Finalize 的 native Release。
//       瀑布流缩略图是最大的 churn 源（每张卡片每次 Realize 一张，滚动/筛选 ResetFrom 批量回收），
//       长会话累积即触发 native 堆损坏（fastfail 0xc0000409 @ ucrtbase，终结器栈实锤）。
//       对象池让实例终身被强引用持有——终结器永不运行；解码纹理经还池 UriSource = null 显式释放。
// 设计约束（勿改）：
//   ① 仅 UI 线程访问（BitmapImage 线程亲和；Debug 断言兜底、Release 静默——与 ImageSourceRetirement
//      同约定）。池是进程级静态状态，所有调用点（LoadThumbnailAsync 续体 / ReleaseVisuals /
//      OnCardDragStarting）本身都在 UI 线程。
//   ② 硬上限 PoolCapacity：超出不再入池，转 ImageSourceRetirement 退役。**注意语义**：BitmapImage
//      无 IClosable——退役队列对它调不到 Dispose，只是把 GC 终结时刻推后，故这些分支（池满/清源失败/
//      已交 DragUI）刻意保持罕见与防御性，不是「已安全释放」。
//   ③ 还池前必须 UriSource = null（释放已解码纹理；防复用后仍指旧源占用内存）。
//   ④ 交给 DragUI 的实例（SetContentFromBitmapImage）经 ExcludeFromPool 标记后不得再入池/复用：
//      拖拽会话可能仍持有该位图，重置 UriSource 会破坏跟随视觉——还池时转退役队列一次性处置。
// 调用链：GalleryItemViewModel.LoadThumbnailAsync（Acquire / 未提交还池）→
//         GalleryItemViewModel.ReleaseVisuals（Return）→ WaterfallView.OnCardDragStarting（ExcludeFromPool）；
//         MainViewModel 侧栏重建收尾读 Snapshot 记池计数埋点。

using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Helpers;

namespace SimpleViewer.Views;

/// <summary>
/// 缩略图 BitmapImage 静态对象池（仅 UI 线程；空闲实例栈 + 一次性排除集）。
/// 池内实例寿命 = 进程寿命（有界 ≤ <see cref="PoolCapacity"/> 个），终结器永不运行。
/// </summary>
internal static class ThumbnailImagePool
{
    /// <summary>池空闲实例硬上限（视口卡片数量级远小于此；超出按一次性实例转退役队列）。</summary>
    private const int PoolCapacity = 64;

    /// <summary>空闲实例栈（后进先出：最近还池的先被取走，缓存局部性更好）。</summary>
    private static readonly Stack<BitmapImage> Free = new();

    /// <summary>「已交给 DragUI、不得再入池」的一次性实例集（引用相等）。</summary>
    private static readonly HashSet<BitmapImage> Excluded = new(ReferenceEqualityComparer.Instance);

    /// <summary>累计 Acquire 调用数（诊断用；只增）。</summary>
    private static long _acquiredCount;

    /// <summary>累计还池调用数（含转退役的一次性实例；诊断用；只增）。</summary>
    private static long _returnedCount;

    /// <summary>累计真实 new BitmapImage 数（池空未命中；诊断用；只增——churn 判据，稳态应为有界小值）。</summary>
    private static long _createdCount;

    /// <summary>累计「因交给 DragUI 而排除复用」的实例数（诊断用；只增）。</summary>
    private static long _excludedCount;

    /// <summary>
    /// 池统计快照（累计取用数, 累计还池数, 累计新建数, 当前空闲数, 累计排除数）——埋点用。
    /// 稳态判据：Created 应稳定在池容量/视口峰值量级，不再随滚动无限增长。
    /// </summary>
    internal static (long Acquired, long Returned, long Created, int Pooled, long Excluded) Snapshot()
        => (_acquiredCount, _returnedCount, _createdCount, Free.Count, _excludedCount);

    /// <summary>
    /// 取一个空闲 BitmapImage（池空则新建，计 <c>Created</c>）。返回实例保证 UriSource 为 null
    /// （还池路径统一置空；此处再兜底校验一次），调用方自行设置 UriSource/SetSourceAsync。
    /// </summary>
    internal static BitmapImage Acquire()
    {
        AssertUiThread();
        _acquiredCount++;
        if (Free.Count == 0)
        {
            _createdCount++;
            return new BitmapImage();
        }

        var bitmap = Free.Pop();
        try
        {
            // 兜底清源（正常路径还池时已置 null）：SetSourceAsync 分支要求复用实例无历史源。
            bitmap.UriSource = null;
        }
        catch (Exception ex)
        {
            // 复用实例清源失败：不可再信任，弃用该实例（转退役队列）并新建一个顶上。
            App.WriteDiagnosticLog("[缩略图池] 取用实例清源失败，弃用该实例", ex);
            ImageSourceRetirement.Retire(bitmap);
            _createdCount++;
            return new BitmapImage();
        }

        return bitmap;
    }

    /// <summary>
    /// 归还一个不再显示的缩略图实例。语义：
    ///   · 已排除（交给过 DragUI）→ 不入池，转退役队列（拖拽会话可能仍引用；BitmapImage 无 Dispose 可调，
    ///     退役=推迟终结，此为每拖拽一次的少量兜底，代价可接受）；
    ///   · 置空 UriSource 释放解码纹理后入池；池满或置空失败 → 转退役队列（同上，防御路径）。
    /// 幂等约束：同一实例不得重复归还（调用方 ReleaseVisuals 每次只归还从 Thumbnail 摘下的那一个）。
    /// </summary>
    internal static void Return(BitmapImage bitmap)
    {
        AssertUiThread();
        _returnedCount++;

        if (Excluded.Remove(bitmap))
        {
            _excludedCount++;
            // 一次性处置：不回池（重置 UriSource 会破坏 DragUI 已持有的跟随视觉），
            // 但也不裸交 GC（RULE:26）——转退役队列在 UI 线程延迟 Dispose（保留窗口内拖拽会话安全）。
            ImageSourceRetirement.Retire(bitmap);
            return;
        }

        try
        {
            // 释放已解码纹理：池实例终身被强引用，不显式清源则每张缩略图纹理常驻到复用为止。
            bitmap.UriSource = null;
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("[缩略图池] 还池置空 UriSource 失败，转退役队列", ex);
            ImageSourceRetirement.Retire(bitmap);
            return;
        }

        if (Free.Count >= PoolCapacity)
        {
            // 防御路径（正常视口远小于 64）：不入池，转退役队列延迟 Dispose，不裸交 GC（RULE:26）。
            ImageSourceRetirement.Retire(bitmap);
            return;
        }

        Free.Push(bitmap);
    }

    /// <summary>
    /// 标记「已交给 <c>DragUI.SetContentFromBitmapImage</c>」的一次性实例（WaterfallView 拖拽回退链）：
    /// 拖拽会话可能继续引用该 BitmapImage，重置/复用会让跟随视觉失效或引发竞态——该实例自此
    /// 退出池生命周期，待 <see cref="Return"/> 时转退役队列处置（见类注释设计约束④）。
    /// </summary>
    internal static void ExcludeFromPool(BitmapImage bitmap)
    {
        AssertUiThread();
        Excluded.Add(bitmap);
    }

    /// <summary>UI 线程专用断言（Debug 兜底、Release 静默，与 ImageSourceRetirement 同约定）。</summary>
    private static void AssertUiThread()
        => Debug.Assert(
            DispatcherQueue.GetForCurrentThread() is not null,
            "ThumbnailImagePool 必须在 UI 线程访问（XAML 位图线程亲和；对象须在创建线程析构）");
}
