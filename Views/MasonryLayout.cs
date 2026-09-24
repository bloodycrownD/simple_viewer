// 职责：瀑布流自定义布局（D3）——列高最短优先分配卡片（VirtualizingLayout 虚拟化版本）。
//       布局核心已下沉 Core 为 Helpers/MasonryPlanner（cr/P2-10：AppendItem 槽位分配 /
//       ComputeColumns / ComputeCardWidth / ExtentHeight / CanReuse 增量续算判定），
//       本类只保留 WinUI 侧职责：LayoutState 生命周期、视口求宽、宽高比提供者解析、
//       视口相交测试与元素 realize/回收调度。
// 不变量：卡片高度不依赖元素实际测量（由索引中预计算的宽高比推算），列分配以“估算行”纯数据完成——
//         无需 realize 元素即可确定全部卡片位置；每轮布局只 realize 视口 ± 缓冲区内的元素，
//         视口外元素由 ItemsRepeater 自动回收；位置数组缓存在 MasonryPlanner（LayoutState）：
//         尾部追加增量续算（D15：渐进呈现不重排已布局项）、列数变化/Count 回退时全量重算；
//         数据源非 Add 通知（如 Reset）由宿主（WaterfallView 订阅 CollectionChanged）置
//         ForceRecompute 全量重算（WinAppSDK 1.6 的 OnItemsChanged 虚方法不可重写，
//         重算标记经宿主侧触发，效果等价）；列数不变的窄幅宽度变化走滞后阈值（resize 去抖配合）。
// 两段提交说明（spec 风险表既定策略）：第一段为 NonVirtualizingLayout 正确版（已提交），
//         本版本为其虚拟化升级，布局常量与列分配语义不变。
// 调用链：WaterfallView（XAML 声明 ItemsRepeater.Layout + resize 去抖 ForceRecompute）→ MeasureOverride/ArrangeOverride
//         → Helpers/MasonryPlanner（Core，单测覆盖 T-MS1~4）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleViewer.Helpers;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 瀑布流布局（D3，VirtualizingLayout 版）。布局核心委托 <see cref="MasonryPlanner"/>（Core）。
/// 布局常量：目标卡宽 <see cref="TargetCardWidth"/>、间距 <see cref="CardGap"/>、
/// 列数 = max(2, ⌊视口宽/目标卡宽⌋)、卡片高度 = 卡宽/宽高比 + <see cref="TextAreaHeight"/>
/// （文字区为常量化估算值，实施可调，须与 WaterfallView 卡片模板的文字区高度保持一致）。
/// </summary>
public class MasonryLayout : VirtualizingLayout
{
    /// <summary>目标卡宽（D3 固化 240px；单一事实源在 <see cref="MasonryPlanner"/>，此处转发保持既有公共引用不破）。</summary>
    public const double TargetCardWidth = MasonryPlanner.TargetCardWidth;

    /// <summary>卡片间距（D3 固化 14px；垂直与水平同值；转发自 <see cref="MasonryPlanner"/>）。</summary>
    public const double CardGap = MasonryPlanner.CardGap;

    /// <summary>卡片文字区估算高度（D3：常量化估算值，实施可调；须与 WaterfallView 卡片模板文字区高度一致；转发自 <see cref="MasonryPlanner"/>）。</summary>
    public const double TextAreaHeight = MasonryPlanner.TextAreaHeight;

    /// <summary>视口上下缓冲高度（px）：超出视口该范围内的卡片仍保持 realize，降低快速滚动白屏。</summary>
    private const double RealizationBuffer = 600;

    /// <summary>列数不变时的宽度滞后阈值（px）：拖拽 resize 的中间态不触发 O(n) 全量重算与视觉跳动，
    /// 由宿主的 resize 去抖（WaterfallView）结束后经 ForceRecompute 强制按最终宽度重排。</summary>
    private const double WidthSnapTolerance = 40;

    /// <summary>视口宽度未知（无限约束）时的兜底假设宽度。</summary>
    private const double FallbackViewportWidth = 1200;

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

    /// <summary>
    /// 最近一次布局的实际卡宽（视口均分，可大于目标 240）。
    /// 宿主据此按"实际卡宽 × DPI"更新缩略图分桶（2026-09-17 走查修复模糊：
    /// 宽视口少列数时实际卡宽可达 ~307 逻辑，固定 360 桶在 150% 屏会被拉伸发糊）。
    /// </summary>
    public double ActualCardWidth { get; private set; } = TargetCardWidth;

    /// <inheritdoc />
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        // XAML 布局覆写异常防弹（2026-09-25 0xc000027b stowed 闪退）：覆写抛出的任何托管异常
        // 会被 XAML stowed 直接杀进程（不经过托管 handler，日志干净+dump 实锤）——兜底落日志
        // 并按空尺寸返回，绝不外抛。
        try
        {
            return MeasureCore(context, availableSize);
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("[MasonryLayout.Measure 异常兜底]", ex);
            return new Size(0, 0);
        }
    }

    private Size MeasureCore(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = context.LayoutState as MasonryPlanner ?? new MasonryPlanner();
        context.LayoutState = state;

        var viewportWidth = ResolveViewportWidth(availableSize);
        var columns = MasonryPlanner.ComputeColumns(viewportWidth);

        // 布局复用条件：已有布局、无强制重排、列数相同、宽度在滞后阈值内、且数据只增不减（尾部追加）。
        // 其余（首建/强制重排/列数变化/宽度越阈/Count 回退）全量 Reset 重算。
        var canReuse = !ForceRecompute
            && state.CanReuse(columns, viewportWidth, context.ItemCount, WidthSnapTolerance);
        ForceRecompute = false;

        if (!canReuse)
        {
            state.Reset(columns, viewportWidth, MasonryPlanner.ComputeCardWidth(viewportWidth, columns));
        }

        ActualCardWidth = state.CardWidth;

        // 估算行实现列分配：纯数据续算新增项 [state.Count, context.ItemCount)，
        // 不需要 realize 元素（卡片高度只依赖宽高比与列宽）；既有槽位不动（D15）。
        var itemCount = context.ItemCount;
        while (state.Count < itemCount)
        {
            state.AppendItem(ResolveAspectRatio(context.GetItemAt(state.Count)));
        }

        // 只 realize 视口 ± 缓冲内的元素；视口外已 realize 的元素因本轮未 Arrange 被 ItemsRepeater 自动回收。
        var realizationRect = ExpandRect(context.RealizationRect, RealizationBuffer);
        for (var i = 0; i < itemCount; i++)
        {
            var slot = state[i];
            if (!Intersects(new Rect(slot.X, slot.Y, slot.Width, slot.Height), realizationRect))
            {
                continue;
            }

            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(slot.Width, slot.Height));
        }

        return new Size(state.LayoutWidth, state.ExtentHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        // 同 MeasureOverride：布局覆写内异常=stowed 死刑，兜底防弹。
        try
        {
            if (context.LayoutState is MasonryPlanner state)
            {
                var realizationRect = ExpandRect(context.RealizationRect, RealizationBuffer);
                for (var i = 0; i < state.Count; i++)
                {
                    var slot = state[i];
                    if (!Intersects(new Rect(slot.X, slot.Y, slot.Width, slot.Height), realizationRect))
                    {
                        continue;
                    }

                    // 与 Measure 同窗口：已 realize 的直接返回同一元素。
                    context.GetOrCreateElementAt(i).Arrange(new Rect(slot.X, slot.Y, slot.Width, slot.Height));
                }
            }

            return finalSize;
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("[MasonryLayout.Arrange 异常兜底]", ex);
            return finalSize;
        }
    }

    /// <summary>视口宽度：无限约束（理论上不出现，ScrollViewer 横向禁用）时用兜底假设值。</summary>
    private static double ResolveViewportWidth(Size availableSize)
        => double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : FallbackViewportWidth;

    /// <summary>解析项宽高比：provider 缺失或返回非法值时回退 1:1（与 GalleryItem.AspectRatio 口径一致；
    /// Planner.AppendItem 对非法值另有同口径防御，双保险）。</summary>
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
