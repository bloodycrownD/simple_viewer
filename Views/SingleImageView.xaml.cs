// 职责：单图视图 code-behind——依据 MainViewModel 的文件名三段属性组装 Inlines（标签段高亮）。
// 不变量：ViewModel 构造注入（先赋值后 InitializeComponent，沿用 SettingsPage 惯例）；
//         仅响应三段属性变更重建 Inlines，其余绑定走 XAML x:Bind。
// 调用链：MainWindow（ContentControl 宿主）→ SingleImageView → MainViewModel 文件名三段属性。

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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

    public MainViewModel ViewModel { get; }

    public SingleImageView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildFileNameInlines();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.FileNamePrefix)
            or nameof(MainViewModel.FileNameTagSegment)
            or nameof(MainViewModel.FileNameSuffix))
        {
            RebuildFileNameInlines();
        }
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
