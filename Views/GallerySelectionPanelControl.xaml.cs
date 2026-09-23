// 职责：图库右栏（选中集标签面板）code-behind（batch-tag-management Step 4）——宽度常量族锚点、
//       chip ✕ 点击转发（Tag 槽位回查标签名 → MainViewModel.RemoveTagFromSelectionAsync 批量移除管线）
//       与 chip UIA Name 生成（ui/H-2，DataTemplate 内经「页面类名.静态函数」x:Bind 逐项求值）。
// 不变量：ViewModel 构造注入（先赋值后 InitializeComponent，TagFilterPanelControl/TagSidebarControl 惯例）；
//         面板状态全部在 MainViewModel（收展态/并集集合/标题文本），本类无自持业务状态；
//         双 Border 展开/折叠互斥 Visibility 切换无动画（仿单图右栏机制，XAML 绑定驱动）；
//         Click 直达事件不冒泡，不触发卡片/行级处理器。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

/// <summary>
/// 图库右栏（选中集标签面板，batch-tag-management Step 4 / 拍板 D5）：常驻图库右缘的选中集
/// 标签批量管理面板——「已选 N 张」标题行、选中集标签并集 chips（✕ 批量移除）、「＋ 添加标签」
/// （Step 5 接线批量目录）。展开 280 / 折叠 36 双形态。
/// </summary>
public sealed partial class GallerySelectionPanelControl : UserControl
{
    /// <summary>
    /// 展开态宽度（逻辑 px）。常量族锚点：与 <see cref="SingleImageView"/> 的
    /// SidebarExpandedWidth = 280 同值（单图右栏展开宽，SingleImageView.xaml.cs）——
    /// 两处右栏展开宽度属同族锚点，改值时两处同步（XAML 侧 Width 由构造函数应用本常量）。
    /// </summary>
    private const double ExpandedPanelWidth = 280;

    /// <summary>
    /// 折叠窄条宽度（逻辑 px）。常量族锚点：与 SingleImageView 的 SidebarCollapsedWidth = 36
    /// 同值——改值时两处同步。
    /// </summary>
    private const double CollapsedPanelWidth = 36;

    public MainViewModel ViewModel { get; }

    public GallerySelectionPanelControl(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // 宽度常量族应用（值定义集中于本类常量，XAML Border 不写死——SingleImageView 的 XAML
        // 字面 + code-behind 常量双轨曾致改值遗漏，此处收敛为单源）。
        ExpandedPanel.Width = ExpandedPanelWidth;
        CollapsedBar.Width = CollapsedPanelWidth;
    }

    /// <summary>
    /// 并集 chip 的 ✕ 点击：Tag 槽位回查标签名（DataTemplate 内 x:String 自身）→
    /// <see cref="MainViewModel.RemoveTagFromSelectionAsync"/>（选中集内含该标签的文件批量移除，
    /// 选中集保持）。Click 直达事件不冒泡，不触发卡片/行级处理器。
    /// </summary>
    private void OnRemoveChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tagName })
        {
            _ = ViewModel.RemoveTagFromSelectionAsync(tagName);
        }
    }

    /// <summary>
    /// 并集 chip 本体的 UIA Name（ui/H-2）：「标签名 ×计数」。chip 本体 Button 的内容为
    /// StackPanel（UIA Name 为空），多 chip 并存时读屏/自动化无法区分；经
    /// 「页面类名.静态函数」x:Bind 逐项生成（TagCatalogDialog.xaml
    /// AutomationProperties.Name=TagName 先例同族）。× 为 U+00D7 BMP 文本字符。
    /// </summary>
    public static string SelectionChipName(string name, int count) => $"{name} ×{count}";

    /// <summary>
    /// chip 内嵌 ✕ 按钮的 UIA Name（ui/H-2）：「移除标签 {标签名}」——✕ 按钮内容恒为
    /// 字符「✕」，无 Name 时多 chip 的移除目标不可区分；与 <see cref="SelectionChipName"/>
    /// 同款 x:Bind 函数生成。
    /// </summary>
    public static string SelectionChipRemoveName(string name) => $"移除标签 {name}";
}
