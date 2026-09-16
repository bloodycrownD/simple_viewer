// 职责：瀑布流视图 code-behind——ItemsRepeater 装配（MasonryLayout 宽高比注入、批量数据源）、
//       缩略图按需加载接线（ElementPrepared 加载 / ElementClearing 取消）、卡片点击转发（Shift 键状态）、resize 去抖重排。
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
/// </summary>
public static class WaterfallConverters
{
    private static SolidColorBrush? _accentBorderBrush;
    private static SolidColorBrush? _neutralBorderBrush;

    /// <summary>选中态边框画刷（系统强调色）。</summary>
    public static Brush CardBorderBrush(bool isSelected)
    {
        EnsureBorderBrushes();
        return isSelected ? _accentBorderBrush! : _neutralBorderBrush!;
    }

    /// <summary>缩略图透明度：未到位为 0（浅色占位），到位为 1（经 OpacityTransition 渐入）。</summary>
    public static double PresenceOpacity(object? value) => value is null ? 0d : 1d;

    /// <summary>非空文本 → 可见。</summary>
    public static Visibility NonEmptyTextToVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>选中 → 可见（选中角标）。</summary>
    public static Visibility SelectedToVisibility(bool isSelected)
        => isSelected ? Visibility.Visible : Visibility.Collapsed;

    private static void EnsureBorderBrushes()
    {
        if (_accentBorderBrush is not null && _neutralBorderBrush is not null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        _accentBorderBrush = new SolidColorBrush((Windows.UI.Color)resources["SystemAccentColor"]);
        _neutralBorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    }
}
