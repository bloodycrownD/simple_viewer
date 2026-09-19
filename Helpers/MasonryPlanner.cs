// 职责：瀑布流布局核心规划器（cr/P2-10 从 Views/MasonryLayout.cs 下沉 Core，可单测）——
//       列参数计算（ComputeColumns/ComputeCardWidth）、列高最短优先的槽位分配（AppendItem）、
//       内容总高（ExtentHeight）与增量续算判定（CanReuse）。
// 不变量：布局常量固化（目标卡宽 240 / 间距 14 / 文字区高 48 / 列数 = max(2, ⌊视口宽/240⌋)），
//         与 Views/MasonryLayout 的公共常量、WaterfallView 卡片模板文字区行高锚点保持一致；
//         槽位分配为纯数据续算（卡片高度 = 卡宽/宽高比 + 文字区高，不依赖元素实际测量）；
//         尾部追加不触碰既有槽位（D15：渐进呈现不重排已布局项）；
//         列数变化 / 宽度越过滞后阈值 / Count 回退（数据源缩减）时由 CanReuse 判 false，
//         调用方 Reset 后全量重排（WinAppSDK 1.6 的 OnItemsChanged 不可重写，Reset/ForceRecompute
//         重算标记经宿主侧触发，效果等价）；
//         ratio 非法（0/负/无限）回退 1:1（与 GalleryItem.AspectRatio 口径一致）。
// 调用链：Views/MasonryLayout（MeasureOverride/ArrangeOverride 委托本类）；
//         tests/SimpleViewer.Tests/MasonryPlannerTests.cs（T-MS1~4）直接锁定口径。

namespace SimpleViewer.Helpers;

/// <summary>瀑布流槽位（纯数据）：卡片在内容坐标系中的位置与尺寸（对应 WinRT Rect，Core 不依赖 WinUI）。</summary>
public readonly record struct MasonrySlot(double X, double Y, double Width, double Height);

/// <summary>
/// 瀑布流布局规划器（D3 布局核心，cr/P2-10 下沉 Core）：列高最短优先分配卡片，
/// 位置数组随 <see cref="AppendItem"/> 增量增长，支撑尾部追加续算（D15）与全量重排（Reset）。
/// 实例状态应随数据源生命周期存活（宿主挂在 VirtualizingLayoutContext.LayoutState）。
/// </summary>
public sealed class MasonryPlanner
{
    /// <summary>目标卡宽（D3 固化 240px）。</summary>
    public const double TargetCardWidth = 240;

    /// <summary>卡片间距（D3 固化 14px；垂直与水平同值）。</summary>
    public const double CardGap = 14;

    /// <summary>卡片文字区估算高度（D3：常量化估算值，实施可调；须与 WaterfallView 卡片模板文字区行高一致）。</summary>
    public const double TextAreaHeight = 48;

    /// <summary>最小列数（D3：max(2, ...)，极窄视口至少两列）。</summary>
    public const int MinColumnCount = 2;

    private double[] _columnHeights = [];
    private MasonrySlot[] _slots = [];

    /// <summary>是否已建立布局（Reset 至少执行过一次）。</summary>
    public bool HasLayout { get; private set; }

    /// <summary>布局采用的总项数（槽位数组的有效前缀长度，只随 AppendItem 递增）。</summary>
    public int Count { get; private set; }

    /// <summary>布局采用的列数。</summary>
    public int ColumnCount { get; private set; }

    /// <summary>布局采用的视口宽（宽度滞后比较基准）。</summary>
    public double AppliedWidth { get; private set; }

    /// <summary>布局采用的列宽。</summary>
    public double CardWidth { get; private set; }

    /// <summary>内容横向总宽（列宽 × 列数 + 间距；extent 宽）。</summary>
    public double LayoutWidth { get; private set; }

    /// <summary>按索引取槽位（0 ≤ index &lt; <see cref="Count"/>）。</summary>
    public MasonrySlot this[int index] => _slots[index];

    /// <summary>
    /// 布局复用判定（增量续算前提）：已有布局、列数相同、宽度在滞后阈值内、且数据只增不减
    /// （Count 回退 = 数据源缩减，须全量重排）。强制重排标志由调用方（宿主 resize 去抖）另行相与。
    /// </summary>
    public bool CanReuse(int columns, double viewportWidth, int itemCount, double widthSnapTolerance)
        => HasLayout
            && ColumnCount == columns
            && Math.Abs(viewportWidth - AppliedWidth) <= widthSnapTolerance
            && Count <= itemCount;

    /// <summary>
    /// 按新列参数全量重置：槽位数组清空、列高归零、计数归零——后续 AppendItem 从头重排
    /// （列数变化 / 宽度越过阈值 / Count 回退 / 宿主强制重排时调用）。
    /// </summary>
    public void Reset(int columns, double appliedWidth, double cardWidth)
    {
        ColumnCount = columns;
        AppliedWidth = appliedWidth;
        CardWidth = cardWidth;
        LayoutWidth = columns * cardWidth + (columns - 1) * CardGap;
        _slots = [];
        _columnHeights = new double[columns];
        Count = 0;
        HasLayout = true;
    }

    /// <summary>
    /// 尾部追加一项并返回其槽位：列高最短优先分配（并列取左侧列）；
    /// 卡片高度 = ⌊卡宽/宽高比⌋ + 文字区高，列累计高度 += 卡片高 + 间距。
    /// 既有槽位不动（D15：渐进呈现不重排）。ratio 非法（0/负/无限）回退 1:1。
    /// </summary>
    public MasonrySlot AppendItem(double aspectRatio)
    {
        var ratio = aspectRatio > 0 && double.IsFinite(aspectRatio) ? aspectRatio : 1.0;
        var cardHeight = Math.Floor(CardWidth / ratio) + TextAreaHeight;

        var column = GetShortestColumn();
        var slot = new MasonrySlot(
            X: column * (CardWidth + CardGap),
            Y: _columnHeights[column],
            Width: CardWidth,
            Height: cardHeight);

        EnsureCapacity(Count + 1);
        _slots[Count] = slot;
        Count++;
        _columnHeights[column] += cardHeight + CardGap;
        return slot;
    }

    /// <summary>列数：max(2, ⌊视口宽/240⌋)。</summary>
    public static int ComputeColumns(double viewportWidth)
        => Math.Max(MinColumnCount, (int)Math.Floor(viewportWidth / TargetCardWidth));

    /// <summary>列宽：均分视口宽（去掉列间距）。</summary>
    public static double ComputeCardWidth(double viewportWidth, int columns)
        => Math.Max(1, Math.Floor((viewportWidth - (columns - 1) * CardGap) / columns));

    /// <summary>内容总高度 = 最高列高减去最后一张卡片下方多算的一个间距。</summary>
    public double ExtentHeight
    {
        get
        {
            var max = 0.0;
            foreach (var height in _columnHeights)
            {
                if (height > max)
                {
                    max = height;
                }
            }

            return Math.Max(0, max - CardGap);
        }
    }

    /// <summary>找当前最矮的列（列高最短优先分配；并列取左侧列）。</summary>
    private int GetShortestColumn()
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

    /// <summary>槽位数组按需扩容（倍增策略）。</summary>
    private void EnsureCapacity(int capacity)
    {
        if (_slots.Length >= capacity)
        {
            return;
        }

        Array.Resize(ref _slots, Math.Max(capacity, _slots.Length * 2));
    }
}
