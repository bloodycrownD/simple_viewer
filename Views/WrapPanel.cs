// 职责：水平流式换行面板（可复用布局控件，cr/P2-8 从 TagSidebarControl.xaml.cs 拆出）——
//       WaterfallView 卡片角标条与 SingleImageView 右栏标签 chips 使用。
// 不变量：Measure/Arrange 两遍一致的贪心换行（同行放不下即换行；行高取该行子元素最大高）；
//         无限宽约束（理论不出现）时按内容自然宽度估算。
// 调用链：XAML ItemsControl.ItemsPanel / 直接声明（Views 命名空间，SDK 隐式通配编译）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 水平流式换行面板（WaterfallView 卡片角标条使用；侧栏已改为目录树行式节点，不再使用本面板）。
/// </summary>
public sealed class WrapPanel : Panel
{
    /// <summary>同行子元素间距依赖属性。</summary>
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d));

    /// <summary>行间距依赖属性。</summary>
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d));

    /// <summary>同行子元素间距（像素）。</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>行与行之间的间距（像素）。</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var limitWidth = double.IsInfinity(availableSize.Width) ? 0d : availableSize.Width;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && limitWidth > 0 && x + size.Width > limitWidth)
            {
                x = 0;
                y += rowHeight + LineSpacing;
                rowHeight = 0;
            }

            x += size.Width + ItemSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(limitWidth > 0 ? limitWidth : Math.Max(0, x - ItemSpacing), y + rowHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + LineSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + ItemSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(finalSize.Width, y + rowHeight);
    }
}
