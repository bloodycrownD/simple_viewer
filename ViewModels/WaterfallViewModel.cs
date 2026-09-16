// 职责：瀑布流薄壳视图模型（spec：逻辑尽量下沉 Core，本类只做项包装与集合通知）——
//       渐进追加/整体重置，卡片交互向 MainViewModel 转发。
// 不变量：GallerySource 仅允许 UI 线程变更（扫描块经 Progress<T> 回投 UI 线程后追加）；
//         扫描渐进追加走批量 Add 通知（每扫描块一次，不逐项通知）；
//         打标/重命名/筛选切换后走整体 Reset 刷新（卡片 VM 全部重建，缩略图由缓存兜底）。
// 调用链：MainViewModel（扫描块回投/打标与筛选后重置）→ WaterfallViewModel → GallerySource → ItemsRepeater。

using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>
/// 瀑布流数据源与交互转发（spec Step 8；薄壳）。
/// </summary>
public partial class WaterfallViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly IThumbnailService _thumbnailService;

    public WaterfallViewModel(MainViewModel owner, IThumbnailService thumbnailService)
    {
        _owner = owner;
        _thumbnailService = thumbnailService;
        Items = new GallerySource();
    }

    /// <summary>瀑布流项集合（ItemsRepeater 数据源；批量追加通知 + Reset）。</summary>
    public GallerySource Items { get; }

    /// <summary>项集合内容变化（追加/重置/替换）时通知宿主刷新空态等派生属性。</summary>
    public event Action? ItemsChanged;

    /// <summary>
    /// 追加一个扫描块（扫描渐进呈现；必须在 UI 线程调用——MainViewModel 经 Progress 回投）。
    /// 筛选激活时块内项先经筛选谓词过滤（Step 9：筛选态与渐进追加互不干扰）。
    /// </summary>
    public void AppendChunkFromScan(IReadOnlyList<GalleryItem> chunk)
    {
        if (chunk.Count == 0)
        {
            return;
        }

        if (_owner.IsTagFilterActive)
        {
            chunk = chunk.Where(_owner.MatchesTagFilter).ToArray();
        }

        if (chunk.Count == 0)
        {
            ItemsChanged?.Invoke(); // 无命中也要通知（状态行命中数刷新）。
            return;
        }

        var viewModels = new List<GalleryItemViewModel>(chunk.Count);
        foreach (var item in chunk)
        {
            viewModels.Add(new GalleryItemViewModel(item, this, _thumbnailService));
        }

        Items.AddRange(viewModels);
        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// 整体重置（筛选切换/重新打开图库；必须在 UI 线程调用）。会丢弃全部卡片 VM——
    /// 缩略图由 ThumbnailService 内存/磁盘缓存兜底，重新 Realize 时快速恢复。
    /// </summary>
    public void ResetFrom(IEnumerable<GalleryItem> items)
    {
        Items.ResetWith(items.Select(item => new GalleryItemViewModel(item, this, _thumbnailService)));
        ItemsChanged?.Invoke();
    }

    /// <summary>卡片单击转发（选中/取消选中；选中集状态在 MainViewModel）。</summary>
    internal void RaiseCardTapped(GalleryItemViewModel viewModel) => _owner.ToggleCardSelection(viewModel);

    /// <summary>卡片双击转发（以单图模式打开）。</summary>
    internal void RaiseCardDoubleTapped(GalleryItemViewModel viewModel) => _ = _owner.OpenImageAsSingle(viewModel.Item);
}

/// <summary>
/// 瀑布流项集合：轻量批量通知数据源（IReadOnlyList + INotifyCollectionChanged）。
/// 追加走单次多项 Add 通知（每扫描块一次，而非每项一次），重置走 Reset；
/// 只在 UI 线程使用（与 ObservableCollection 相同的线程亲和性约束）。
/// 退路说明：若 ItemsRepeater 对多项 Add 通知出现兼容问题（M2 走查发现），可退化为
/// AddRange 内逐项发单项 Add 通知（语义与 ObservableCollection 完全一致），不影响调用方。
/// </summary>
public sealed class GallerySource : IReadOnlyList<GalleryItemViewModel>, INotifyCollectionChanged
{
    private readonly List<GalleryItemViewModel> _items = [];

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public GalleryItemViewModel this[int index] => _items[index];

    /// <summary>批量尾部追加（单次多项 Add 通知；UI 线程）。</summary>
    public void AddRange(IReadOnlyList<GalleryItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var startingIndex = _items.Count;
        _items.AddRange(items);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            (System.Collections.IList)items,
            startingIndex));
    }

    /// <summary>整体重置（Reset 通知；UI 线程）。</summary>
    public void ResetWith(IEnumerable<GalleryItemViewModel> items)
    {
        _items.Clear();
        _items.AddRange(items);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    public IEnumerator<GalleryItemViewModel> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
        => CollectionChanged?.Invoke(this, args);
}
