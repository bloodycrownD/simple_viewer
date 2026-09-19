// 职责：标签栏模板的 x:Bind 函数转换器（可复用静态函数绑定，cr/P2-8 从 TagSidebarControl.xaml.cs 拆出）——
//       画刷随主题惰性初始化，仅 UI 线程访问；供 TagSidebarControl / TagCatalogDialog /
//       MainWindow 筛选条 / SingleImageView 空态等多处 XAML 引用。
// 不变量：代码侧颜色一律走 IsDarkTheme 明暗双值（Application.Current.Resources 的 ThemeResource
//         查找不认 RootGrid.RequestedTheme 运行时覆盖，深色模式会取回浅色主题画刷——RULE 主题约束）；
//         IsDarkTheme 由 MainWindow.ApplyTheme 写入并触发侧栏/筛选条重建使 x:Bind 函数重新求值。
// 调用链：各 XAML 的 {x:Bind views:TagSidebarConverters.*} 函数绑定 + WaterfallView.xaml.cs
//         经 internal 访问 FromHsl + TagSidebarControl.xaml.cs 经 internal 访问拖拽高亮画刷。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

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
