// 职责：标签目录选择器 code-behind（2026-09-19 交互重构）——构造时取 MainViewModel 快照
//       （配置组 + 当前图标签集）构建两级行列表；点击标签行 → TagApplied 事件（宿主关闭对话框）
//       + MainViewModel.ApplyCatalogTagAsync（单图 toggle 管线"添加"方向）。
// 不变量：快照口径——对话框生命周期内不感知配置/标签变化（打标经"同图改名"链路刷新右栏 chips，
//         本对话框点选后即关闭，无需动态刷新）；点击转发经 Tag 槽位回查条目（ItemsControl 无 DataContext）；
//         已含标签的行禁点（IsEnabled 经 x:Bind 函数取反绑定）——防误触移除，移除入口在右栏 chip ✕；
//         对话框期间快捷键屏蔽由宿主（MainWindow._shortcutsEnabled）负责。
// 调用链：SingleImageView 右栏「＋」→ MainViewModel.OpenTagCatalogCommand → ShowTagCatalogAsync（宿主注入）
//         → MainWindow.ShowTagCatalogDialogAsync → ContentDialog → TagCatalogDialog →
//         MainViewModel.ApplyCatalogTagAsync → ToggleTagOnCurrentImageAsync（同图改名管线）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleViewer.Models;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

/// <summary>目录组行展示模型（组名 + 互斥徽章 + 标签条目列表；不可变快照）。</summary>
public sealed class TagCatalogGroup
{
    public TagCatalogGroup(string name, bool exclusive, IReadOnlyList<TagCatalogEntry> entries)
    {
        Name = name;
        Exclusive = exclusive;
        Entries = entries;
    }

    /// <summary>组名。</summary>
    public string Name { get; }

    /// <summary>是否互斥组。</summary>
    public bool Exclusive { get; }

    /// <summary>组内标签条目。</summary>
    public IReadOnlyList<TagCatalogEntry> Entries { get; }
}

/// <summary>目录标签行展示模型（所属配置组 + 标签名 + 当前图已选态；不可变快照）。</summary>
public sealed class TagCatalogEntry
{
    public TagCatalogEntry(TagGroup group, string tagName, bool isApplied)
    {
        Group = group;
        TagName = tagName;
        IsApplied = isApplied;
    }

    /// <summary>所属配置组（打标语义源：互斥/兼容）。</summary>
    public TagGroup Group { get; }

    /// <summary>标签名。</summary>
    public string TagName { get; }

    /// <summary>当前图是否已含该标签（已选态：✓ 已有 + 禁点）。</summary>
    public bool IsApplied { get; }
}

/// <summary>
/// 标签目录选择器（单图详情右栏「＋」打开；两级行列表，点击标签为当前图打标）。
/// </summary>
public sealed partial class TagCatalogDialog : UserControl
{
    private readonly MainViewModel _viewModel;

    public TagCatalogDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        // 快照构建（构造时一次性）：配置组 × 当前图标签集（OrdinalIgnoreCase 判已选——
        // 文件名解析出的标签与配置名大小写可能有差异，语义口径对齐 TagService 按名操作）。
        var (groups, currentTags) = viewModel.GetTagCatalogSnapshot();
        var applied = new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase);
        var catalogGroups = new List<TagCatalogGroup>(groups.Count);
        foreach (var group in groups)
        {
            var entries = group.Tags
                .Select(tag => new TagCatalogEntry(group, tag.Name, applied.Contains(tag.Name)))
                .ToList();
            catalogGroups.Add(new TagCatalogGroup(group.Name, group.Exclusive, entries));
        }

        Groups = catalogGroups;

        InitializeComponent();
    }

    /// <summary>目录组行列表（绑定源）。</summary>
    public IReadOnlyList<TagCatalogGroup> Groups { get; }

    /// <summary>已选标签行点击后请求关闭对话框（宿主 ContentDialog.Hide）。</summary>
    public event Action? TagApplied;

    /// <summary>bool 取反（x:Bind 函数绑定：已选行禁点）。</summary>
    public static bool Not(bool value) => !value;

    /// <summary>
    /// 标签行点击：Tag 槽位回查条目 → 先通知宿主关闭对话框（打标是异步管线，关闭不等它）
    /// → MainViewModel.ApplyCatalogTagAsync 走单图 toggle 管线添加标签
    /// （右栏 chips 经"同图改名"通知链刷新，不闪不重载）。
    /// </summary>
    private void OnCatalogTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TagCatalogEntry entry } || entry.IsApplied)
        {
            return; // 已含标签的行禁点兜底（IsEnabled 已绑定，双保险）。
        }

        TagApplied?.Invoke();
        _ = _viewModel.ApplyCatalogTagAsync(entry.Group, entry.TagName);
    }
}
