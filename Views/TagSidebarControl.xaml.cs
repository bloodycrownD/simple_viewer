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

namespace SimpleViewer.Views;

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

    private static bool IsShiftKeyDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }
}

/// <summary>
/// 标签栏模板的 x:Bind 函数转换器（静态函数绑定；画刷惰性初始化，仅 UI 线程访问）。
/// </summary>
public static class TagSidebarConverters
{
    private static readonly SolidColorBrush ActiveChipBrush = new(Windows.UI.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush InactiveChipBrush = new(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush RadioBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));

    /// <summary>互斥/多选徽章文本。</summary>
    public static string ExclusiveBadge(bool exclusive) => exclusive ? "互斥" : "多选";

    /// <summary>组计数文本（组内标签引用张数合计）。</summary>
    public static string GroupCountText(int count) => $"{count} 张";

    /// <summary>chip 计数文本。</summary>
    public static string CountText(int count) => count.ToString();

    /// <summary>chip 背景：筛选激活高亮（半透明叠加层，明暗主题通用）。</summary>
    public static Brush ChipBackground(bool isActive)
    {
        EnsureThemeBrushes();
        return isActive ? ActiveChipBrush : InactiveChipBrush;
    }

    /// <summary>互斥组单选圆点画刷（强调色）。</summary>
    public static Brush RadioDotBrush(bool _)
    {
        EnsureThemeBrushes();
        return RadioBrush;
    }

    /// <summary>bool → 可见。</summary>
    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>非未分组 → 可见（组管理按钮）。</summary>
    public static Visibility NotUngroupedToVisibility(bool isUngrouped)
        => isUngrouped ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>命令存在 → 可见（未分组标签无重命名/删除按钮）。</summary>
    public static Visibility CommandToVisibility(ICommand? command)
        => command is null ? Visibility.Collapsed : Visibility.Visible;

    private static void EnsureThemeBrushes()
    {
        // 系统强调色仅初始化一次；其余为固定 ARGB（依赖按钮/主题画刷自身做明暗适配）。
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        ActiveChipBrush.Color = Windows.UI.Color.FromArgb(0x3D, accent.R, accent.G, accent.B);
        RadioBrush.Color = accent;
    }
}
