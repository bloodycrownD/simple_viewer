// 职责：标签目录选择器 code-behind（2026-09-19 交互重构；batch-tag-management Step 5 批量三态变体 D7）
//       ——构造时取 MainViewModel 快照（配置组 + 当前图标签集 / 批量口径：选中集并集计数 + 选中总数）
//       构建两级行列表；点击标签行 → TagApplied 事件（宿主关闭对话框）
//       + MainViewModel.ApplyCatalogTagAsync（单图 toggle 管线"添加"方向）
//       / ApplyCatalogTagToSelectionAsync（批量口径：选中集路径集统一批量打标管线）。
// 不变量：快照口径——对话框生命周期内不感知配置/标签变化（打标经"同图改名"链路刷新右栏 chips，
//         本对话框点选后即关闭，无需动态刷新）；点击转发经 Tag 槽位回查条目（ItemsControl 无 DataContext）；
//         已选态三态判定（D7）：全部已有 = 禁点（防误触，移除入口在右栏 chip ✕）、
//         部分已有 = 可点（批量口径，再点幂等补齐——互斥组按替换语义）、
//         无 = 可点；单图口径只产生 AppliedToAll/None 两态（与原 IsApplied bool 等价，行为零变化）；
//         对话框期间快捷键屏蔽由宿主（MainWindow._shortcutsEnabled）负责。
// 调用链：单图：SingleImageView 右栏「＋」→ MainViewModel.OpenTagCatalogCommand → ShowTagCatalogAsync（宿主注入）
//         → MainWindow.ShowTagCatalogDialogAsync → ContentDialog → TagCatalogDialog（单图构造）→
//         MainViewModel.ApplyCatalogTagAsync → ToggleTagOnCurrentImageAsync（同图改名管线）。
//         批量（Step 5）：图库右栏「＋ 添加标签」→ OpenSelectionTagCatalogCommand（空选中轻提示 C4）→
//         ShowSelectionTagCatalogAsync（宿主注入）→ MainWindow.ShowSelectionTagCatalogDialogAsync
//         → TagCatalogDialog(ViewModel, batchSelection: true) → ApplyCatalogTagToSelectionAsync
//         → ApplyTagToPathsAsync（统一批量管线，完成后并集重算由 RunTagOperationAsync 收口触发）。

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

/// <summary>
/// 目录标签行已选三态（batch-tag-management Step 5，D7）：目标集 = 单图（当前图）或选中集（批量口径）。
/// 单图口径只产生 <see cref="AppliedToAll"/>/<see cref="None"/> 两态（与原 IsApplied bool 等价）。
/// </summary>
public enum TagCatalogApplyState
{
    /// <summary>目标集无一含该标签：可点。</summary>
    None,

    /// <summary>选中集部分图片已含（仅批量口径）：可点——再点幂等补齐（互斥组按替换语义）。</summary>
    AppliedToSome,

    /// <summary>目标集全部已含：禁点（防误触；移除入口在右栏 chip ✕；单图口径 = 原 IsApplied true）。</summary>
    AppliedToAll,
}

/// <summary>目录标签行展示模型（所属配置组 + 标签名 + 目标集已选三态 + 计数 + 组色相；不可变快照）。</summary>
public sealed class TagCatalogEntry
{
    /// <summary>
    /// 单图口径构造（2026-09-19 原签名保留，单图行为零变化）：bool 已选态映射三态——
    /// true → <see cref="TagCatalogApplyState.AppliedToAll"/>、false → <see cref="TagCatalogApplyState.None"/>
    /// （单图目标集就是当前图一张，不产生部分态）。
    /// </summary>
    public TagCatalogEntry(TagGroup group, string tagName, bool isApplied, int count)
        : this(
            group,
            tagName,
            isBatch: false,
            isApplied ? TagCatalogApplyState.AppliedToAll : TagCatalogApplyState.None,
            count,
            appliedCount: 0,
            selectionCount: 0)
    {
    }

    /// <summary>全量构造（批量口径主入口，batch-tag-management Step 5）。</summary>
    /// <param name="group">所属配置组。</param>
    /// <param name="tagName">标签名（配置侧拼写）。</param>
    /// <param name="isBatch">是否批量口径（后缀文案分支：「✓ 全部已有」/「部分已有 N/M」；单图 = 「✓ 已有」）。</param>
    /// <param name="applyState">目标集已选三态。</param>
    /// <param name="count">全库计数（与侧栏行同源，展示用）。</param>
    /// <param name="appliedCount">选中集内含该标签的图片数（Some 后缀的 N；单图口径恒 0）。</param>
    /// <param name="selectionCount">选中集总数（Some 后缀的 M；单图口径恒 0）。</param>
    public TagCatalogEntry(
        TagGroup group,
        string tagName,
        bool isBatch,
        TagCatalogApplyState applyState,
        int count,
        int appliedCount,
        int selectionCount)
    {
        Group = group;
        TagName = tagName;
        IsBatch = isBatch;
        ApplyState = applyState;
        Count = count;
        AppliedCount = appliedCount;
        SelectionCount = selectionCount;
        Hue = TagGroupViewModel.HueOfName(group.Name);
    }

    /// <summary>所属配置组（打标语义源：互斥/兼容）。</summary>
    public TagGroup Group { get; }

    /// <summary>标签名。</summary>
    public string TagName { get; }

    /// <summary>是否批量口径（图库右栏「＋」打开的目录变体）。</summary>
    public bool IsBatch { get; }

    /// <summary>目标集已选三态（D7）：All = 禁点；Some = 可点补齐；None = 可点。</summary>
    public TagCatalogApplyState ApplyState { get; }

    /// <summary>该标签计数（与侧栏行同源 _latestTagCounts）。</summary>
    public int Count { get; }

    /// <summary>选中集内含该标签的图片数（「部分已有 N/M」的 N；单图口径恒 0）。</summary>
    public int AppliedCount { get; }

    /// <summary>选中集总数（「部分已有 N/M」的 M；单图口径恒 0）。</summary>
    public int SelectionCount { get; }

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

    /// <summary>是否批量口径（图库右栏「＋」打开）：点击转发 ApplyCatalogTagToSelectionAsync；单图转发 ApplyCatalogTagAsync（原行为）。</summary>
    private readonly bool _isBatch;

    public TagCatalogDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DescriptionText = "点击标签为当前图片打标（互斥组会替换组内旧标签）；已含标签的行不可点，移除请用右栏 chip 的 ✕。";

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

    /// <summary>
    /// 批量口径构造（batch-tag-management Step 5，D7；独立构造函数降低与单图口径耦合——spec R3）：
    /// 快照 = 配置组 × 选中集并集计数（<see cref="MainViewModel.SelectionTagUnion"/> 构造时枚举拷贝，
    /// live 集合后续变化不影响本对话框——快照口径与单图一致）× 选中总数。三态判定：
    /// 选中集内计数 0 → None；≥ 选中总数 → AppliedToAll（禁点）；否则 AppliedToSome（可点补齐）。
    /// 并集拼写为首见拼写（文件名侧，D12），与配置标签名按 OrdinalIgnoreCase 匹配（比较器吸收大小写差异）。
    /// 选中总数为 0 时全部条目落 None（防御：宿主回调被绕过直接构造的中间态）。
    /// </summary>
    public TagCatalogDialog(MainViewModel viewModel, bool batchSelection)
    {
        _viewModel = viewModel;
        _isBatch = batchSelection;
        var selectionTotal = viewModel.SelectedCardCount;
        DescriptionText =
            $"点击标签为选中的 {selectionTotal} 张图片批量打标（互斥组会替换组内旧标签）；"
            + "全部图片已含的行不可点，部分已含可点选补齐，移除请用右栏 chip 的 ✕。";

        var (groups, _, tagCounts) = viewModel.GetTagCatalogSnapshot();
        var selectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in viewModel.SelectionTagUnion)
        {
            selectionCounts[entry.Name] = entry.Count;
        }

        var catalogGroups = new List<TagCatalogGroup>(groups.Count);
        foreach (var group in groups)
        {
            var entries = new List<TagCatalogEntry>(group.Tags.Count);
            var totalCount = 0;
            foreach (var tag in group.Tags)
            {
                var count = tagCounts.TryGetValue(tag.Name, out var value) ? value : 0;
                totalCount += count;
                var applied = selectionCounts.GetValueOrDefault(tag.Name);
                var state = applied <= 0 ? TagCatalogApplyState.None
                    : applied >= selectionTotal ? TagCatalogApplyState.AppliedToAll
                    : TagCatalogApplyState.AppliedToSome;
                entries.Add(new TagCatalogEntry(
                    group, tag.Name, isBatch: true, state, count, applied, selectionTotal));
            }

            catalogGroups.Add(new TagCatalogGroup(group.Name, group.Exclusive, totalCount, entries));
        }

        Groups = catalogGroups;

        InitializeComponent();
    }

    /// <summary>目录组行列表（绑定源）。</summary>
    public IReadOnlyList<TagCatalogGroup> Groups { get; }

    /// <summary>顶部说明文案（单图/批量构造各自定稿，XAML 绑定；单图文案 = 2026-09-19 原文逐字迁移）。</summary>
    public string DescriptionText { get; }

    /// <summary>已选标签行点击后请求关闭对话框（宿主 ContentDialog.Hide）。</summary>
    public event Action? TagApplied;

    /// <summary>三态可点判定（x:Bind 函数绑定）：仅「全部已有」禁点；部分已有可点（补齐）；无 → 可点。</summary>
    public static bool Not(TagCatalogApplyState value) => value != TagCatalogApplyState.AppliedToAll;

    /// <summary>
    /// 标签行点击：Tag 槽位回查条目 → 先通知宿主关闭对话框（打标是异步管线，关闭不等它）
    /// → 单图口径走 MainViewModel.ApplyCatalogTagAsync（toggle 管线"添加"方向，右栏 chips 经
    /// "同图改名"通知链刷新）；批量口径走 ApplyCatalogTagToSelectionAsync（选中集路径集统一
    /// 批量管线，完成后并集重算由 RunTagOperationAsync 收口统一触发——D6，无需额外挂点）。
    /// </summary>
    private void OnCatalogTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TagCatalogEntry entry }
            || entry.ApplyState == TagCatalogApplyState.AppliedToAll)
        {
            return; // 全部已有行禁点兜底（IsEnabled 已绑定，双保险；单图口径 = 原 IsApplied 判断，行为等价）。
        }

        TagApplied?.Invoke();
        _ = _isBatch
            ? _viewModel.ApplyCatalogTagToSelectionAsync(entry.Group, entry.TagName)
            : _viewModel.ApplyCatalogTagAsync(entry.Group, entry.TagName);
    }
}
