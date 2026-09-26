// 职责：单图视图 code-behind——文件名文本组装（显示名 = 剥离标签段；右栏信息区，底部文件名栏已移除）、
//       滚轮缩放/拖拽平移交互、右栏标签管理转发（chip ✕ 移除 → MainViewModel 单图 toggle 管线）。
// 不变量：ViewModel 构造注入（先赋值后 InitializeComponent，沿用 SettingsPage 惯例）；
//         仅响应显示名/完整名属性变更重建文件名文本，其余绑定走 XAML x:Bind；
//         遮盖式布局（2026-09-19）：ImageHost 画布铺满整根（几何=整窗恒定），右栏（280/36）与
//         文件名栏为浮层——收展右栏只改变遮盖范围，画布几何不变、不触发重解码；
//         图片适配盒 = 可见区（2026-09-25 detail-view-fit-visible-area）：ViewerImage 靠
//         ApplyChromeInsets 设 Margin（左=左栏宽 / 上=顶部信息横条底边 / 右=右栏宽 / 下=0）+
//         Stretch 对齐，把「整窗 contain」收窄为「未遮挡可见区 contain」——图片不再被 chrome 切掉；
//         画布几何与解码尺寸链（VM 仍取 max(画布宽,高)）都不受本改动影响；
//         ImageHost.SizeChanged 仍是解码尺寸源（仅窗口 resize 触发）；
//         缩放/平移是纯视图交互态（不入 VM）：切图（ImageSource 变化）与双击复位；
//         CompositeTransform 应用顺序 Scale→Rotate→Translate——平移在最外层，
//         拖拽按屏幕 delta 直接累加；光标锚点缩放需按旋转角变换指针向量，
//         且锚点中心按「Margin 内缩后的元素盒」实算（见 ZoomAt，上下内缩不对称）。

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

/// <summary>
/// 单图查看视图（D14：自 MainWindow Row1 抽出的 UserControl；2026-09-19 统一口径：
/// 文件名显示剥离标签段的显示名，完整名挂 tooltip，标签由右栏 chips 承载）。
/// </summary>
public sealed partial class SingleImageView : UserControl
{
    /// <summary>滚轮每格缩放倍率（前滚放大；1.15^±1 每格）。</summary>
    private const double ZoomStepFactor = 1.15;

    /// <summary>缩放下限（相对 fit 尺寸；fit = Scale 1 时的「可见区 contain」尺寸，见 <see cref="ApplyChromeInsets"/>）。</summary>
    private const double MinZoom = 0.1;

    /// <summary>缩放上限（相对 fit 尺寸；放大超过解码分辨率会像素化，属已知限制）。</summary>
    private const double MaxZoom = 8.0;

    /// <summary>
    /// 全分辨率重解码触发阈值（2026-09-19）：解码尺寸 ≈ fit 尺寸，缩放超过此值即放大了
    /// 解码像素，通知 VM 按需加载全分辨率版本（每图一次，失败不重试）。
    /// 「相对 fit 尺寸」自 2026-09-25 起字面成立：Scale=1 即图片按 Uniform contain 进可见区适配盒
    /// （detail-view-fit-visible-area），故该阈值判定的仍是"大于 fit 显示尺寸"。
    /// </summary>
    private const double FullResZoomThreshold = 1.2;

    /// <summary>左栏展开宽度（逻辑 px；与 MainWindow 左栏展开态 Border Width 一致；图库右栏 GallerySelectionPanelControl 展开宽 280 同族锚点，改值两处同步）。</summary>
    private const double SidebarExpandedWidth = 280;

    /// <summary>左栏折叠窄条宽度（逻辑 px；与 MainWindow 左栏折叠态 Border Width 一致；图库右栏 GallerySelectionPanelControl 折叠宽 36 同族锚点，改值两处同步）。</summary>
    private const double SidebarCollapsedWidth = 36;

    /// <summary>单图右栏展开宽度（逻辑 px；与 XAML InfoPanelOverlay.Width 一致；左栏 SidebarExpandedWidth 同族锚点，改值两处同步）。</summary>
    private const double InfoPanelExpandedWidth = 280;

    /// <summary>单图右栏折叠窄条宽度（逻辑 px；与 XAML InfoPanelCollapsedBar.Width 一致；左栏 SidebarCollapsedWidth 同族锚点，改值两处同步）。</summary>
    private const double InfoPanelCollapsedWidth = 36;

    /// <summary>
    /// 顶部信息横条高度回退值（逻辑 px，detail-view-fit-visible-area）：首帧
    /// <see cref="TopStatusStrip"/> 的 ActualHeight 尚为 0 时用它算上内缩——宁大勿小（图片最多偏小、
    /// 不会顶出可见区被横条切掉）；横条尺寸就绪后 SizeChanged 触发重算修正（实测横条 43 DIP）。
    /// </summary>
    private const double TopStatusStripFallbackHeight = 48;

    /// <summary>缩放/平移交互态归属的图路径（切图复位依据；同图分辨率升级不重置）。</summary>
    private string? _zoomOwnerPath;

    /// <summary>拖拽中的指针 id（<see cref="uint.MaxValue"/> = 未拖拽）。</summary>
    private uint _dragPointerId = uint.MaxValue;

    /// <summary>拖拽上一位置（宿主坐标系；平移 delta 之用）。</summary>
    private Windows.Foundation.Point _lastDragPosition;

    public MainViewModel ViewModel { get; }

    public SingleImageView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.CurrentImageRenamed += OnCurrentImageRenamed;
        RebuildFileNameInlines();
        ApplyChromeInsets();

        // 顶部信息横条尺寸就绪/变化 → 重算图片适配盒的上内缩（detail-view-fit-visible-area）：
        // 首帧 ActualHeight=0 时 ApplyChromeInsets 走 TopStatusStripFallbackHeight 保守值，
        // 本回调在其量测完成后修正为真实底边。XAML 回调异常防弹铁律：回调体只做轻量赋值，
        // 整体 try/catch 兜底落日志绝不外抛（回调内逃逸异常 = XAML stowed 直接杀进程，
        // 不弹不记；先例 WaterfallView.OnElementPrepared / MasonryLayout 覆写）。
        TopStatusStrip.SizeChanged += (_, _) =>
        {
            try
            {
                ApplyChromeInsets();
            }
            catch (Exception ex)
            {
                App.WriteDiagnosticLog("[SingleImageView.TopStatusStrip.SizeChanged 异常兜底]", ex);
            }
        };

        // 视口尺寸源（2026-09-19 修复二次缩放锯齿 + 遮盖式布局）：解码尺寸贴合画布实际区，
        // 显示层 1:1 无重采样。画布铺满整窗且几何恒定——仅窗口 resize 触发 SizeChanged；
        // 侧栏/右栏收展是 chrome 遮盖层显隐，不再改变画布（图片位置不动、不重解码）。
        // 隐藏（切回图库）时 ActualSize 归零，忽略避免触发全尺寸解码。
        ImageHost.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width >= 1 && e.NewSize.Height >= 1)
            {
                _ = ViewModel.OnViewportSizeChangedAsync(
                    (int)e.NewSize.Width, (int)e.NewSize.Height);
            }
        };
    }

    /// <summary>
    /// 同图改名通知（打标重命名，图片字节与 ImageSource 均不变）：仅把缩放/平移交互态的
    /// 归属路径 <see cref="_zoomOwnerPath"/> 追到新路径——区别于切图（ImageSource 与路径都变 → 复位）
    /// 与同图分辨率升级（源变路径不变 → 保持），这是"路径变、源不变"的第三种情形，交互态同样保持
    ///（否则下方 ImageSource 属性变更分支以路径判切图，会把改名误判切图打断用户放大态）。
    /// GIF 改名后会重载换源：事件先行更新归属路径，换源时的路径对比即命中"同图"而不复位。
    /// </summary>
    private void OnCurrentImageRenamed(string oldPath, string newPath)
    {
        if (string.Equals(_zoomOwnerPath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            _zoomOwnerPath = newPath;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentImageDisplayName)
            or nameof(MainViewModel.CurrentFileFullName))
        {
            RebuildFileNameInlines();
        }
        else if (e.PropertyName == nameof(MainViewModel.ImageSource))
        {
            // ImageSource 变化有两种：切图（复位缩放/平移）与同图全分辨率升级
            // （保持交互态——放大正是触发升级的动作，换源后合成器用高分辨率纹理重采样）。
            var path = ViewModel.CurrentImagePath;
            if (!string.Equals(path, _zoomOwnerPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetZoom();
                _zoomOwnerPath = path;
            }
        }
        else if (e.PropertyName is nameof(MainViewModel.TopChromeHeight)
            or nameof(MainViewModel.IsSidebarCollapsed)
            or nameof(MainViewModel.IsInfoPanelCollapsed))
        {
            // 顶部 chrome 行高、左栏收展、右栏收展都影响 chrome 让位与图片适配盒内缩
            //（返回按钮左侧让出左栏实际宽度；图片可见区右侧让出右栏实际宽度）。
            ApplyChromeInsets();
        }
    }

    /// <summary>
    /// chrome 遮让（2026-09-19 遮挡修复）：画布层浮层（右栏展开/折叠条）位于 chrome
    /// 遮盖层之下，顶部被工具栏横行遮盖——可点/可见区必须让出这段
    /// 实际高度（MainWindow 依 SizeChanged 写入 VM）。右栏收起按钮曾因浮层顶到窗口顶
    /// 被工具栏盖住、真实鼠标点不到（UIA Press 不做视觉命中测试，走查假阳性）。
    /// 底部状态栏已移除（2026-09-19）：底部避让删除，浮层底部恒 0。
    /// 2026-09-24 悬空根治：TopChromeHeight 恒=工具栏高（MainInfoBar 浮层化后不再计入）——
    /// 旧行为回执弹出时横条被顶离工具栏悬在画布中部。
    /// 返回图库按钮（2026-09-19 引入，走查 6 收进顶部信息横条首元素）：CanExecute=HasGallery，
    /// CLI 直开无图库时禁用灰态。
    /// 2026-09-25 图片适配盒改「可见区」（detail-view-fit-visible-area）：在既有浮层避让之外，
    /// 额外把 <see cref="ViewerImage"/> 的 Margin 设为可见区内缩——左=左栏实际宽 / 上=顶部信息横条
    /// 底边 / 右=右栏实际宽 / 下=0。ViewerImage 对齐为 Stretch，故「宿主 - Padding - Margin」
    /// 即元素盒（可见区），Uniform 再按比例把图片 contain 进该盒（Scale=1 即可见区 fit）。
    /// 画布几何（ImageHost=整窗恒定）与解码尺寸链都不受影响——本方法只改图片自身的适配盒。
    /// </summary>
    private void ApplyChromeInsets()
    {
        var top = ViewModel.TopChromeHeight;
        InfoPanelOverlay.Margin = new Thickness(0, top, 0, 0);
        InfoPanelCollapsedBar.Margin = new Thickness(0, top, 0, 0);

        // 顶部信息横条（走查 5/6）：整宽铺设 + 上叠 2dip 入工具栏底边——与左栏/工具栏交界从
        // 相邻变叠加（左段钻入左栏下方，ChromeLayer 层级更高盖住），DPI 取整不再透光；
        // 内容左让左栏实际宽 + 14 内边距（返回图库按钮/序号/显示名）。
        TopStatusStrip.Margin = new Thickness(0, Math.Max(0, top - 2), 0, 0);
        var sidebarWidth = ViewModel.IsSidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
        StripContent.Margin = new Thickness(sidebarWidth + 14, 0, 14, 0);

        // 图片适配盒 = 可见区（2026-09-25 detail-view-fit-visible-area）：上内缩取横条底边
        //（= 横条 Margin.Top + 实际高）——ActualHeight 首帧未就绪（0）时退回保守常量，
        // 待 TopStatusStrip.SizeChanged 重算修正；右内缩随右栏收展在 280/36 间切换。
        var infoPanelWidth = ViewModel.IsInfoPanelCollapsed ? InfoPanelCollapsedWidth : InfoPanelExpandedWidth;
        var stripHeight = TopStatusStrip.ActualHeight > 0 ? TopStatusStrip.ActualHeight : TopStatusStripFallbackHeight;
        ViewerImage.Margin = new Thickness(sidebarWidth, TopStatusStrip.Margin.Top + stripHeight, infoPanelWidth, 0);
    }

    // ==================== 滚轮缩放 / 拖拽平移（2026-09-19：单图查看核心交互） ====================

    /// <summary>滚轮缩放：光标为不动点（指针处内容在缩放前后屏幕位置不变）。</summary>
    private void OnImagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewerImage.Visibility != Visibility.Visible)
        {
            return;
        }

        var point = e.GetCurrentPoint(ImageHost);
        var steps = point.Properties.MouseWheelDelta / 120.0;
        var targetScale = Math.Clamp(
            ViewerTransform.ScaleX * Math.Pow(ZoomStepFactor, steps),
            MinZoom,
            MaxZoom);
        ZoomAt(point.Position, targetScale);

        // 放大跨过阈值：按需全分辨率重解码（fire-and-forget；VM 内部防重入）。
        if (targetScale >= FullResZoomThreshold)
        {
            _ = ViewModel.EnsureFullResolutionAsync();
        }

        e.Handled = true;
    }

    /// <summary>双击复位缩放/平移（旋转保留——那是用户显式操作）。</summary>
    private void OnImageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResetZoom();
        e.Handled = true;
    }

    private void OnImagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewerImage.Visibility != Visibility.Visible)
        {
            return;
        }

        _dragPointerId = e.Pointer.PointerId;
        _lastDragPosition = e.GetCurrentPoint(ImageHost).Position;
        ImageHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
        {
            return;
        }

        var position = e.GetCurrentPoint(ImageHost).Position;
        ViewerTransform.TranslateX += position.X - _lastDragPosition.X;
        ViewerTransform.TranslateY += position.Y - _lastDragPosition.Y;
        _lastDragPosition = position;
        e.Handled = true;
    }

    private void OnImagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
        {
            return;
        }

        _dragPointerId = uint.MaxValue;
        ImageHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// 以宿主坐标系 <paramref name="anchor"/> 为不动点缩放到 <paramref name="targetScale"/>。
    /// 推导（变换序 Scale→Rotate→Translate，原点在元素布局中心 C）：屏幕点 = C + T + R·(s·p)。
    /// 锚点 A 对应元素点在 s→s' 后不动：T' = v − (s'/s)·(v − T)，v = A − C——
    /// 旋转矩阵在推导中消去（R·k·R⁻¹ = k），与当前旋转角无关。
    /// 2026-09-25（detail-view-fit-visible-area）：C 不再等于宿主中心——可见区适配盒左右/上下内缩
    /// 不对称（左让左栏、上让信息横条、右让右栏、下 0），C 必须按适配槽位实算（见
    /// <see cref="GetViewerImageBoxCenter"/>），否则滚轮缩放锚点会整体偏移。
    /// </summary>
    private void ZoomAt(Windows.Foundation.Point anchor, double targetScale)
    {
        var center = GetViewerImageBoxCenter();
        if (App.ZoomDiagnosticsEnabled)
        {
            var tl = ViewerImage.TransformToVisual(ImageHost).TransformPoint(new Windows.Foundation.Point(0, 0));
            App.WriteDiagnosticLog(
                $"[zoomprobe] anchor=({anchor.X:F1},{anchor.Y:F1}) C=({center.X:F1},{center.Y:F1})" +
                $" actualTL=({tl.X:F1},{tl.Y:F1}) box={ViewerImage.ActualWidth:F1}x{ViewerImage.ActualHeight:F1}" +
                $" margin=({ViewerImage.Margin.Left:F1},{ViewerImage.Margin.Top:F1})" +
                $" host={ImageHost.ActualWidth:F1}x{ImageHost.ActualHeight:F1}" +
                $" s={ViewerTransform.ScaleX:F3} t=({ViewerTransform.TranslateX:F1},{ViewerTransform.TranslateY:F1})");
        }
        var vx = anchor.X - center.X;
        var vy = anchor.Y - center.Y;
        var ratio = targetScale / ViewerTransform.ScaleX;
        ViewerTransform.TranslateX = vx - ratio * (vx - ViewerTransform.TranslateX);
        ViewerTransform.TranslateY = vy - ratio * (vy - ViewerTransform.TranslateY);
        ViewerTransform.ScaleX = targetScale;
        ViewerTransform.ScaleY = targetScale;
    }

    /// <summary>
    /// <see cref="ViewerImage"/> 变换原点（= 位图内容盒中心 = 适配槽位中心）在 ImageHost 坐标系中的位置，
    /// 即 ZoomAt 推导中的 C。
    /// 2026-09-26（放大锚点修正）：WinUI 的 Image 在 Stretch=Uniform 下**不**把元素盒撑满槽位——
    /// 实测（ZoomAt 内埋点 + 由内容反解不动点）元素盒 = 位图 contain 后的内容盒、且在槽位内居中，
    /// <c>ActualWidth/Height</c> 即该内容盒尺寸。所以「元素左上 = Padding + Margin」只在内容盒
    /// 恰好铺满槽位的那一轴成立：横图纵向差 132.7 DIP（用户实报「放大不动点不是鼠标」，
    /// 实测不动点偏离光标 68~300 px）。内容盒在槽位内居中 ⇒ 内容盒中心恒等于槽位中心，故按槽位算。
    /// </summary>
    private Windows.Foundation.Point GetViewerImageBoxCenter()
    {
        var insetLeft = ImageHost.Padding.Left + ViewerImage.Margin.Left;
        var insetTop = ImageHost.Padding.Top + ViewerImage.Margin.Top;
        var slotWidth = ImageHost.ActualWidth - insetLeft - ImageHost.Padding.Right - ViewerImage.Margin.Right;
        var slotHeight = ImageHost.ActualHeight - insetTop - ImageHost.Padding.Bottom - ViewerImage.Margin.Bottom;
        if (slotWidth > 0 && slotHeight > 0)
        {
            return new Windows.Foundation.Point(insetLeft + slotWidth / 2, insetTop + slotHeight / 2);
        }

        // 盒尚未布局（ActualWidth/Height 为 0，切图首帧）时退回宿主中心，与旧口径一致。
        return new Windows.Foundation.Point(ImageHost.ActualWidth / 2, ImageHost.ActualHeight / 2);
    }

    private void ResetZoom()
    {
        ViewerTransform.ScaleX = 1;
        ViewerTransform.ScaleY = 1;
        ViewerTransform.TranslateX = 0;
        ViewerTransform.TranslateY = 0;
    }

    /// <summary>
    /// 按 VM 属性更新文件名显示（2026-09-19 统一口径；2026-09-26 xaml-finalizer-residuals P1-6
    /// 起直写 Text 不再拼内联）：显示处仅右栏信息区 InfoFileNameText
    ///（底部文件名栏 2026-09-19 用户拍板移除，文件名由右栏承载），显示剥离标签段的显示名
    ///（与瀑布流卡片一致）；标签信息由右栏 chips 与卡片角标承载；完整文件名挂 tooltip。
    /// 布局固定自动换行多行（None/Wrap）。
    /// </summary>
    private void RebuildFileNameInlines()
    {
        // 直写 Text（2026-09-26 xaml-finalizer-residuals P1-6）：原实现每次显示名变化
        // Inlines.Clear() + new Run——打标改名即触发，每次产生一个 Run（XAML 内联对象）裸交 GC。
        // 该 Run 无独立样式（前景/字体继承自 TextBlock），直写 Text 与现呈现等价且零新建。
        InfoFileNameText.TextTrimming = TextTrimming.None;
        InfoFileNameText.TextWrapping = TextWrapping.Wrap;

        // 完整文件名（含标签段）挂 tooltip：悬停可见，不占展示位。
        // WinUI 3 附加属性（FrameworkElement 无 WPF 式 ToolTip 属性）。
        ToolTipService.SetToolTip(
            InfoFileNameText,
            ViewModel.CurrentFileFullName.Length > 0 ? ViewModel.CurrentFileFullName : null);

        InfoFileNameText.Text = ViewModel.CurrentImageDisplayName;

        // 顶部信息横条（走查 5）同步显示名；完整文件名 tooltip 同口径。
        TopStripFileNameText.Text = ViewModel.CurrentImageDisplayName;
        ToolTipService.SetToolTip(
            TopStripFileNameText,
            ViewModel.CurrentFileFullName.Length > 0 ? ViewModel.CurrentFileFullName : null);
    }

    // ==================== 右栏标签管理（2026-09-19 交互重构） ====================

    /// <summary>
    /// 右栏标签 chip 的 ✕ 点击：Tag 槽位回查标签名（DataTemplate 内 x:String 自身）→
    /// MainViewModel.RemoveCurrentImageTagAsync（按名解析所属组 + 单图 toggle 管线移除）。
    /// Click 直达事件不冒泡，不触发卡片/行级处理器。
    /// </summary>
    private void OnRemoveTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tagName })
        {
            _ = ViewModel.RemoveCurrentImageTagAsync(tagName);
        }
    }
}
