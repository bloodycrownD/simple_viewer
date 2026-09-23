// 职责：瀑布流「已呈现路径集合」（cr/P1-2）——路径级去重，防同一图片出现两张卡片。
// 背景：筛选 ResetFrom（内存求值命中集，tag-filter-tree 起）与扫描渐进 AppendChunkFromScan（uiBuffer 攒批窗口内
//       「已入索引但尚未投递 UI」的项）会发生重叠；重叠项若不查重会重复追加卡片。
// 纯逻辑下沉 Core（tests 只引用 Core，可单测）；仅 UI 线程使用（与 GallerySource 相同的线程亲和性）。
// 调用链：WaterfallViewModel（ResetFrom 重建 / AppendChunkFromScan 追加前查重）。

namespace SimpleViewer.Helpers;

/// <summary>
/// 已呈现路径集合（OrdinalIgnoreCase 比较）：整体重置时重建，追加前查重。
/// </summary>
public sealed class PresentedPathSet
{
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 整体重置（瀑布流 ResetFrom 后重建集合；传入序列只枚举一次）。
    /// </summary>
    public void ResetWith(IEnumerable<string> paths)
        => _paths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试登记路径：未呈现过返回 true（应追加卡片）；已呈现返回 false（跳过，防重复卡片）。
    /// </summary>
    public bool TryAdd(string path) => _paths.Add(path);
}
