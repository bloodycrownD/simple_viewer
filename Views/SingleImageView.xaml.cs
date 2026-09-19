// 职责：单图视图 code-behind——文件名 Inlines 组装（标签段高亮；底部文件名栏 + 右栏信息区两处同步）、
//       滚轮缩放/拖拽平移交互、右栏标签管理转发（chip ✕ 移除 → MainViewModel 单图 toggle 管线）。
// 不变量：ViewModel 构造注入（先赋值后 InitializeComponent，沿用 SettingsPage 惯例）；
//         仅响应三段属性变更重建 Inlines，其余绑定走 XAML x:Bind；
//         右栏（280 展开/36 折叠）在 UserControl 内部——图库模式随宿主 SingleVisibility 整体隐藏，
//         右栏收展改变 ImageHost 显示区自动触发重解码（尺寸源即 ImageHost.SizeChanged）；
//         缩放/平移是纯视图交互态（不入 VM）：切图（ImageSource 变化）与双击复位；
//         CompositeTransform 应用顺序 Scale→Rotate→Translate——平移在最外层，
//         拖拽按屏幕 delta 直接累加；光标锚点缩放需按旋转角变换指针向量。

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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

    /// <summary>缩放下限（相对 fit 尺寸）。</summary>
    private const double MinZoom = 0.1;

    /// <summary>缩放上限（相对 fit 尺寸；放大超过解码分辨率会像素化，属已知限制）。</summary>
    private const double MaxZoom = 8.0;

    /// <summary>
    /// 全分辨率重解码触发阈值（2026-09-19）：解码尺寸 ≈ fit 尺寸，缩放超过此值即放大了
    /// 解码像素，通知 VM 按需加载全分辨率版本（每图一次，失败不重试）。
    /// </summary>
    private const double FullResZoomThreshold = 1.2;

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

        // 视口尺寸源（2026-09-19 修复二次缩放锯齿）：解码尺寸贴合实际显示区（含侧栏占位），
        // 显示层 1:1 无重采样；折叠/展开侧栏与窗口 resize 都经 SizeChanged 驱动重解码。
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
        if (e.PropertyName is nameof(MainViewModel.FileNamePrefix)
            or nameof(MainViewModel.FileNameTagSegment)
            or nameof(MainViewModel.FileNameSuffix)
            or nameof(MainViewModel.CurrentImageDisplayName)
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
    /// </summary>
    private void ZoomAt(Windows.Foundation.Point anchor, double targetScale)
    {
        var vx = anchor.X - ImageHost.ActualWidth / 2;
        var vy = anchor.Y - ImageHost.ActualHeight / 2;
        var ratio = targetScale / ViewerTransform.ScaleX;
        ViewerTransform.TranslateX = vx - ratio * (vx - ViewerTransform.TranslateX);
        ViewerTransform.TranslateY = vy - ratio * (vy - ViewerTransform.TranslateY);
        ViewerTransform.ScaleX = targetScale;
        ViewerTransform.ScaleY = targetScale;
        UpdateZoomLayering();
    }

    private void ResetZoom()
    {
        ViewerTransform.ScaleX = 1;
        ViewerTransform.ScaleY = 1;
        ViewerTransform.TranslateX = 0;
        ViewerTransform.TranslateY = 0;
        UpdateZoomLayering();
    }

    /// <summary>
    /// 放大置顶（2026-09-19 层级走查）：scale &gt; 1（浮点容差）时把图片区 ZIndex 提到最高，
    /// 放大图片溢出显示区时遮盖右栏（同父兄弟，Grid 默认不裁剪溢出）；并写
    /// <see cref="MainViewModel.IsCurrentImageZoomed"/> 让宿主把内容区整体提到工具栏/
    /// 状态栏之上（遮盖左栏与上下 chrome）。复位/切图/回图库时还原（侧栏恢复可交互）。
    /// </summary>
    private void UpdateZoomLayering()
    {
        var zoomed = ViewerTransform.ScaleX > 1.05 || ViewerTransform.ScaleY > 1.05;
        Canvas.SetZIndex(ImageArea, zoomed ? 100 : 0);
        ViewModel.IsCurrentImageZoomed = zoomed;
    }

    /// <summary>
    /// 按 VM 属性重建文件名显示（2026-09-19 统一口径）：两处显示（底部文件名栏 FileNameText
    /// 与右栏信息区 InfoFileNameText）一律显示剥离标签段的显示名（与瀑布流卡片一致），
    /// 标签信息由右栏 chips 与卡片角标承载；完整文件名挂 tooltip（CurrentFileFullName）。
    /// </summary>
    private void RebuildFileNameInlines()
    {
        RebuildInlinesInto(FileNameText, trim: true);
        RebuildInlinesInto(InfoFileNameText, trim: false);
    }

    /// <summary>向目标 TextBlock 重建显示名单段（trim = 裁剪省略号单行；false = 自动换行多行）。</summary>
    private void RebuildInlinesInto(TextBlock target, bool trim)
    {
        target.Inlines.Clear();
        target.TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        target.TextWrapping = trim ? TextWrapping.NoWrap : TextWrapping.Wrap;

        // 完整文件名（含标签段）挂 tooltip：悬停可见，不占展示位。
        // WinUI 3 附加属性（FrameworkElement 无 WPF 式 ToolTip 属性）。
        ToolTipService.SetToolTip(
            target,
            ViewModel.CurrentFileFullName.Length > 0 ? ViewModel.CurrentFileFullName : null);

        if (!string.IsNullOrEmpty(ViewModel.CurrentImageDisplayName))
        {
            target.Inlines.Add(new Run { Text = ViewModel.CurrentImageDisplayName });
        }
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
