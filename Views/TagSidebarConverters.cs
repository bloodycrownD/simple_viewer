// 职责：标签栏模板的 x:Bind 函数转换器（可复用静态函数绑定，cr/P2-8 从 TagSidebarControl.xaml.cs 拆出）——
//       画刷随主题惰性初始化，仅 UI 线程访问；供 TagSidebarControl / TagCatalogDialog /
//       MainWindow 筛选条 / TagFilterPanelControl（tag-filter-tree Step 5 筛选面板）/
//       SingleImageView 空态等多处 XAML 引用。
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
/// 筛选条（FilterChip*，tag-filter-tree 表达式段形态）：条件 = 强调色胶囊（否定 = 红），
/// 且/或/括号段 = 次要灰轻量文本；瀑布流角标（FromHsl）沿用组 hue 色彩，与侧栏树行无关。
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
    /// 筛选 chip 胶囊可见性（tag-filter-tree）：条件段/无标签 chip = 胶囊 + ✕；
    /// 且/或/括号段 = 轻量文本（无胶囊）。
    /// </summary>
    public static Visibility FilterChipCapsuleVisibility(FilterChipKind kind)
        => kind is FilterChipKind.Condition or FilterChipKind.Untagged
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>筛选 chip 轻量文本可见性（且/或/括号段；与胶囊互斥）。</summary>
    public static Visibility FilterChipSegmentVisibility(FilterChipKind kind)
        => kind is FilterChipKind.Op or FilterChipKind.Paren
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// 筛选 chip 字色（demo fx-cond/fx-op 同构，IsDarkTheme 双值）：
    /// 否定条件 = 红（深 #FF7B72 / 浅 #D64545）；普通条件 = 系统强调色；
    /// 无标签 chip = 中性次要灰；且/或/括号段 = 次要灰。
    /// </summary>
    public static Brush FilterChipForeground(FilterChipKind kind, bool negated)
    {
        if (kind == FilterChipKind.Condition && negated)
        {
            return DangerBrush();
        }

        if (kind == FilterChipKind.Condition)
        {
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            return new SolidColorBrush(accent);
        }

        return SecondaryTextBrush();
    }

    /// <summary>
    /// 筛选 chip 描边：否定条件 = 红 55%；普通条件 = 系统强调色 55%；
    /// 无标签 chip = 中性灰描边（且/或/括号段无胶囊不消费）。
    /// </summary>
    public static Brush FilterChipBorderBrush(FilterChipKind kind, bool negated)
    {
        if (kind == FilterChipKind.Untagged)
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                AlphaBorder,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00,
                IsDarkTheme ? (byte)0xFF : (byte)0x00));
        }

        if (negated)
        {
            var danger = DangerColor();
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                AlphaBorder, danger.R, danger.G, danger.B));
        }

        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            AlphaBorder, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// 筛选 chip 底色：否定条件 = 红 12%/10% 淡底（demo danger-soft）；其余胶囊 = 中性 10% 淡底
    /// （半透明不盖筛选条强调底；且/或/括号段无胶囊不消费）。
    /// </summary>
    public static Brush FilterChipBackground(FilterChipKind kind, bool negated)
    {
        if (kind == FilterChipKind.Condition && negated)
        {
            var danger = DangerColor();
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                IsDarkTheme ? (byte)0x1F : (byte)0x1A, danger.R, danger.G, danger.B));
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            AlphaFaint,
            IsDarkTheme ? (byte)0xFF : (byte)0x00,
            IsDarkTheme ? (byte)0xFF : (byte)0x00,
            IsDarkTheme ? (byte)0xFF : (byte)0x00));
    }

    /// <summary>否定条件红（demo --danger：深色 #FF7B72 / 浅色 #D64545，双值）。</summary>
    private static Windows.UI.Color DangerColor()
        => IsDarkTheme
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x7B, 0x72)
            : Windows.UI.Color.FromArgb(0xFF, 0xD6, 0x45, 0x45);

    private static Brush DangerBrush() => new SolidColorBrush(DangerColor());

    /// <summary>
    /// 错误提示文字色（batch-tag-management Step 3：代码构建的收纳对话框内联错误文本；
    /// 深色 #FF7B72 / 浅色 #D64545，IsDarkTheme 双值——代码取色不走 ThemeResource 运行时查找）。
    /// </summary>
    public static Brush DangerTextForeground() => DangerBrush();

    /// <summary>次要灰文本（且/或/括号段与无标签 chip；对齐 TreeCountForeground 色值）。</summary>
    private static Brush SecondaryTextBrush()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            (byte)(IsDarkTheme ? 0xA0 : 0x6C),
            (byte)(IsDarkTheme ? 0xA0 : 0x6B),
            (byte)(IsDarkTheme ? 0xA0 : 0x69)));

    /// <summary>筛选条底色：强调色 12% 透明叠加（demo #filterBar accent-soft）。</summary>
    public static Brush FilterBarBackground()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
    }

    // ==================== 筛选面板配色（tag-filter-tree Step 5：demo filter.css 面板系列同构，IsDarkTheme 双值） ====================
    // 色值基准 demo filter.css：深色 panel #2C2C2C / panel-2 #333333 / bg-2 #272727 / border #3D3D3D /
    // text #F1F1F1 / text-2 #A8A8A8；浅色 panel #FFFFFF / panel-2 #F7F7F5 / bg-2 #FBFBFA /
    // border #E2E1DF / text #1B1B1B / text-2 #5F5E5C。强调/红沿用系统强调色与 DangerColor。

    /// <summary>筛选面板底色（demo --panel：面板本体与条件行 / 嵌套偶数层组卡片共用）。</summary>
    public static Brush FilterPanelBackground()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            IsDarkTheme ? (byte)0x2C : (byte)0xFF,
            IsDarkTheme ? (byte)0x2C : (byte)0xFF,
            IsDarkTheme ? (byte)0x2C : (byte)0xFF));

    /// <summary>面板次级底色（demo --panel-2：表达式预览区底）。</summary>
    public static Brush FilterPanelSubtleBackground()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            IsDarkTheme ? (byte)0x33 : (byte)0xF7,
            IsDarkTheme ? (byte)0x33 : (byte)0xF7,
            IsDarkTheme ? (byte)0x33 : (byte)0xF5));

    /// <summary>
    /// 面板组卡片底色（demo .f-group-box 嵌套交替）：奇数层（根 / 第 3 层）= bg-2 感、
    /// 偶数层（第 2 层）= panel 感——层级由底色交替增强，与左侧 accent 竖线共同分层。
    /// </summary>
    public static Brush FilterPanelGroupBackground(int depth)
    {
        var odd = depth % 2 == 1;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            (byte)(IsDarkTheme ? (odd ? 0x27 : 0x2C) : (odd ? 0xFB : 0xFF)),
            (byte)(IsDarkTheme ? (odd ? 0x27 : 0x2C) : (odd ? 0xFB : 0xFF)),
            (byte)(IsDarkTheme ? (odd ? 0x27 : 0x2C) : (odd ? 0xFA : 0xFF))));
    }

    /// <summary>面板组卡片描边（demo --border）。</summary>
    public static Brush FilterPanelGroupBorderBrush()
        => new SolidColorBrush(Windows.UI.Color.FromArgb(
            0xFF,
            IsDarkTheme ? (byte)0x3D : (byte)0xE2,
            IsDarkTheme ? (byte)0x3D : (byte)0xE1,
            IsDarkTheme ? (byte)0x3D : (byte)0xDF));

    /// <summary>组卡片左侧 accent 竖线与标题强调（demo border-left 3px accent）。</summary>
    public static Brush FilterPanelAccentBrush()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(accent);
    }

    /// <summary>面板主文字色（demo --text）。</summary>
    public static Brush FilterPanelTextForeground()
        => TagRowNameForeground(isActive: true);

    /// <summary>面板次要文字色（demo --text-2：组头前后缀 / 空态提示 / 计数）。</summary>
    public static Brush FilterPanelSecondaryForeground()
        => TagRowNameForeground(isActive: false);

    /// <summary>面板强调淡底（demo --accent-soft：选中的全部/任一项 / 勾选行底）。</summary>
    public static Brush FilterPanelAccentSoftBackground()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            IsDarkTheme ? (byte)0x26 : (byte)0x1F, accent.R, accent.G, accent.B));
    }

    /// <summary>条件值 chip 底色（demo .val-chip）：普通 = accent 淡底；否定行（NotIn）= 红淡底。</summary>
    public static Brush FilterPanelValueChipBackground(bool negated)
    {
        if (negated)
        {
            var danger = DangerColor();
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                IsDarkTheme ? (byte)0x1F : (byte)0x1A, danger.R, danger.G, danger.B));
        }

        return FilterPanelAccentSoftBackground();
    }

    /// <summary>条件值 chip 字色：普通 = accent；否定行 = 红。</summary>
    public static Brush FilterPanelValueChipForeground(bool negated)
        => negated ? DangerBrush() : FilterPanelAccentBrush();

    /// <summary>面板中性淡底（demo .f-select / .val-add 的 panel-2 观感：未选中选项 / ＋ 标签按钮底）。</summary>
    public static Brush FilterPanelChoiceRowBackground(bool isSelected)
        => isSelected ? FilterPanelAccentSoftBackground() : TransparentBrush;

    /// <summary>值选择行字色：勾选 = accent；未勾选 = 面板主文字。</summary>
    public static Brush FilterPanelChoiceRowForeground(bool isSelected)
        => isSelected ? FilterPanelAccentBrush() : FilterPanelTextForeground();

    /// <summary>工具栏筛选按钮徽章底色（demo .cond-badge：accent 实底白字）。</summary>
    public static Brush FilterToggleBadgeBackground() => FilterPanelAccentBrush();

    /// <summary>工具栏筛选按钮徽章字色：白（accent 实底上恒白，双主题一致）。</summary>
    public static Brush FilterToggleBadgeForeground() => WhiteBrush;

    /// <summary>工具栏筛选按钮徽章可见性：有条件（&gt;0）才显示数字。</summary>
    public static Visibility FilterBadgeVisibility(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 工具栏筛选按钮底色（demo .filter-toggle.active = accent-soft）：有条件 = 强调淡底；无 = 透明
    /// （hover 反馈交还 GhostButtonStyle 模板叠加层）。
    /// </summary>
    public static Brush FilterToggleBackground(int count)
        => count > 0 ? FilterPanelAccentSoftBackground() : TransparentBrush;

    /// <summary>工具栏筛选按钮字色：有条件 = accent；无 = 面板主文字。</summary>
    public static Brush FilterToggleForeground(int count)
        => count > 0 ? FilterPanelAccentBrush() : FilterPanelTextForeground();

    /// <summary>工具栏筛选按钮描边：有条件 = accent；无 = 透明（GhostButtonStyle 本无边框常态）。</summary>
    public static Brush FilterToggleBorderBrush(int count)
        => count > 0 ? FilterPanelAccentBrush() : TransparentBrush;

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

    /// <summary>
    /// 目录标签行已选后缀（batch-tag-management Step 5 三态化 D7）：单图口径「✓ 已有」逐字保留
    /// （isBatch=false 只有 AppliedToAll/None 两态，行为与 2026-09-19 原版零变化）；
    /// 批量口径 AppliedToAll =「✓ 全部已有」、AppliedToSome =「部分已有 N/M」
    /// （N = 选中集内已含数、M = 选中总数）、None = 空串。原 bool 版随 IsApplied 二值口径废弃删除
    /// （唯一消费方 TagCatalogDialog.xaml 已迁移；U+2713 BMP 安全码点）。
    /// </summary>
    public static string CatalogAppliedSuffix(
        bool isBatch, TagCatalogApplyState state, int appliedCount, int selectionCount)
        => state switch
        {
            TagCatalogApplyState.AppliedToAll => isBatch ? "✓ 全部已有" : "✓ 已有",
            TagCatalogApplyState.AppliedToSome when isBatch => $"部分已有 {appliedCount}/{selectionCount}",
            _ => string.Empty,
        };

    /// <summary>目录标签行三态圆点填充（D7）：全部已有 = 白实心（已选态）；部分/无 = 透明（可点态空心）。
    /// 三态版委托 bool 版保证色值零漂移；独立函数名（非重载）规避 x:Bind 重载决议风险，
    /// bool 版仍被侧栏（TagSidebarControl）复用故保留。</summary>
    public static Brush CatalogDotFill(TagCatalogApplyState state)
        => RadioDotFill(state == TagCatalogApplyState.AppliedToAll);

    /// <summary>目录标签行三态圆点描边（D7）：全部已有 = 白；部分/无 = 组 hue 45%（可点态）。</summary>
    public static Brush CatalogDotStroke(int hue, TagCatalogApplyState state)
        => RadioDotStroke(hue, state == TagCatalogApplyState.AppliedToAll);

    /// <summary>目录标签行名字色（D7）：已有（全部/部分）= 主题主文字；无 = 次要灰（侧栏 bool 版同色值）。</summary>
    public static Brush CatalogRowNameForeground(TagCatalogApplyState state)
        => TagRowNameForeground(state != TagCatalogApplyState.None);

    /// <summary>组展开 → 标签行列表可见（目录树态：折叠时子行整体收起）。</summary>
    public static Visibility IsExpandedToVisibility(bool isExpanded)
        => isExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 未定义 chip 的 AutomationId（batch-tag-management Step 3，实机走查定位用）：
    /// 前缀 + 标签名（chip 实例随 ItemsControl 模板实例化，x:Bind 函数绑定逐项生成稳定标记）。
    /// </summary>
    public static string UndefinedChipAutomationId(string name) => "UndefinedChip_" + name;

    /// <summary>
    /// 图库右栏选中集并集 chip 的 AutomationId（batch-tag-management Step 4，实机走查定位用）：
    /// UndefinedChipAutomationId 同款模式（x:Bind 函数绑定逐项生成）。
    /// </summary>
    public static string SelectionChipAutomationId(string name) => "SelectionChip_" + name;

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
