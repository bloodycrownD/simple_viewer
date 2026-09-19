// 职责：标签栏视图 code-behind——构造注入（MainViewModel + TagSidebarViewModel）、行点击转发、
//       行悬停浮现管理按钮（RowCommands 容器 Opacity 切换）与拖拽卡片到标签行打标。
//       （WrapPanel 与 TagSidebarConverters 已拆出为独立文件，cr/P2-8。）
// 不变量：无交互逻辑（分流/命令全部在视图模型）；标签行/组行点击经 Tag 槽位回查 VM（ItemsRepeater 不设置
//         DataContext）；标签行点击 = 筛选——无修饰 = 单选筛选（唯一选中再点取消）、Ctrl = 加减选
//         （多标签 OR；键状态只判 Down，与实现一致，cr/P2-7 注释对齐）；
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
    /// 修饰键只读 Ctrl（2026-09-19 对齐卡片选择心智）：无修饰 = 单选筛选（唯一选中时再点 = 取消），
    /// Ctrl+点击 = 加/减选（多标签 OR）。
    /// </summary>
    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TagChipViewModel chip })
        {
            _ = ViewModel.HandleChipTappedAsync(chip, IsControlKeyDown());
        }
    }

    private static bool IsControlKeyDown()
    {
        // 只判 Down（照抄 WaterfallView.IsControlKeyDown 模式；禁判 Locked 位——RULE 铁律）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
