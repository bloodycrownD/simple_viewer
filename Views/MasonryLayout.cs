// 职责：瀑布流自定义布局（D3）——列高最短优先分配卡片。
// 不变量：布局常量固化（目标卡宽 240 / 间距 14 / 列数 = max(2, floor(视口宽/240)) /
//         卡片高度 = 卡宽/宽高比 + 文字区高度常量）；宽高比由宿主注入 provider（按元素 DataContext），
//         缺失/非法回退 1:1；卡片位置仅由 index 与已分配列高决定，尾部追加不影响已布局项
//         （D15：扫描渐进呈现不引起已呈现项重排/位置跳动）。
// 两段提交（spec 风险表既定策略）：本文件第一段为 NonVirtualizingLayout 正确版（全量布局，
//         先保证列分配语义正确）；第二段升级为 VirtualizingLayout（估算行实现列分配、视口外回收），
//         布局常量与列分配语义保持不变。
// 调用链：WaterfallView（XAML 声明 ItemsRepeater.Layout）→ MeasureOverride/ArrangeOverride。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 瀑布流布局（D3）。
/// 布局常量：目标卡宽 <see cref="TargetCardWidth"/>、间距 <see cref="CardGap"/>、
/// 列数 = max(2, ⌊视口宽/目标卡宽⌋)、卡片高度 = 卡宽/宽高比 + <see cref="TextAreaHeight"/>
/// （文字区为常量化估算值，实施可调，须与 WaterfallView 卡片模板的文字区高度保持一致）。
/// </summary>
public class MasonryLayout : NonVirtualizingLayout
{
    /// <summary>目标卡宽（D3 固化 240px）。</summary>
    public const double TargetCardWidth = 240;

    /// <summary>卡片间距（D3 固化 14px；垂直与水平同值）。</summary>
    public const double CardGap = 14;

    /// <summary>卡片文字区估算高度（D3：常量化估算值，实施可调；须与 WaterfallView 卡片模板文字区高度一致）。</summary>
    public const double TextAreaHeight = 48;

    /// <summary>视口宽度未知（无限约束）时的兜底假设宽度。</summary>
    private const double FallbackViewportWidth = 1200;

    /// <summary>最小列数（D3：max(2, ...)，极窄视口至少两列）。</summary>
    private const int MinColumnCount = 2;

    private Rect[] _arrangeRects = [];

    /// <summary>
    /// 宽高比提供者（入参为卡片元素 DataContext，即 GalleryItemViewModel），返回宽/高。
    /// 由 WaterfallView 构造时注入；null 或返回非法值（0/负/无限）时回退 1:1。
    /// </summary>
    public Func<object, double>? AspectRatioProvider { get; set; }

    /// <summary>
    /// 强制重排标志：resize 去抖结束后由宿主（WaterfallView）置 true 并触发重新度量。
    /// 第一段（NonVirtualizingLayout）每轮全量重算，本标志为第二段虚拟化版本（列宽滞后复用布局）预留消费点。
    /// </summary>
    public bool ForceRecompute { get; set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var viewportWidth = ResolveViewportWidth(availableSize);
        (var columns, var cardWidth) = ComputeColumns(viewportWidth);

        var columnHeights = new double[columns];
        var rects = new Rect[children.Count];

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var ratio = ResolveAspectRatio((child as FrameworkElement)?.DataContext);
            var cardHeight = Math.Floor(cardWidth / ratio) + TextAreaHeight;

            var column = GetShortestColumn(columnHeights);
            var y = columnHeights[column];
            rects[i] = new Rect(column * (cardWidth + CardGap), y, cardWidth, cardHeight);
            columnHeights[column] = y + cardHeight + CardGap;

            child.Measure(new Size(cardWidth, cardHeight));
        }

        _arrangeRects = rects;
        return new Size(viewportWidth, GetExtentHeight(columnHeights));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        for (var i = 0; i < children.Count && i < _arrangeRects.Length; i++)
        {
            children[i].Arrange(_arrangeRects[i]);
        }

        return finalSize;
    }

    /// <summary>视口宽度：无限约束（理论上不出现，ScrollViewer 横向禁用）时用兜底假设值。</summary>
    private static double ResolveViewportWidth(Size availableSize)
        => double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : FallbackViewportWidth;

    /// <summary>列数与列宽：列数 = max(2, floor(视口宽/240))，列宽均分剩余宽度（去掉间距）。</summary>
    private static (int Columns, double CardWidth) ComputeColumns(double viewportWidth)
    {
        var columns = Math.Max(MinColumnCount, (int)Math.Floor(viewportWidth / TargetCardWidth));
        var cardWidth = Math.Floor((viewportWidth - (columns - 1) * CardGap) / columns);
        return (columns, Math.Max(1, cardWidth));
    }

    /// <summary>解析项宽高比：provider 缺失或返回非法值时回退 1:1（与 GalleryItem.AspectRatio 口径一致）。</summary>
    private double ResolveAspectRatio(object? item)
    {
        var ratio = AspectRatioProvider?.Invoke(item!) ?? 1.0;
        return ratio > 0 && double.IsFinite(ratio) ? ratio : 1.0;
    }

    /// <summary>找当前最矮的列（列高最短优先分配；并列取左侧列）。</summary>
    private static int GetShortestColumn(double[] columnHeights)
    {
        var shortest = 0;
        for (var k = 1; k < columnHeights.Length; k++)
        {
            if (columnHeights[k] < columnHeights[shortest])
            {
                shortest = k;
            }
        }

        return shortest;
    }

    /// <summary>内容总高度 = 最高列高减去最后一张卡片下方多算的一个间距。</summary>
    private static double GetExtentHeight(double[] columnHeights)
    {
        var max = 0.0;
        foreach (var height in columnHeights)
        {
            if (height > max)
            {
                max = height;
            }
        }

        return Math.Max(0, max - CardGap);
    }
}
