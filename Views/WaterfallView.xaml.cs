// 职责：瀑布流视图 code-behind——ItemsRepeater 装配（MasonryLayout 宽高比注入、批量数据源）、
//       缩略图按需加载接线（ElementPrepared 加载 / ElementClearing 取消）、卡片点击转发（Shift 键状态）、resize 去抖重排、
//       卡片 hover 视觉（上浮 2px + 未选态勾选章显隐，视觉对齐 demo .card:hover/.check）。
// 不变量：缩略图按需加载（禁止一次性为全部项加载）；realized 元素经 Tag 槽位回查 VM（ItemsRepeater
//         不设置 DataContext；卡片模板 Tag="{x:Bind}" 携带项 VM 自身，ElementPrepared 覆写为同一引用）；
//         卡片点击经 Tag 槽位回查 VM 并读取 Shift 键状态交 MainViewModel 分流（连选属 Step 10）；
//         去抖窗口内连续 resize 不触发重排（避免拖拽中间态 O(n) 重算与视觉跳动）。
// 调用链：MainWindow（ContentControl 宿主注入）→ WaterfallView → MainViewModel.HandleCardTapped → GalleryItemViewModel。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

/// <summary>
/// 瀑布流图库视图（spec Step 8/10）。
/// </summary>
public sealed partial class WaterfallView : UserControl
{
    /// <summary>resize 去抖窗口（毫秒）：停止变化后按最终视口宽重排列数/列宽。</summary>
    private const int ResizeDebounceMilliseconds = 200;

    private readonly DispatcherQueueTimer _resizeDebounceTimer;

    public MainViewModel ViewModel { get; }

    public WaterfallView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // 宽高比由数据项提供（未知回退 1:1 在布局内兜底）。
        Masonry.AspectRatioProvider = static item => item is GalleryItemViewModel vm ? vm.AspectRatio : 1.0;
        Repeater.ItemsSource = viewModel.Waterfall.Items;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;

        // 数据源 Reset/Replace/Remove → 强制全量重算（WinAppSDK 1.6 的布局 OnItemsChanged
        // 虚方法不可重写，经宿主侧订阅数据源通知置 ForceRecompute，效果等价）；
        // 尾部 Add 由布局按计数差增量续算，无需处理。
        viewModel.Waterfall.Items.CollectionChanged += OnItemsSourceChanged;

        // resize 去抖：连续 SizeChanged 只重置计时器，静止后触发一次强制重排。
        _resizeDebounceTimer = DispatcherQueue.CreateTimer();
        _resizeDebounceTimer.Interval = TimeSpan.FromMilliseconds(ResizeDebounceMilliseconds);
        _resizeDebounceTimer.IsRepeating = false;
        _resizeDebounceTimer.Tick += (_, _) =>
        {
            Masonry.ForceRecompute = true;
            Repeater.InvalidateMeasure();
        };
    }

    /// <summary>
    /// 卡片点击转发：Tag 槽位回查卡片 VM，Shift 键实时状态交 MainViewModel 分流（Step 10：连选）。
    /// TappedRoutedEventArgs 不携带修饰键，按 MainWindow.IsKeyDown 同模式读取当前线程键盘状态
    /// （点击同步触发，状态可靠）。
    /// </summary>
    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryItemViewModel viewModel })
        {
            ViewModel.HandleCardTapped(viewModel, IsShiftKeyDown());
        }
    }

    /// <summary>
    /// 卡片 hover 进入（视觉对齐 demo .card:hover translateY(-2px)）：上浮 2px（Translation 不影响布局）
    /// 并显示未选态勾选章（半透明黑底白描边；选中态常显由绑定控制，此处跳过）。
    /// </summary>
    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.Translation = new System.Numerics.Vector3(0, -2, 0);
            SetHoverCheck(card, visible: true);
        }
    }

    /// <summary>卡片 hover 离开：回落原位并隐藏未选态勾选章。</summary>
    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.Translation = default;
            SetHoverCheck(card, visible: false);
        }
    }

    /// <summary>未选态勾选章显隐（选中态由 IsSelected 绑定常显，跳过避免覆盖绑定值）。</summary>
    private static void SetHoverCheck(Border card, bool visible)
    {
        if (card.Tag is GalleryItemViewModel { IsSelected: false }
            && card.FindName("CheckBadge") is Border badge)
        {
            badge.Opacity = visible ? 1d : 0d;
        }
    }

    private static bool IsShiftKeyDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // ItemsRepeater 不设置元素 DataContext：经 Tag 槽位记录 VM，ElementClearing 时回查取消加载。
        if (Repeater.ItemsSourceView?.GetAt(args.Index) is GalleryItemViewModel viewModel
            && args.Element is FrameworkElement element)
        {
            element.Tag = viewModel;
            viewModel.BeginLoadThumbnail();
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement { Tag: GalleryItemViewModel viewModel } element)
        {
            viewModel.CancelThumbnailLoad();
            element.Tag = null;
        }
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnItemsSourceChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            return;
        }

        Masonry.ForceRecompute = true;
        Repeater.InvalidateMeasure();
    }
}

/// <summary>
/// 瀑布流卡片模板的 x:Bind 函数转换器（静态函数绑定；画刷惰性初始化，仅 UI 线程访问）。
/// 视觉对齐 demo：未选边框透明（卡片靠底色分层）、选中 2px 强调色描边；
/// 勾选章未选 = 黑 35% 底白 90% 描边（hover 显示），选中 = 强调色 85% 实心；
/// 角标药丸 = 组 hue 92% 底（「+N」黑色 55% 底）。
/// </summary>
public static class WaterfallConverters
{
    private static SolidColorBrush? _accentBorderBrush;
    private static SolidColorBrush? _transparentBorderBrush;

    /// <summary>卡片边框画刷：选中 = 系统强调色；未选 = 透明（demo .card border 2px transparent）。</summary>
    public static Brush CardBorderBrush(bool isSelected)
    {
        EnsureBorderBrushes();
        return isSelected ? _accentBorderBrush! : _transparentBorderBrush!;
    }

    /// <summary>勾选章底色：未选 = 黑 35%；选中 = 强调色 85%（demo --sel）。</summary>
    public static Brush CheckBackground(bool isSelected)
    {
        if (!isSelected)
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0x00, 0x00, 0x00));
        }

        EnsureBorderBrushes();
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, accent.R, accent.G, accent.B));
    }

    /// <summary>勾选章描边：未选 = 白 90%；选中 = 与底色一致的强调色。</summary>
    public static Brush CheckBorderBrush(bool isSelected)
    {
        if (!isSelected)
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }

        EnsureBorderBrushes();
        return _accentBorderBrush!;
    }

    /// <summary>标签角标底色：hue &lt; 0 = 「+N」黑 55% 底；否则组 hue 92% 不透明（demo .badge）。</summary>
    public static Brush BadgeBackground(int hue)
        => hue < 0
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0x00, 0x00, 0x00))
            : TagSidebarConverters.FromHsl(hue, 0.50, 0.45, 0xEB);

    /// <summary>缩略图透明度：未到位为 0（浅色占位），到位为 1（经 OpacityTransition 渐入）。</summary>
    public static double PresenceOpacity(object? value) => value is null ? 0d : 1d;

    /// <summary>非空文本 → 可见。</summary>
    public static Visibility NonEmptyTextToVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>选中 → 不透明度（勾选章：选中常显，未选 0 由 hover code-behind 临时置 1）。</summary>
    public static double SelectedToOpacity(bool isSelected) => isSelected ? 1d : 0d;

    private static void EnsureBorderBrushes()
    {
        if (_accentBorderBrush is not null && _transparentBorderBrush is not null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        _accentBorderBrush = new SolidColorBrush((Windows.UI.Color)resources["SystemAccentColor"]);
        _transparentBorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    }
}
