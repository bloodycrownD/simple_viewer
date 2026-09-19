// 职责：瀑布流视图 code-behind——ItemsRepeater 装配（MasonryLayout 宽高比注入、批量数据源）、
//       缩略图按需加载接线（ElementPrepared 加载 / ElementClearing 取消）、卡片点击转发（Click + Ctrl/Shift 键状态，
//       cr/P1-6 卡片 Button 化）、卡片 hover 视觉（上浮 2px + 未选态勾选章显隐，视觉对齐 demo .card:hover/.check）、
//       卡片拖拽打标启动（DragStarting：向 MainViewModel 取整集/单卡路径集并写入 DataPackage 标记）。
// 不变量：缩略图按需加载（禁止一次性为全部项加载）；realized 元素经 Tag 槽位回查 VM（ItemsRepeater
//         不设置 DataContext；卡片模板 Tag="{x:Bind}" 携带项 VM 自身，ElementPrepared 覆写为同一引用）；
//         卡片点击经 Tag 槽位回查 VM 并读取 Ctrl/Shift 键状态交 MainViewModel 分流
//         （2026-09-19 Explorer 心智：无修饰单选重置 / Ctrl 加减选 / Shift 范围重置）；
//         去抖窗口内连续 resize 不触发重排（避免拖拽中间态 O(n) 重算与视觉跳动）；
//         拖拽启动不改选中集（拖拽是打标手势不是选卡手势；路径集暂存 VM 侧，Drop 时消费）。
// 调用链：MainWindow（ContentControl 宿主注入）→ WaterfallView → MainViewModel.HandleCardTapped → GalleryItemViewModel；
//         卡片拖拽 → OnCardDragStarting → MainViewModel.BeginCardDrag →（Drop 在 TagSidebarControl）
//         → MainViewModel.ApplyTagToDraggedCardsAsync。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace SimpleViewer.Views;

/// <summary>
/// 瀑布流图库视图（spec Step 8/10）。
/// </summary>
public sealed partial class WaterfallView : UserControl
{
    /// <summary>
    /// 卡片拖拽数据的自定义格式标记（2026-09-19 拖拽打标）：值仅为计数字符串——
    /// 同进程内路径集以 MainViewModel 侧暂存为准，DataPackage 只承担“这是本应用卡片拖拽”的判别，
    /// 目标侧（标签树行）经 DataView.Contains 判定有效性，外部拖入（无此格式）一律拒绝。
    /// </summary>
    public const string CardDragFormat = "SimpleViewer.CardPaths";

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

        // 布局后按"实际卡宽 × 显示缩放"更新缩略图分桶（宽视口少列数时实际卡宽 > 目标 240，
        // 固定 240×DPI 桶会被拉伸发糊；值变化才更新，避免每次布局都动静态状态）。
        Repeater.LayoutUpdated += (_, _) =>
        {
            var bucket = (int)Math.Ceiling(Masonry.ActualCardWidth * App.DisplayScale / 120.0) * 120;
            if (bucket > 0 && bucket != GalleryItemViewModel.ThumbnailBucket)
            {
                GalleryItemViewModel.ThumbnailBucket = bucket;
            }
        };
    }

    /// <summary>
    /// 卡片点击转发（cr/P1-6：卡片为 Button + Click——键盘/UIA Invoke 可达，RULE 硬约束）：
    /// Tag 槽位回查卡片 VM，Ctrl/Shift 键实时状态交 MainViewModel 分流
    /// （2026-09-19 Explorer 心智：无修饰单选重置 / Ctrl 加减选 / Shift 范围重置）。
    /// RoutedEventArgs 不携带修饰键，按 MainWindow.IsKeyDown 同模式读取当前线程键盘状态
    /// （点击同步触发，状态可靠）。
    /// </summary>
    private void OnCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryItemViewModel viewModel })
        {
            ViewModel.HandleCardTapped(viewModel, IsControlKeyDown(), IsShiftKeyDown());
        }
    }

    /// <summary>
    /// 卡片 hover 进入（视觉对齐 demo .card:hover translateY(-2px)）：上浮 2px（Translation 不影响布局）
    /// 并显示未选态勾选章（半透明黑底白描边；选中态常显由绑定控制，此处跳过）。
    /// 卡片为 Button（cr/P1-6），参数类型取 FrameworkElement 通用。
    /// </summary>
    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement card)
        {
            card.Translation = new System.Numerics.Vector3(0, -2, 0);
            SetHoverCheck(card, visible: true);
        }
    }

    /// <summary>卡片 hover 离开：回落原位并隐藏未选态勾选章。</summary>
    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement card)
        {
            card.Translation = default;
            SetHoverCheck(card, visible: false);
        }
    }

    /// <summary>未选态勾选章显隐（选中态由 IsSelected 绑定常显，跳过避免覆盖绑定值）。</summary>
    private static void SetHoverCheck(FrameworkElement card, bool visible)
    {
        if (card.Tag is GalleryItemViewModel { IsSelected: false }
            && card.FindName("CheckBadge") is Border badge)
        {
            badge.Opacity = visible ? 1d : 0d;
        }
    }

    private static bool IsShiftKeyDown()
    {
        // 只判 Down：Locked 位对 Shift 无意义，中文 IME 切中英文会置位（曾致普通点击全变连选）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static bool IsControlKeyDown()
    {
        // 只判 Down（与 IsShiftKeyDown 同模式；禁判 Locked 位——RULE 铁律）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    /// <summary>
    /// 卡片拖拽启动（2026-09-19 拖拽打标）：Tag 槽位回查卡片 VM → MainViewModel.BeginCardDrag 取路径集
    /// （选中集内 = 整集，否则仅该卡；不改选中集）→ DataPackage 写自定义格式标记（值为计数文本）+
    /// AllowedOperations=Copy。
    /// 拖拽跟随视觉（2026-09-19 拖拽视觉）：WinAppSDK 1.6 的 DragStartingEventArgs.DragUI 实际提供
    /// SetContentFromSoftwareBitmap/SetContentFromBitmapImage/SetContentFromDataPackage
    /// （另有 GetDeferral/GetPosition 可用；此前“仅 AllowedOperations/Data/DragUI/Cancel、无法定制”
    /// 的结论有误，特此纠正——DragUIOverride/AcceptedOperation 确实不在 DragStartingEventArgs 上，
    /// 那两者属于目标侧 DragEventArgs）。据此定制跟随视觉：优先用加载路径预生成的 ~120px 短边小位图
    /// （常规体验：小缩略图随鼠标），无小位图回退整张缩略图 BitmapImage（bucket 360+ 偏大但仍优于
    /// 整卡快照），两者皆无才落系统默认（被拖元素整体快照）。同步禁用 GetDeferral 异步生成——
    /// 拖拽启动须即时，异步等待会拖慢入场。多选计数 caption 由目标侧 TagSidebarControl.DragOver 设置。
    /// 拖拽与 Click/DoubleTapped 天然共存：系统拖拽需按住位移超阈值才进入，单击/双击不受影响
    /// （cr/P1-6 卡片 Button 化后依旧，Button 的 Click 判定被系统拖拽接管时自然取消）。
    /// </summary>
    private void OnCardDragStarting(object sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GalleryItemViewModel cardVm })
        {
            e.Cancel = true;
            return;
        }

        var paths = ViewModel.BeginCardDrag(cardVm);
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetData(CardDragFormat, paths.Count.ToString());
        e.AllowedOperations = DataPackageOperation.Copy;

        // 拖拽跟随视觉（优先级：预生成小位图 > 缩略图 BitmapImage > 系统默认整卡快照）。
        if (cardVm.DragVisual is { PixelWidth: > 0, PixelHeight: > 0 } visual)
        {
            // anchorPoint 语义 = 小图视觉上与鼠标指针对齐的点，取中心使指针居中于缩略图。
            e.DragUI.SetContentFromSoftwareBitmap(
                visual,
                new Point(visual.PixelWidth / 2.0, visual.PixelHeight / 2.0));
        }
        else if (cardVm.Thumbnail is BitmapImage fallback && fallback.PixelWidth > 0)
        {
            // 回退：直接用缩略图源（DragUI 渲染位图不缩放，bucket 360+ 显示偏大，但仍优于整卡快照）。
            e.DragUI.SetContentFromBitmapImage(fallback);
        }
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
