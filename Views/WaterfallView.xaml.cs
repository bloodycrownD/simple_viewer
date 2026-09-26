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

    /// <summary>卡片拖拽启动的位移阈值（DIP）：按压后移动超过该距离即主动 StartDragAsync。</summary>
    private const double DragStartThresholdDip = 8.0;

    private readonly DispatcherQueueTimer _resizeDebounceTimer;

    // —— 命令式拖拽启动状态（2026-09-19 拖拽二次修复，详见 OnCardPointerPressed 注释）——
    /// <summary>按压起点元素（位移判定只在同一元素上累计）。</summary>
    private FrameworkElement? _dragPressElement;

    /// <summary>按压起点（元素本地坐标，DIP）。</summary>
    private Point _dragPressPoint;

    /// <summary>是否处于"按压中、等待位移超阈值"的待拖拽态。</summary>
    private bool _dragArmed;

    /// <summary>卡片根元素的指针事件处理器组（AddHandler 挂接，ElementClearing 时成对 Remove）。</summary>
    private readonly Dictionary<FrameworkElement, PointerEventHandler[]> _cardPointerHandlers = new();

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

        // 首帧闸门武装点①（2026-09-24 首帧卡死根治，见 Services/FirstFrameGate.cs 背景）：
        // 瀑布流进入树即闭闸，布局安定 400ms 后开闸——冷缓存风暴期的缩略图应用段被整体推迟到
        // 首个大批量呈现完成之后，移出与合成器互等的竞态窗口。
        Loaded += OnWaterfallViewLoaded;

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
    /// 拖拽与 Click/DoubleTapped 天然共存：系统拖拽需按住位移超阈值才进入，单击/双击不受影响。
    /// 发送方为卡片模板外层的拖拽宿主 Grid（2026-09-19 回归修复）：WinUI 3 的 Button 吞指针输入，
    /// Button.CanDrag 不会触发 DragStarting（已知问题）——cr/P1-6 卡片 Button 化时拖拽源随之失效，
    /// 官方解法是拖拽源放外层普通 UIElement；Tag 槽位由 OnElementPrepared 写在模板根（即宿主 Grid）。
    /// </summary>
    private async void OnCardDragStarting(object sender, DragStartingEventArgs e)
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

        // 拖拽跟随视觉（优先级：常驻小位图 > 懒生成小位图 > 缩略图 BitmapImage > 系统默认整卡快照）。
        // 懒生成（2026-09-23 冻结修复）：小位图不再随缩略图预生成（预生成全链跑在 UI STA，WIC 完成
        // 封送回 STA 与首帧布局互等成零 CPU 死锁——实机 8 次 ≥15s 冻结），改为拖拽发起时按需生成
        //（GetDeferral 保持拖拽会话等位图就绪，字节走缩略图缓存命中 + 毫秒级小图解码，全程后台线程）。
        if (cardVm.DragVisual is { PixelWidth: > 0, PixelHeight: > 0 } visual)
        {
            // anchorPoint 语义 = 小图视觉上与鼠标指针对齐的点，取中心使指针居中于缩略图。
            e.DragUI.SetContentFromSoftwareBitmap(
                visual,
                new Point(visual.PixelWidth / 2.0, visual.PixelHeight / 2.0));
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var lazy = await cardVm.GetOrCreateDragVisualAsync();
            if (lazy is { PixelWidth: > 0, PixelHeight: > 0 } lazyVisual)
            {
                e.DragUI.SetContentFromSoftwareBitmap(
                    lazyVisual,
                    new Point(lazyVisual.PixelWidth / 2.0, lazyVisual.PixelHeight / 2.0));
                return;
            }

            if (cardVm.Thumbnail is BitmapImage fallback && fallback.PixelWidth > 0)
            {
                // 回退：直接用缩略图源（DragUI 渲染位图不缩放，bucket 360+ 显示偏大，但仍优于整卡快照）。
                e.DragUI.SetContentFromBitmapImage(fallback);
            }
        }
        catch (Exception)
        {
            // 跟随视觉属锦上添花：任何失败走系统默认整卡快照，不影响拖拽主链路。
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // XAML 回调异常防弹（2026-09-25 0xc000027b stowed 闪退实锤）：ElementPrepared 在
        // MeasureOverride 内同步触发，此回调内逃逸的任何托管异常会被 XAML stowed 直接杀进程
        //（不经过 UnhandledException/看门狗，日志全干净+dump 栈实锤）——回调体只做接线，
        // 全 try/catch 兜底落日志。
        try
        {
            // ItemsRepeater 不设置元素 DataContext：经 Tag 槽位记录 VM，ElementClearing 时回查释放视觉资源（cr/P1-5）。
            if (Repeater.ItemsSourceView?.GetAt(args.Index) is GalleryItemViewModel viewModel
                && args.Element is FrameworkElement element)
            {
                element.Tag = viewModel;

                // 缩略图加载启动脱离布局回调（2026-09-25 同一闪退）：此前同步启动的异步链在
                // 全同步完成路径下（内存未命中→磁盘读秒回→解码启动）整链跑穿在 MeasureOverride
                // 内——回调内异常=stowed 死刑 + 布局被 IO 拖长。TryEnqueue 推迟到布局外再启动。
                // 视图代数守卫（2026-09-26 tag-op-finalizer-crash 修复 C①）：入队时捕获代数，
                // ElementClearing → ReleaseVisuals 先执行（先清后启竞态）时回调被丢弃，不再把
                // 缩略图挂回已回收 VM（悬挂 BitmapImage 绕过退役队列）。
                var vm = viewModel;
                var generation = vm.VisualGeneration;
                _ = DispatcherQueue.TryEnqueue(() => vm.BeginLoadThumbnail(generation));

                // 命令式拖拽（2026-09-19 拖拽二次修复）：CanDrag 手势路径在"Button 子元素拉伸占满宿主"时
                // 永远不触发（Button 捕获指针拦截手势识别，Q&A "Drag Grid with Streached elements" 实锤）——
                // 改为 handledEventsToo 监听按压/移动，位移超阈值主动 StartDragAsync（官方命令式 API，
                // 绕开手势识别）。Click/双击不受影响（小位移释放仍走 Button）。
                var handlers = new[]
                {
                    new PointerEventHandler(OnCardPointerPressed),
                    new PointerEventHandler(OnCardPointerMoved),
                    new PointerEventHandler(OnCardPointerReleased),
                    new PointerEventHandler(OnCardPointerCaptureLost),
                };
                element.AddHandler(UIElement.PointerPressedEvent, handlers[0], handledEventsToo: true);
                element.AddHandler(UIElement.PointerMovedEvent, handlers[1], handledEventsToo: true);
                element.AddHandler(UIElement.PointerReleasedEvent, handlers[2], handledEventsToo: true);
                element.AddHandler(UIElement.PointerCaptureLostEvent, handlers[3], handledEventsToo: true);
                _cardPointerHandlers[element] = handlers;
            }
        }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog($"[ElementPrepared 异常兜底] index={args.Index}", ex);
        }
    }

    /// <summary>按压起点记录：左键按下进入待拖拽态（不捕获指针，Button 的 Click 交互照常）。</summary>
    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            _dragArmed = false;
            return;
        }

        _dragArmed = true;
        _dragPressElement = element;
        _dragPressPoint = e.GetCurrentPoint(element).Position;
    }

    /// <summary>位移超阈值即一次性主动启动拖拽会话（DragStarting 随后在本元素触发，走既有 OnCardDragStarting）。</summary>
    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragArmed || sender is not FrameworkElement element || !ReferenceEquals(element, _dragPressElement))
        {
            return;
        }

        var point = e.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragArmed = false;
            return;
        }

        var dx = point.Position.X - _dragPressPoint.X;
        var dy = point.Position.Y - _dragPressPoint.Y;
        if (dx * dx + dy * dy < DragStartThresholdDip * DragStartThresholdDip)
        {
            return;
        }

        _dragArmed = false; // 一次性：拖拽会话期间不再重复启动
        e.Handled = true; // 吃掉本移动事件，避免 Button 继续按"按住"处理
        _ = element.StartDragAsync(point);
    }

    /// <summary>释放/捕获丢失：解除待拖拽态。</summary>
    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e) => _dragArmed = false;

    /// <summary>捕获丢失（含拖拽会话接管）：解除待拖拽态。</summary>
    private void OnCardPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragArmed = false;

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement { Tag: GalleryItemViewModel viewModel } element)
        {
            // 取消加载 + 释放已加载的缩略图位图与拖拽小图（cr/P1-5）：卡片 VM 全量常驻不随回收丢弃，
            // 仅取消加载会让视觉资源留在 VM 上累积；释放后重新 Realize 走 BeginLoadThumbnail 重载恢复。
            viewModel.ReleaseVisuals();
            element.Tag = null;

            // 成对摘除命令式拖拽的指针处理器（防回收复用后重复挂接）。
            if (_cardPointerHandlers.Remove(element, out var handlers))
            {
                element.RemoveHandler(UIElement.PointerPressedEvent, handlers[0]);
                element.RemoveHandler(UIElement.PointerMovedEvent, handlers[1]);
                element.RemoveHandler(UIElement.PointerReleasedEvent, handlers[2]);
                element.RemoveHandler(UIElement.PointerCaptureLostEvent, handlers[3]);
            }
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

        // 首帧闸门武装点②（2026-09-24）：Reset/Replace/Remove 触发全量重排 = 新布局代——
        // 筛选切换后瀑布流重新 Realize 的风暴与冷启动首帧同构（21:21 用户会话卡死现场），
        // 该代首帧呈现前同样不进缩略图应用段。
        ArmFirstFrameGate();
    }

    /// <summary>首帧闸门开安定计时器（null=未武装）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _firstFrameSettleTimer;

    /// <summary>闸门武装代是否已见到布局完成（LayoutUpdated 后才开始安定计时）。</summary>
    private bool _firstFrameLayoutSeen;

    private void OnWaterfallViewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Loaded -= OnWaterfallViewLoaded;
        ArmFirstFrameGate();
    }

    /// <summary>
    /// 武装首帧闸门：闭闸 → 等本代布局完成（LayoutUpdated）→ 400ms 安定期 → 开闸。
    /// 不用 CompositionTarget.Rendering 计帧：Rendering 首拍=帧开始渲染而非呈现完成，
    /// 首版实机复测仍卡（apply 落进合成器消化 245 卡首呈现的窗口）；确定性时序免疫帧计数死角
    ///（开闸仅需合成器消化完大批量首呈现，~数百毫秒；应用段本有 16ms 节流续摊）。
    /// </summary>
    private void ArmFirstFrameGate()
    {
        Services.FirstFrameGate.Arm();
        _firstFrameLayoutSeen = false;
        Repeater.LayoutUpdated -= OnGateGenerationLayoutUpdated;
        Repeater.LayoutUpdated += OnGateGenerationLayoutUpdated;
    }

    private void OnGateGenerationLayoutUpdated(object? sender, object e)
    {
        if (_firstFrameLayoutSeen)
        {
            return;
        }

        _firstFrameLayoutSeen = true;
        Repeater.LayoutUpdated -= OnGateGenerationLayoutUpdated;

        _firstFrameSettleTimer?.Stop();
        _firstFrameSettleTimer = DispatcherQueue.CreateTimer();
        _firstFrameSettleTimer.Interval = TimeSpan.FromMilliseconds(400);
        _firstFrameSettleTimer.IsRepeating = false;
        _firstFrameSettleTimer.Tick += (_, _) => Services.FirstFrameGate.Open();
        _firstFrameSettleTimer.Start();
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

    // 勾选章/角标画刷静态缓存（2026-09-24 审计修复）：色值组合有限（未选/选中各一、角标按 hue），
    // 此前每次求值 new——滚动万张卡片产生数万 Brush（DependencyObject 族）裸交 GC 终结器，
    // 违反「XAML 对象永不可裸交给 GC」铁律的全场最大批量点；缓存后每色值仅一份、复用终身。
    private static SolidColorBrush? _checkBackgroundUnselected;
    private static SolidColorBrush? _checkBackgroundSelected;
    private static SolidColorBrush? _checkBorderUnselected;
    private static readonly Dictionary<int, SolidColorBrush> _badgeBackgrounds = [];

    /// <summary>卡片边框画刷：选中 = 系统强调色；未选 = 透明（demo .card border 2px transparent）。</summary>
    public static Brush CardBorderBrush(bool isSelected)
    {
        EnsureBorderBrushes();
        return isSelected ? _accentBorderBrush! : _transparentBorderBrush!;
    }

    /// <summary>勾选章底色：未选 = 黑 35%；选中 = 强调色 85%（demo --sel；缓存复用）。</summary>
    public static Brush CheckBackground(bool isSelected)
    {
        if (!isSelected)
        {
            return _checkBackgroundUnselected ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0x00, 0x00, 0x00));
        }

        return _checkBackgroundSelected ??= CreateAccentBrush(0xD9);
    }

    /// <summary>勾选章描边：未选 = 白 90%；选中 = 与底色一致的强调色（缓存复用）。</summary>
    public static Brush CheckBorderBrush(bool isSelected)
    {
        if (!isSelected)
        {
            return _checkBorderUnselected ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }

        EnsureBorderBrushes();
        return _accentBorderBrush!;
    }

    /// <summary>标签角标底色：hue &lt; 0 = 「+N」黑 55% 底；否则组 hue 92% 不透明（demo .badge；按 hue 缓存）。</summary>
    public static Brush BadgeBackground(int hue)
    {
        if (hue < 0)
        {
            return _badgeBackgrounds.TryGetValue(-1, out var more)
                ? more
                : (_badgeBackgrounds[-1] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0x00, 0x00, 0x00)));
        }

        if (!_badgeBackgrounds.TryGetValue(hue, out var badge))
        {
            badge = TagSidebarConverters.FromHsl(hue, 0.50, 0.45, 0xEB);
            _badgeBackgrounds[hue] = badge;
        }

        return badge;
    }

    /// <summary>按 alpha 构建强调色画刷（选中态复用同一强调色源）。</summary>
    private static SolidColorBrush CreateAccentBrush(byte alpha)
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

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
