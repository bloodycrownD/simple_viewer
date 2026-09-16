// 职责：瀑布流自定义布局（D3）——列高最短优先分配卡片（VirtualizingLayout 虚拟化版本）。
// 不变量：布局常量固化（目标卡宽 240 / 间距 14 / 列数 = max(2, floor(视口宽/240)) /
//         卡片高度 = 卡宽/宽高比 + 文字区高度常量）；卡片高度不依赖元素实际测量（由索引中预计算的
//         宽高比推算），列分配以“估算行”纯数据完成——无需 realize 元素即可确定全部卡片位置；
//         每轮布局只 realize 视口 ± 缓冲区内的元素，视口外元素由 ItemsRepeater 自动回收；
//         位置数组缓存在 LayoutState：尾部追加增量续算（D15：渐进呈现不重排已布局项）、
//         列数变化/Count 回退时全量重算；数据源非 Add 通知（如 Reset）由宿主（WaterfallView 订阅
//         CollectionChanged）置 ForceRecompute 全量重算（WinAppSDK 1.6 的 OnItemsChanged 虚方法
//         不可重写，重算标记经宿主侧触发，效果等价）；列数不变的窄幅宽度变化走滞后阈值（resize 去抖配合）。
// 两段提交说明（spec 风险表既定策略）：第一段为 NonVirtualizingLayout 正确版（已提交），
//         本版本为其虚拟化升级，布局常量与列分配语义不变。
// 调用链：WaterfallView（XAML 声明 ItemsRepeater.Layout + resize 去抖 ForceRecompute）→ MeasureOverride/ArrangeOverride。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 瀑布流布局（D3，VirtualizingLayout 版）。
/// 布局常量：目标卡宽 <see cref="TargetCardWidth"/>、间距 <see cref="CardGap"/>、
/// 列数 = max(2, ⌊视口宽/目标卡宽⌋)、卡片高度 = 卡宽/宽高比 + <see cref="TextAreaHeight"/>
/// （文字区为常量化估算值，实施可调，须与 WaterfallView 卡片模板的文字区高度保持一致）。
/// </summary>
public class MasonryLayout : VirtualizingLayout
{
    /// <summary>目标卡宽（D3 固化 240px）。</summary>
    public const double TargetCardWidth = 240;

    /// <summary>卡片间距（D3 固化 14px；垂直与水平同值）。</summary>
    public const double CardGap = 14;

    /// <summary>卡片文字区估算高度（D3：常量化估算值，实施可调；须与 WaterfallView 卡片模板文字区高度一致）。</summary>
    public const double TextAreaHeight = 48;

    /// <summary>视口上下缓冲高度（px）：超出视口该范围内的卡片仍保持 realize，降低快速滚动白屏。</summary>
    private const double RealizationBuffer = 600;

    /// <summary>列数不变时的宽度滞后阈值（px）：拖拽 resize 的中间态不触发 O(n) 全量重算与视觉跳动，
    /// 由宿主的 resize 去抖（WaterfallView）结束后经 ForceRecompute 强制按最终宽度重排。</summary>
    private const double WidthSnapTolerance = 40;

    /// <summary>视口宽度未知（无限约束）时的兜底假设宽度。</summary>
    private const double FallbackViewportWidth = 1200;

    /// <summary>最小列数（D3：max(2, ...)，极窄视口至少两列）。</summary>
    private const int MinColumnCount = 2;

    /// <summary>
    /// 宽高比提供者（入参为数据项，即 GalleryItemViewModel），返回宽/高。
    /// 由 WaterfallView 构造时注入；null 或返回非法值（0/负/无限）时回退 1:1。
    /// </summary>
    public Func<object, double>? AspectRatioProvider { get; set; }

    /// <summary>
    /// 强制重排标志：resize 去抖结束后由宿主（WaterfallView）置 true 并触发重新度量；
    /// 下一轮度量按当前视口宽全量重算（忽略宽度滞后阈值）后自动复位。
    /// </summary>
    public bool ForceRecompute { get; set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = context.LayoutState as MasonryLayoutState ?? new MasonryLayoutState();
        context.LayoutState = state;

        var viewportWidth = ResolveViewportWidth(availableSize);
        var columns = ComputeColumns(viewportWidth);

        // 布局复用条件：已有布局、无强制重排、列数相同、宽度在滞后阈值内、且数据只增不减（尾部追加）。
        var canReuse = state.HasLayout
            && !ForceRecompute
            && state.ColumnCount == columns
            && Math.Abs(viewportWidth - state.AppliedWidth) <= WidthSnapTolerance
            && state.Count <= context.ItemCount;
        ForceRecompute = false;

        if (!canReuse)
        {
            state.Reset(columns, viewportWidth, ComputeCardWidth(viewportWidth, columns));
        }

        // 估算行实现列分配：纯数据续算新增项 [state.Count, context.ItemCount)，
        // 不需要 realize 元素（卡片高度只依赖宽高比与列宽）。
        var itemCount = context.ItemCount;
        for (var i = state.Count; i < itemCount; i++)
        {
            var ratio = ResolveAspectRatio(context.GetItemAt(i));
            var cardHeight = Math.Floor(state.CardWidth / ratio) + TextAreaHeight;
            var column = state.GetShortestColumn();
            var y = state.ColumnHeight(column);
            state.SetPosition(i, new Rect(column * (state.CardWidth + CardGap), y, state.CardWidth, cardHeight));
            state.AddColumnHeight(column, cardHeight + CardGap);
        }

        state.Count = itemCount;

        // 只 realize 视口 ± 缓冲内的元素；视口外已 realize 的元素因本轮未 Arrange 被 ItemsRepeater 自动回收。
        var realizationRect = ExpandRect(context.RealizationRect, RealizationBuffer);
        for (var i = 0; i < itemCount; i++)
        {
            if (!Intersects(state.Positions[i], realizationRect))
            {
                continue;
            }

            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(state.Positions[i].Width, state.Positions[i].Height));
        }

        return new Size(state.LayoutWidth, state.GetExtentHeight());
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is MasonryLayoutState state)
        {
            var realizationRect = ExpandRect(context.RealizationRect, RealizationBuffer);
            for (var i = 0; i < state.Count; i++)
            {
                if (!Intersects(state.Positions[i], realizationRect))
                {
                    continue;
                }

                // 与 Measure 同窗口：已 realize 的直接返回同一元素。
                context.GetOrCreateElementAt(i).Arrange(state.Positions[i]);
            }
        }

        return finalSize;
    }

    /// <summary>视口宽度：无限约束（理论上不出现，ScrollViewer 横向禁用）时用兜底假设值。</summary>
    private static double ResolveViewportWidth(Size availableSize)
        => double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : FallbackViewportWidth;

    /// <summary>列数：max(2, floor(视口宽/240))。</summary>
    private static int ComputeColumns(double viewportWidth)
        => Math.Max(MinColumnCount, (int)Math.Floor(viewportWidth / TargetCardWidth));

    /// <summary>列宽：均分视口宽（去掉列间距）。</summary>
    private static double ComputeCardWidth(double viewportWidth, int columns)
        => Math.Max(1, Math.Floor((viewportWidth - (columns - 1) * CardGap) / columns));

    /// <summary>解析项宽高比：provider 缺失或返回非法值时回退 1:1（与 GalleryItem.AspectRatio 口径一致）。</summary>
    private double ResolveAspectRatio(object? item)
    {
        var ratio = AspectRatioProvider?.Invoke(item!) ?? 1.0;
        return ratio > 0 && double.IsFinite(ratio) ? ratio : 1.0;
    }

    /// <summary>上下扩展矩形（视口缓冲）。</summary>
    private static Rect ExpandRect(Rect rect, double buffer)
        => new(rect.X, rect.Y - buffer, Math.Max(rect.Width, 1), rect.Height + buffer * 2);

    /// <summary>两矩形是否相交（WinRT Rect 无 IntersectsWith 成员，自行实现）。</summary>
    private static bool Intersects(Rect a, Rect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}

/// <summary>
/// 瀑布流布局状态（挂在 VirtualizingLayoutContext.LayoutState，随数据源生命周期存活）：
/// 缓存列参数与全部卡片位置数组，支撑尾部追加增量续算与视口相交测试。
/// </summary>
internal sealed class MasonryLayoutState
{
    private double[] _columnHeights = [];
    private Rect[] _positions = [];

    /// <summary>是否已建立布局（Reset 至少执行过一次）。</summary>
    public bool HasLayout { get; private set; }

    /// <summary>布局采用的总项数（位置数组的有效前缀长度）。</summary>
    public int Count { get; set; }

    /// <summary>布局采用的列数。</summary>
    public int ColumnCount { get; private set; }

    /// <summary>布局采用的视口宽（宽度滞后比较基准）。</summary>
    public double AppliedWidth { get; private set; }

    /// <summary>布局采用的列宽。</summary>
    public double CardWidth { get; private set; }

    /// <summary>内容横向总宽（列宽 × 列数 + 间距；extent 宽）。</summary>
    public double LayoutWidth { get; private set; }

    /// <summary>全部卡片位置（下标即项 index）。</summary>
    public Rect[] Positions => _positions;

    /// <summary>按新列参数全量重置（位置数组清空，计数归零）。</summary>
    public void Reset(int columns, double appliedWidth, double cardWidth)
    {
        ColumnCount = columns;
        AppliedWidth = appliedWidth;
        CardWidth = cardWidth;
        LayoutWidth = columns * cardWidth + (columns - 1) * MasonryLayout.CardGap;
        _positions = [];
        _columnHeights = new double[columns];
        Count = 0;
        HasLayout = true;
    }

    /// <summary>找当前最矮的列（列高最短优先分配；并列取左侧列）。</summary>
    public int GetShortestColumn()
    {
        var shortest = 0;
        for (var k = 1; k < _columnHeights.Length; k++)
        {
            if (_columnHeights[k] < _columnHeights[shortest])
            {
                shortest = k;
            }
        }

        return shortest;
    }

    /// <summary>指定列当前累计高度。</summary>
    public double ColumnHeight(int column) => _columnHeights[column];

    /// <summary>指定列累计高度增加（卡片高 + 间距）。</summary>
    public void AddColumnHeight(int column, double delta) => _columnHeights[column] += delta;

    /// <summary>写入项位置（按需扩容位置数组）。</summary>
    public void SetPosition(int index, Rect rect)
    {
        EnsureCapacity(index + 1);
        _positions[index] = rect;
    }

    /// <summary>内容总高度 = 最高列高减去最后一张卡片下方多算的一个间距。</summary>
    public double GetExtentHeight()
    {
        var max = 0.0;
        foreach (var height in _columnHeights)
        {
            if (height > max)
            {
                max = height;
            }
        }

        return Math.Max(0, max - MasonryLayout.CardGap);
    }

    private void EnsureCapacity(int capacity)
    {
        if (_positions.Length >= capacity)
        {
            return;
        }

        Array.Resize(ref _positions, Math.Max(capacity, _positions.Length * 2));
    }
}
