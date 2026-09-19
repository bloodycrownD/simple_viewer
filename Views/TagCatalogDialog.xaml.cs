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

/// <summary>目录组行展示模型（组名 + 互斥徽章 + 组内计数 + 标签条目列表；不可变快照）。</summary>
public sealed class TagCatalogGroup
{
    public TagCatalogGroup(string name, bool exclusive, int totalCount, IReadOnlyList<TagCatalogEntry> entries)
    {
        Name = name;
        Exclusive = exclusive;
        TotalCount = totalCount;
        Entries = entries;
    }

    /// <summary>组名。</summary>
    public string Name { get; }

    /// <summary>是否互斥组。</summary>
    public bool Exclusive { get; }

    /// <summary>组内标签计数合计（与侧栏组行同口径：逐标签计数累加）。</summary>
    public int TotalCount { get; }

    /// <summary>组内标签条目。</summary>
    public IReadOnlyList<TagCatalogEntry> Entries { get; }
}

/// <summary>目录标签行展示模型（所属配置组 + 标签名 + 当前图已选态 + 计数 + 组色相；不可变快照）。</summary>
public sealed class TagCatalogEntry
{
    public TagCatalogEntry(TagGroup group, string tagName, bool isApplied, int count)
    {
        Group = group;
        TagName = tagName;
        IsApplied = isApplied;
        Count = count;
        Hue = TagGroupViewModel.HueOfName(group.Name);
    }

    /// <summary>所属配置组（打标语义源：互斥/兼容）。</summary>
    public TagGroup Group { get; }

    /// <summary>标签名。</summary>
    public string TagName { get; }

    /// <summary>当前图是否已含该标签（已选态：✓ 已有 + 禁点）。</summary>
    public bool IsApplied { get; }

    /// <summary>该标签计数（与侧栏行同源 _latestTagCounts）。</summary>
    public int Count { get; }

    /// <summary>所属组色相（单选圆点描边着色，与侧栏一致）。</summary>
    public int Hue { get; }
}

/// <summary>
/// 标签目录选择器（单图详情右栏「＋」打开；两级行列表，点击标签为当前图打标）。
/// 2026-09-19 走查对齐：行样式与左侧栏标签库一致（组头行/树行视觉、右对齐计数、
/// 互斥组单选圆点）；宿主 ContentDialog 经 ApplyDialogTheme 跟随应用主题。
/// </summary>
public sealed partial class TagCatalogDialog : UserControl
{
    private readonly MainViewModel _viewModel;

    public TagCatalogDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        // 快照构建（构造时一次性）：配置组 × 当前图标签集（OrdinalIgnoreCase 判已选——
        // 文件名解析出的标签与配置名大小写可能有差异，语义口径对齐 TagService 按名操作）
        // × 最近标签计数（与侧栏同一数据源，目录与侧栏计数永不分裂）。
        var (groups, currentTags, tagCounts) = viewModel.GetTagCatalogSnapshot();
        var applied = new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase);
        var catalogGroups = new List<TagCatalogGroup>(groups.Count);
        foreach (var group in groups)
        {
            var entries = new List<TagCatalogEntry>(group.Tags.Count);
            var totalCount = 0;
            foreach (var tag in group.Tags)
            {
                var count = tagCounts.TryGetValue(tag.Name, out var value) ? value : 0;
                totalCount += count;
                entries.Add(new TagCatalogEntry(group, tag.Name, applied.Contains(tag.Name), count));
            }

            catalogGroups.Add(new TagCatalogGroup(group.Name, group.Exclusive, totalCount, entries));
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
