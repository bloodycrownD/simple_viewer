// 职责：标签栏视图 code-behind——构造注入（MainViewModel + TagSidebarViewModel）、行点击转发、
//       行悬停浮现管理按钮（RowCommands 容器 Opacity 切换）与 x:Bind 函数转换器。
// 不变量：无交互逻辑（分流/命令全部在视图模型）；标签行/组行点击经 Tag 槽位回查 VM（ItemsRepeater 不设置
//         DataContext）；点击一律 = 筛选（2026-09-19 交互重构，不再读取修饰键状态）；
//         画刷惰性初始化仅 UI 线程访问；
//         符号字符按钮（＋⇄✎✕）不使用 FontIcon/SymbolIcon Glyph（XamlCompiler 规避清单）。
// 调用链：MainWindow（Column0 宿主注入）→ TagSidebarControl → TagSidebarViewModel.HandleChipTappedAsync
//         → MainViewModel.HandleTagChipTappedAsync（切换筛选；单图模式额外回切图库）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.ViewModels;
using Windows.ApplicationModel.DataTransfer;
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

/// <summary>
/// 左侧标签栏（spec Step 9/10）。
/// </summary>
public sealed partial class TagSidebarControl : UserControl
{
    public MainViewModel Main { get; }

    public TagSidebarViewModel ViewModel { get; }

    public TagSidebarControl(MainViewModel main, TagSidebarViewModel viewModel)
    {
        Main = main;
        ViewModel = viewModel;
        InitializeComponent();
    }

    /// <summary>
    /// 标签行点击转发（2026-09-19 交互重构：点击一律 = 筛选）：Tag 槽位回查 chip VM 交视图模型转发。
    /// 用 Click 而非 Tapped（2026-09-17 走查修复）：Click 对鼠标/触摸/键盘/自动化调用均触发，
    /// Tapped 仅真实指针手势触发，键盘与辅助功能路径会静默失效。
    /// 不再读取修饰键状态——Shift+点击移除入口已取消（移除走单图详情右栏 chip 的 ✕）。
    /// </summary>
    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagChipViewModel chip })
        {
            _ = ViewModel.HandleChipTappedAsync(chip);
        }
    }

    /// <summary>
    /// 组头点击转发（目录树态）：Tag 槽位回查组 VM，切换该组展开/折叠
    /// （TagSidebarViewModel 内折叠集合 + 全量 Rebuild 生效，展开状态会话内记忆）。
    /// Click 直达事件不冒泡：嵌套在组头内的管理按钮（＋⇄✎✕）各自触发 Command，不进入本处理器。
    /// </summary>
    private void OnGroupHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagGroupViewModel group })
        {
            ViewModel.ToggleGroupExpansion(group.Id);
        }
    }

    /// <summary>行内悬停浮现管理按钮容器的固定名（组行 ＋⇄✎✕ 与标签行 ✎✕ 共用，视觉树按名检索）。</summary>
    private const string RowCommandsName = "RowCommands";

    /// <summary>
    /// 行悬停：浮现该行右侧的管理按钮容器（RowCommands，Opacity 0→1）。
    /// 用 Opacity 而非 Visibility——按钮保持在 UIA 树中可命中，键盘/自动化路径不失效（项目 RULE）。
    /// 子按钮位于行边界内，行内移动不触发 Exited；离开行即隐藏。
    /// </summary>
    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
        => SetRowCommandsOpacity(sender, 1d);

    /// <summary>行离开：隐藏该行的管理按钮容器（Opacity→0，占位不变避免布局跳动）。</summary>
    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
        => SetRowCommandsOpacity(sender, 0d);

    /// <summary>按名检索行视觉树中的 RowCommands 容器并切其 Opacity（DataTemplate 内 x:Name 不生成字段，走视觉树回查）。</summary>
    private static void SetRowCommandsOpacity(object sender, double opacity)
    {
        if (sender is not FrameworkElement root)
        {
            return;
        }

        foreach (var element in EnumerateDescendants(root))
        {
            if (element.Name == RowCommandsName)
            {
                element.Opacity = opacity;
            }
        }
    }

    // ==================== 拖拽卡片到标签行打标（2026-09-19 交互重构） ====================

    /// <summary>标签行模板内 DropOverlay 高亮层的固定名（TagTreeRowButtonStyle 模板；视觉树按名检索）。</summary>
    private const string DropOverlayName = "DropOverlay";

    /// <summary>
    /// 标签行 DragOver：拖拽数据含本应用卡片格式（WaterfallView.CardDragFormat）且无打标操作进行中 →
    /// 接受 Copy 并高亮该行（DropOverlay 层视觉树回查，对齐 RowCommands 同模式）；
    /// 否则 AcceptedOperation=None（外部拖入/操作进行中一律拒绝，不出现“可放下”光标）。
    /// 多选拖拽（N&gt;1）时在系统拖拽浮层加计数 caption（2026-09-19 拖拽视觉）——WinAppSDK 1.6 的
    /// DragEventArgs.DragUIOverride 存在（Caption/IsCaptionVisible 等），其生命周期随拖拽会话，
    /// DragLeave/Drop 无需清理；N&lt;=1 不设（单图无需计数）。
    /// DragOver/Drop 属拖拽专用事件（非 Click/Tapped 交互约束范围）。
    /// </summary>
    private void OnTagRowDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement root
            && e.DataView.Contains(WaterfallView.CardDragFormat)
            && !Main.IsTagOperationRunning)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            SetDropOverlay(root, visible: true);

            var count = Main.DragPayloadCount;
            if (count > 1)
            {
                e.DragUIOverride.Caption = $"打标 {count} 张";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    /// <summary>标签行 DragLeave：清除该行的 Drop 高亮（拖出/取消都会触发）。</summary>
    private void OnTagRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement root)
        {
            SetDropOverlay(root, visible: false);
        }
    }

    /// <summary>
    /// 标签行 Drop：清除高亮 → Tag 槽位回查 chip VM → MainViewModel.ApplyTagToDraggedCardsAsync
    /// （chip.OwnerGroup 恒非空——2026-09-19 口径下侧栏仅配置组行，无组行可拖；
    /// 路径集消费后自清，空 payload/外部拖入在 VM 侧忽略）。
    /// </summary>
    private void OnTagRowDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement root)
        {
            return;
        }

        SetDropOverlay(root, visible: false);

        if (root.Tag is TagChipViewModel chip && e.DataView.Contains(WaterfallView.CardDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            _ = Main.ApplyTagToDraggedCardsAsync(chip.OwnerGroup, chip.Name);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 按名检索行视觉树中的 DropOverlay 层并切换落下高亮态：强调色淡底 + 描边
    /// （颜色经 <see cref="TagSidebarConverters"/> 的 IsDarkTheme 明暗双值；清除恢复透明零边框）。
    /// </summary>
    private static void SetDropOverlay(FrameworkElement root, bool visible)
    {
        foreach (var element in EnumerateDescendants(root))
        {
            if (element.Name == DropOverlayName && element is Border overlay)
            {
                if (visible)
                {
                    overlay.Background = TagSidebarConverters.DropOverlayBackground();
                    overlay.BorderBrush = TagSidebarConverters.DropOverlayBorderBrush();
                    overlay.BorderThickness = new Thickness(1.5);
                }
                else
                {
                    overlay.Background = TagSidebarConverters.TransparentBrushValue;
                    overlay.BorderThickness = default;
                }
            }
        }
    }

    /// <summary>深度枚举视觉树后代（行模板小，递归开销可忽略）。</summary>
    private static IEnumerable<FrameworkElement> EnumerateDescendants(FrameworkElement root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                yield return child;
                foreach (var descendant in EnumerateDescendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

/// <summary>
/// 标签栏模板的 x:Bind 函数转换器（静态函数绑定；画刷随主题惰性初始化，仅 UI 线程访问）。
/// 目录树行式节点（Eagle 风格，单色系）：行高亮/连接线/计数/名字色全部为 IsDarkTheme 明暗双值
/// 半透明叠加（不硬编码明暗背景，深浅主题通用）；互斥徽章保留琥珀色系、单选圆点保留组 hue 描边。
/// 筛选条（FilterChip*）与瀑布流角标（FromHsl）沿用组 hue 色彩，与侧栏树行无关。
/// </summary>
public static class TagSidebarConverters
{
    // 常量 alpha（十六进制分量）：10% = 0x1A，55% = 0x8C；
    // 树行高亮/连接线的叠加 alpha 见各函数注释。
    private const byte AlphaFaint = 0x1A;
    private const byte AlphaBorder = 0x8C;

    /// <summary>
    /// 当前有效主题是否深色（2026-09-17 走查修复：深色下 chip 文字发黑）。
    /// 根因：Application.Current.Resources 的 ThemeResource 查找不认 RootGrid.RequestedTheme
    /// 的运行时覆盖（按应用/系统主题解析），深色模式下取回浅色主题的深色文字画刷。
    /// 故代码侧颜色一律改为该标志驱动的明暗双值；由 MainWindow.ApplyTheme 在应用主题时写入，
    /// 并触发侧栏/筛选条重建使 x:Bind 函数重新求值。默认 true（默认主题 Dark）。
    /// </summary>
    public static bool IsDarkTheme { get; set; } = true;

    private static readonly SolidColorBrush WhiteBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TransparentBrush =
        new(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    /// <summary>透明画刷（拖拽高亮层清除等视图层复用）。</summary>
    internal static Brush TransparentBrushValue => TransparentBrush;

    /// <summary>
    /// 拖拽打标目标的落下高亮底色（2026-09-19 拖拽打标）：系统强调色淡叠加
    /// （深色 18% / 浅色 14%，明暗双值；强调色本身随系统主题自适应）。
    /// </summary>
    internal static Brush DropOverlayBackground()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(IsDarkTheme ? 0x2E : 0x24),
            accent.R,
            accent.G,
            accent.B));
    }

    /// <summary>拖拽打标目标的落下高亮描边：系统强调色 70% 不透明。</summary>
    internal static Brush DropOverlayBorderBrush()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, accent.R, accent.G, accent.B));
    }

    /// <summary>互斥/兼容徽章文本（兼容组 = 非互斥组：组内标签可共存叠加）。</summary>
    public static string ExclusiveBadge(bool exclusive) => exclusive ? "互斥" : "兼容";

    /// <summary>组行右对齐计数文本（纯数字；语义 = 该组去重命中张数 TotalCount）。</summary>
    public static string GroupCountText(int count) => count.ToString();

    /// <summary>标签行右对齐计数文本（纯数字）。</summary>
    public static string CountText(int count) => count.ToString();

    /// <summary>
    /// 树行底色：激活（筛选命中）= 整行圆角浅灰叠加（深色 12% 白 / 浅色 9% 黑，明暗双值）；
    /// 普通 = 透明（悬停高亮由 TagTreeRowButtonStyle 的 HoverOverlay 叠加层承担）。
    /// </summary>
    public static Brush TagRowBackground(bool isActive)
        => isActive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(
                IsDarkTheme ? (byte)0x1F : (byte)0x16,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00))
            : TransparentBrush;

    /// <summary>树形竖向连接线颜色（1px 缩进导线，次级描边感：深色 14% 白 / 浅色 10% 黑）。</summary>
    public static Brush TreeLineBrush()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            IsDarkTheme ? (byte)0x24 : (byte)0x1A,
            IsDarkTheme ? (byte)0xFF : (byte)0x00,
            IsDarkTheme ? (byte)0xFF : (byte)0x00,
            IsDarkTheme ? (byte)0xFF : (byte)0x00));

    /// <summary>标签行名字色：激活 = 主题主文字；普通 = 次要灰（明暗双值，单色系树观感）。</summary>
    public static Brush TagRowNameForeground(bool isActive)
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            (byte)(IsDarkTheme ? (isActive ? 0xF1 : 0xA0) : (isActive ? 0x1B : 0x6C)),
            (byte)(IsDarkTheme ? (isActive ? 0xF1 : 0xA0) : (isActive ? 0x1B : 0x6B)),
            (byte)(IsDarkTheme ? (isActive ? 0xF1 : 0xA0) : (isActive ? 0x1B : 0x69))));

    /// <summary>树行计数前景（纯数字右对齐）：次要灰（明暗双值）。</summary>
    public static Brush TreeCountForeground()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            (byte)(IsDarkTheme ? 0xA0 : 0x6C),
            (byte)(IsDarkTheme ? 0xA0 : 0x6B),
            (byte)(IsDarkTheme ? 0xA0 : 0x69)));

    /// <summary>互斥组单选圆点描边：激活 = 白；未激活 = 组 hue 45%（沿用胶囊时代样式）。</summary>
    public static Brush RadioDotStroke(int hue, bool isActive)
        => isActive ? WhiteBrush : FromHsl(hue, 0.45, 0.55, 0xFF);

    /// <summary>互斥组单选圆点填充：激活 = 白实心；未激活 = 透明（仅描边）。</summary>
    public static Brush RadioDotFill(bool isActive)
        => isActive ? WhiteBrush : TransparentBrush;

    /// <summary>互斥/兼容小徽章底色：互斥 = 琥珀 16% 透明（demo .group-badge.excl）；兼容 = 中性淡底（明暗双值）。</summary>
    public static Brush ExclusiveBadgeBackground(bool exclusive)
        => exclusive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x29, 0xF0, 0xB4, 0x29))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(IsDarkTheme ? 0x24 : 0x14), 0xFF, 0xFF, 0xFF));

    /// <summary>互斥/兼容小徽章字色：互斥 = 琥珀（深色下提亮）；兼容 = 中性次要色。</summary>
    public static Brush ExclusiveBadgeForeground(bool exclusive)
        => exclusive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)(IsDarkTheme ? 0xFF : 0xB0),
                (byte)(IsDarkTheme ? 0xC8 : 0x79),
                (byte)(IsDarkTheme ? 0x3D : 0x0A)))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)(IsDarkTheme ? 0xC8 : 0x6C),
                (byte)(IsDarkTheme ? 0xC8 : 0x6B),
                (byte)(IsDarkTheme ? 0xC8 : 0x69)));

    /// <summary>徽章描边厚度：互斥 = 无边框（琥珀淡底自足）；兼容 = 1px 中性描边（demo .group-badge.multi）。</summary>
    public static Thickness MultiBadgeStroke(bool exclusive)
        => exclusive ? default : new Thickness(1);

    /// <summary>
    /// 筛选 chip 文本：「组名：标签名」；空组名只显示标签名（untagged-filter-entry：
    /// 无标签 chip 组名为空串，显示「无标签」而非「：无标签」）。
    /// </summary>
    public static string FilterChipText(string groupName, string tagName)
        => string.IsNullOrEmpty(groupName) ? tagName : $"{groupName}：{tagName}";

    /// <summary>筛选条底色：强调色 12% 透明叠加（demo #filterBar accent-soft）。</summary>
    public static Brush FilterBarBackground()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// 筛选 chip 边框：所属组 hue 55% 透明（2026-09-19 口径：激活筛选标签必属配置组，
    /// 原「未分组」灰蓝特判已随筛选入口移除成死分支而删除）。空组名（无标签 chip，
    /// untagged-filter-entry）取中性灰描边——无标签不属任何组，无组 hue 可言。
    /// </summary>
    public static Brush FilterChipBorderBrush(string groupName)
        => string.IsNullOrEmpty(groupName)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(
                AlphaBorder,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00))
            : FromHsl(TagGroupViewModel.HueOfName(groupName), 0.45, 0.55, AlphaBorder);

    /// <summary>筛选 chip 底色：所属组 hue 10% 淡底；空组名（无标签 chip）取中性 10% 淡底。</summary>
    public static Brush FilterChipBackground(string groupName)
        => string.IsNullOrEmpty(groupName)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(
                AlphaFaint,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00))
            : FromHsl(TagGroupViewModel.HueOfName(groupName), 0.50, 0.50, AlphaFaint);

    /// <summary>筛选 chip 字色：所属组 hue 中亮度（深浅主题均可读）；空组名（无标签 chip）取中性次要灰。</summary>
    public static Brush FilterChipForeground(string groupName)
        => string.IsNullOrEmpty(groupName)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)(IsDarkTheme ? 0xC8 : 0x44),
                (byte)(IsDarkTheme ? 0xC8 : 0x44),
                (byte)(IsDarkTheme ? 0xC8 : 0x44)))
            : FromHsl(TagGroupViewModel.HueOfName(groupName), 0.55, 0.50, 0xFF);

    /// <summary>
    /// 侧栏标题行「∅ 无标签」按钮底色（untagged-filter-entry）：激活 = 系统强调色淡底
    /// （深色 18% / 浅色 14%，明暗双值——参照 DropOverlayBackground 手法）；未激活 = 透明
    /// （对齐 TagRowBackground 未激活分支，观感交还 GhostIconButtonStyle 样式）。
    /// </summary>
    public static Brush UntaggedButtonBackground(bool isActive)
    {
        if (!isActive)
        {
            return TransparentBrush;
        }

        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(IsDarkTheme ? 0x2E : 0x24),
            accent.R,
            accent.G,
            accent.B));
    }

    /// <summary>
    /// 侧栏标题行「∅ 无标签」按钮字色：激活 = 系统强调色实色；未激活 = 次要灰
    /// （明暗双值，对齐 GhostIconButtonStyle 的 TextFillColorSecondaryBrush 观感）。
    /// </summary>
    public static Brush UntaggedButtonForeground(bool isActive)
    {
        if (isActive)
        {
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            return new SolidColorBrush(accent);
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            (byte)(IsDarkTheme ? 0xA0 : 0x6C),
            (byte)(IsDarkTheme ? 0xA0 : 0x6B),
            (byte)(IsDarkTheme ? 0xA0 : 0x69)));
    }

    /// <summary>bool → 可见。</summary>
    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>bool 取反 → 可见（右栏标签区空态文案等）。</summary>
    public static Visibility NotBoolToVisibility(bool value)
        => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>目录选择器标签行的已选后缀文本（已含 = “✓ 已有”，未含 = 空串；U+2713 BMP 安全码点）。</summary>
    public static string AppliedSuffix(bool isApplied) => isApplied ? "✓ 已有" : string.Empty;

    /// <summary>组展开 → 标签行列表可见（目录树态：折叠时子行整体收起）。</summary>
    public static Visibility IsExpandedToVisibility(bool isExpanded)
        => isExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 组头 chevron 字符（目录树态）：展开 ▾（U+25BE）/ 折叠 ▸（U+25B8）。
    /// BMP 文本字符方案（不使用 FontIcon/SymbolIcon Glyph，规避 XamlCompiler 沉默崩溃码点；
    /// 字形缺失方块时备选 U+25B6/U+25BC）。
    /// </summary>
    public static string ChevronGlyph(bool isExpanded) => isExpanded ? "\u25BE" : "\u25B8";

    /// <summary>
    /// HSL → SolidColorBrush（hue 0-359；sat/light 0-1；alpha 半透明叠加用）。
    /// 供侧栏与瀑布流角标/筛选条共用（WaterfallConverters 经 internal 访问）。
    /// </summary>
    internal static SolidColorBrush FromHsl(int hue, double saturation, double lightness, byte alpha)
    {
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var hp = hue / 60d;
        var x = chroma * (1 - Math.Abs((hp % 2) - 1));
        var (r, g, b) = hp switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        var m = lightness - (chroma / 2);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255)));
    }
}
