// 职责：瀑布流薄壳视图模型（spec：逻辑尽量下沉 Core，本类只做项包装与集合通知）——
//       渐进追加/整体重置，卡片交互向 MainViewModel 转发。
// 不变量：GallerySource 仅允许 UI 线程变更（扫描块经 Progress<T> 回投 UI 线程后追加）；
//         扫描渐进追加走批量 Add 通知（每扫描块一次，不逐项通知）；
//         追加前按已呈现路径集查重（cr/P1-2）：筛选 ResetFrom 命中集与扫描攒批窗口重叠不产生重复卡片；
//         打标/重命名走就地更新（MainViewModel.ReplaceGalleryItemState → 卡片 VM UpdateFrom，
//         VM 实例不变、不发集合通知，选中集天然保持）；仅筛选切换走整体 Reset。
// 调用链：MainViewModel（扫描块回投/打标就地更新/筛选后重置）→ WaterfallViewModel → GallerySource → ItemsRepeater。

using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleViewer.Helpers;
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

    /// <summary>
    /// 已呈现路径集合（cr/P1-2）：筛选 ResetFrom（索引命中集）与扫描渐进追加的攒批窗口
    /// （已入索引尚未投递 UI 的项）会重叠——追加前按路径查重，防同一图片出现两张卡片。
    /// </summary>
    private readonly PresentedPathSet _presentedPaths = new();

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
    /// 任一筛选激活（标签 OR 或无标签，untagged-filter-entry）时块内项先经筛选谓词过滤
    /// （Step 9：筛选态与渐进追加互不干扰）；过滤后再按已呈现路径集查重（cr/P1-2）。
    /// </summary>
    public void AppendChunkFromScan(IReadOnlyList<GalleryItem> chunk)
    {
        if (chunk.Count == 0)
        {
            return;
        }

        if (_owner.HasAnyFilter)
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
            // 路径查重（cr/P1-2）：命中 = 已被筛选 ResetFrom 的索引命中集呈现过（攒批窗口重叠），
            // 跳过并维护集合；非命中登记路径后正常追加。
            if (_presentedPaths.TryAdd(item.Path))
            {
                viewModels.Add(new GalleryItemViewModel(item, this, _thumbnailService));
            }
        }

        if (viewModels.Count == 0)
        {
            ItemsChanged?.Invoke(); // 全部为重复也要通知（状态行命中数按 Items.Count 刷新）。
            return;
        }

        Items.AddRange(viewModels);
        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// 整体重置（筛选切换/重新打开图库；必须在 UI 线程调用）。会丢弃全部卡片 VM——
    /// 缩略图由 ThumbnailService 内存/磁盘缓存兜底，重新 Realize 时快速恢复；
    /// 已呈现路径集随之重建（cr/P1-2：重置后的集合内容 = 当前呈现集）。
    /// </summary>
    public void ResetFrom(IEnumerable<GalleryItem> items)
    {
        // 快照后复用：ResetWith 与路径集重建各枚举一次，输入可能是扫描中的活集合（_galleryItems）。
        var snapshot = items.ToList();
        Items.ResetWith(snapshot.Select(item => new GalleryItemViewModel(item, this, _thumbnailService)));
        _presentedPaths.ResetWith(snapshot.Select(item => item.Path));
        ItemsChanged?.Invoke();
    }

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
