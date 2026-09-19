// 职责：标签栏视图 code-behind——构造注入（MainViewModel + TagSidebarViewModel）、chip 点击转发（携带修饰键）
//       与 x:Bind 函数转换器。
// 不变量：无交互逻辑（分流/命令全部在视图模型）；chip 点击经 Tag 槽位回查 VM（ItemsRepeater 不设置 DataContext）
//         并携带 TappedRoutedEventArgs.KeyModifiers（Shift = 移除语义，Step 10）；
//         画刷惰性初始化仅 UI 线程访问；
//         符号字符按钮（＋⇄✎✕）不使用 FontIcon/SymbolIcon Glyph（XamlCompiler 规避清单）。
// 调用链：MainWindow（Column0 宿主注入）→ TagSidebarControl → TagSidebarViewModel.HandleChipTappedAsync
//         → MainViewModel.HandleTagChipTappedAsync（打标/移除/筛选分流）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.ViewModels;
using System.Windows.Input;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 水平流式换行面板（视觉对齐 demo .group-tags 的 flex-wrap；组内 chip 数量小，不做虚拟化）。
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
    /// chip 点击转发：Tag 槽位回查 chip VM，Shift 键实时状态交视图模型分流（Step 10：移除语义）。
    /// 用 Click 而非 Tapped（2026-09-17 走查修复）：Click 对鼠标/触摸/键盘/自动化调用均触发，
    /// Tapped 仅真实指针手势触发，键盘与辅助功能路径会静默失效。
    /// TappedRoutedEventArgs 不携带修饰键，按 MainWindow.IsKeyDown 同模式读取当前线程键盘状态
    /// （点击同步触发，状态可靠）。
    /// </summary>
    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagChipViewModel chip })
        {
            _ = ViewModel.HandleChipTappedAsync(chip, IsShiftKeyDown());
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

    private static bool IsShiftKeyDown()
    {
        // 只判 Down：Locked 位对 Shift 无意义，中文 IME 切中英文会置位（曾致移除语义误触发）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }
}

/// <summary>
/// 标签栏模板的 x:Bind 函数转换器（静态函数绑定；画刷随主题惰性初始化，仅 UI 线程访问）。
/// 视觉规格对齐 demo.css：chip 为胶囊（组 hue 边框 55% 透明 + 10% 淡底、hover 22%、激活实心 85% 白字）；
/// 所有 hue 色均以半透明 alpha 叠加呈现，不硬编码明暗背景（深浅主题通用）。
/// </summary>
public static class TagSidebarConverters
{
    // 常量 alpha（十六进制分量）：10% = 0x1A，16% = 0x29，55% = 0x8C，85% = 0xD9；
    // hover 加深 22% 由 chip 模板内同底色叠加层（opacity 0→1）实现，无需独立画刷。
    private const byte AlphaFaint = 0x1A;
    private const byte AlphaBorder = 0x8C;
    private const byte AlphaSolid = 0xD9;

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

    /// <summary>互斥/兼容徽章文本（兼容组 = 非互斥组：组内标签可共存叠加）。</summary>
    public static string ExclusiveBadge(bool exclusive) => exclusive ? "互斥" : "兼容";

    /// <summary>组计数文本（组内标签引用张数合计）。</summary>
    public static string GroupCountText(int count) => $"{count} 张";

    /// <summary>chip 计数文本。</summary>
    public static string CountText(int count) => count.ToString();

    /// <summary>chip 底色：激活 = 组 hue 实心 85%；未激活 = 组 hue 10%（未分组灰蓝低饱和）。</summary>
    public static Brush ChipBackground(int hue, bool isActive)
        => isActive
            ? FromHsl(hue, 0.60, 0.50, AlphaSolid)
            : FromHsl(hue, IsUngroupedHue(hue) ? 0.10 : 0.50, 0.50, AlphaFaint);

    /// <summary>chip 边框：组 hue 55% 透明（激活时隐藏边框，对齐 demo .chip.filter-on）。</summary>
    public static Brush ChipBorderBrush(int hue, bool isActive)
        => isActive
            ? TransparentBrush
            : FromHsl(hue, IsUngroupedHue(hue) ? 0.15 : 0.45, 0.55, AlphaBorder);

    /// <summary>chip 前景：激活 = 白字；未激活 = 主题主文字色（明暗双值，见 IsDarkTheme 注释）。</summary>
    public static Brush ChipForeground(bool isActive)
        => isActive
            ? WhiteBrush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)(IsDarkTheme ? 0xF1 : 0x1B),
                (byte)(IsDarkTheme ? 0xF1 : 0x1B),
                (byte)(IsDarkTheme ? 0xF1 : 0x1B)));

    /// <summary>chip 内计数前景：激活 = 85% 白；未激活 = 主题次要色（demo .tag-count 10px）。</summary>
    public static Brush ChipCountForeground(bool isActive)
        => isActive
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)(IsDarkTheme ? 0xA0 : 0x6C),
                (byte)(IsDarkTheme ? 0xA0 : 0x6B),
                (byte)(IsDarkTheme ? 0xA0 : 0x69)));

    /// <summary>互斥组单选圆点描边：激活 = 白；未激活 = 组 hue 45%（demo .radio-dot）。</summary>
    public static Brush RadioDotStroke(int hue, bool isActive)
        => isActive ? WhiteBrush : FromHsl(hue, IsUngroupedHue(hue) ? 0.15 : 0.45, 0.55, 0xFF);

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

    /// <summary>筛选 chip 文本：「组名：标签名」（demo .filter-chip）。</summary>
    public static string FilterChipText(string groupName, string tagName) => $"{groupName}：{tagName}";

    /// <summary>筛选条底色：强调色 12% 透明叠加（demo #filterBar accent-soft）。</summary>
    public static Brush FilterBarBackground()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
    }

    /// <summary>筛选 chip 边框：所属组 hue 55% 透明（未分组灰蓝）。</summary>
    public static Brush FilterChipBorderBrush(string groupName)
        => FilterHue(groupName) is int hue
            ? FromHsl(hue, 0.45, 0.55, AlphaBorder)
            : FromHsl(TagGroupViewModel.UngroupedHue, 0.15, 0.55, AlphaBorder);

    /// <summary>筛选 chip 底色：所属组 hue 10% 淡底。</summary>
    public static Brush FilterChipBackground(string groupName)
        => FilterHue(groupName) is int hue
            ? FromHsl(hue, 0.50, 0.50, AlphaFaint)
            : FromHsl(TagGroupViewModel.UngroupedHue, 0.10, 0.50, AlphaFaint);

    /// <summary>筛选 chip 字色：所属组 hue 中亮度（深浅主题均可读）。</summary>
    public static Brush FilterChipForeground(string groupName)
        => FilterHue(groupName) is int hue
            ? FromHsl(hue, 0.55, 0.50, 0xFF)
            : FromHsl(TagGroupViewModel.UngroupedHue, 0.20, 0.65, 0xFF);

    /// <summary>bool → 可见。</summary>
    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>组展开 → 标签行可见（目录树态：折叠时整行 chip 收起）。</summary>
    public static Visibility IsExpandedToVisibility(bool isExpanded)
        => isExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 组头 chevron 字符（目录树态）：展开 ▾（U+25BE）/ 折叠 ▸（U+25B8）。
    /// BMP 文本字符方案（不使用 FontIcon/SymbolIcon Glyph，规避 XamlCompiler 沉默崩溃码点；
    /// 字形缺失方块时备选 U+25B6/U+25BC）。
    /// </summary>
    public static string ChevronGlyph(bool isExpanded) => isExpanded ? "\u25BE" : "\u25B8";

    /// <summary>非未分组 → 可见（组管理按钮）。</summary>
    public static Visibility NotUngroupedToVisibility(bool isUngrouped)
        => isUngrouped ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>命令存在 → 可见（未分组标签无重命名/删除按钮）。</summary>
    public static Visibility CommandToVisibility(ICommand? command)
        => command is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>组名 → 组色相（未分组虚拟组返回 null，由调用处走灰蓝低饱和分支）。</summary>
    private static int? FilterHue(string groupName)
        => TagSidebarViewModel.UngroupedGroupName.Equals(groupName, StringComparison.Ordinal)
            ? null
            : TagGroupViewModel.HueOfName(groupName);

    private static bool IsUngroupedHue(int hue) => hue == TagGroupViewModel.UngroupedHue;

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
