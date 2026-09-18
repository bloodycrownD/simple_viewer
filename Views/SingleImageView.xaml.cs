// 职责：单图视图 code-behind——文件名 Inlines 组装（标签段高亮）+ 滚轮缩放/拖拽平移交互。
// 不变量：ViewModel 构造注入（先赋值后 InitializeComponent，沿用 SettingsPage 惯例）；
//         仅响应三段属性变更重建 Inlines，其余绑定走 XAML x:Bind；
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
/// 单图查看视图（D14：自 MainWindow Row1 抽出的 UserControl，含完整文件名与标签段高亮）。
/// </summary>
public sealed partial class SingleImageView : UserControl
{
    /// <summary>标签段高亮用的主题资源键（强调色文字画刷）。</summary>
    private const string TagHighlightBrushKey = "AccentTextFillColorPrimaryBrush";

    /// <summary>滚轮每格缩放倍率（前滚放大；1.15^±1 每格）。</summary>
    private const double ZoomStepFactor = 1.15;

    /// <summary>缩放下限（相对 fit 尺寸）。</summary>
    private const double MinZoom = 0.1;

    /// <summary>缩放上限（相对 fit 尺寸；放大超过解码分辨率会像素化，属已知限制）。</summary>
    private const double MaxZoom = 8.0;

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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.FileNamePrefix)
            or nameof(MainViewModel.FileNameTagSegment)
            or nameof(MainViewModel.FileNameSuffix))
        {
            RebuildFileNameInlines();
        }
        else if (e.PropertyName == nameof(MainViewModel.ImageSource))
        {
            // 切图复位缩放/平移（缩放属上一张的交互态，不跨图保留）。
            ResetZoom();
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
    }

    private void ResetZoom()
    {
        ViewerTransform.ScaleX = 1;
        ViewerTransform.ScaleY = 1;
        ViewerTransform.TranslateX = 0;
        ViewerTransform.TranslateY = 0;
    }

    /// <summary>按 VM 三段属性重建文件名 Inlines：标签段以强调色 + 半粗字重高亮。</summary>
    private void RebuildFileNameInlines()
    {
        FileNameText.Inlines.Clear();

        if (!string.IsNullOrEmpty(ViewModel.FileNamePrefix))
        {
            FileNameText.Inlines.Add(new Run { Text = ViewModel.FileNamePrefix });
        }

        if (!string.IsNullOrEmpty(ViewModel.FileNameTagSegment))
        {
            FileNameText.Inlines.Add(new Run
            {
                Text = ViewModel.FileNameTagSegment,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResolveTagHighlightBrush(),
            });
        }

        if (!string.IsNullOrEmpty(ViewModel.FileNameSuffix))
        {
            FileNameText.Inlines.Add(new Run { Text = ViewModel.FileNameSuffix });
        }
    }

    private static Brush? ResolveTagHighlightBrush()
    {
        if (Application.Current?.Resources.TryGetValue(TagHighlightBrushKey, out var value) == true
            && value is Brush brush)
        {
            return brush;
        }

        return null;
    }
}
