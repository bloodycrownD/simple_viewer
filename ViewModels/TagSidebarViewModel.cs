// 职责：左侧标签栏视图模型（spec Step 9/10）——配置组 + 固定末位「未分组」虚拟组的展示模型、
//       chip 交互分流（选中集非空/单图模式点击 = 打标，Shift = 移除；否则 = 切换筛选）、编辑请求上抛。
// 不变量：组/chip 为不可变快照对象——任何变化（计数刷新/筛选切换/配置编辑）经 Rebuild 全量重建
//         （侧栏规模为几十个 chip，重建开销可忽略，换取免 INPC 的简单性）；
//         「未分组」为索引 TagCounts 中不属于任何配置组的标签聚合（不可配置互斥属性、非互斥、可筛选）；
//         chip 点击经 HandleChipTappedAsync 分流（Step 10 语义，对齐 demo onChipClick）：
//         单图模式 → 当前图打标；选中集非空 → 批量打标/Shift 移除；其余 → 切换筛选；
//         所有编辑操作经 TagEditRequest 上抛给宿主对话框（MainWindow 注入 ShowTagEditorAsync），
//         落盘/索引/配置持久化统一在 MainViewModel.ExecuteTagEditAsync。
// 调用链：MainViewModel.RefreshTagDataAsync → TagSidebarViewModel.Rebuild → TagSidebarControl（绑定）；
//         chip 点击 → TagSidebarControl.OnChipTapped → HandleChipTappedAsync → MainViewModel.HandleTagChipTappedAsync；
//         chip/组命令 → TagEditRequest → MainWindow.ShowTagEditorAsync → TagEditDialog → MainViewModel.ExecuteTagEditAsync。

using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleViewer.Models;

namespace SimpleViewer.ViewModels;

/// <summary>标签/组编辑操作种类（TagEditDialog 据此装配 UI）。</summary>
public enum TagEditKind
{
    /// <summary>新建标签组。</summary>
    AddGroup,

    /// <summary>在指定组中新建标签。</summary>
    AddTag,

    /// <summary>重命名标签组（组名不影响文件名）。</summary>
    RenameGroup,

    /// <summary>重命名标签（更新引用它的图片文件名，打标即改名）。</summary>
    RenameTag,

    /// <summary>删除标签（从引用它的图片文件名移除，不删图片本体）。</summary>
    DeleteTag,

    /// <summary>删除标签组（级联移除组内全部标签）。</summary>
    DeleteGroup,

    /// <summary>组「互斥 ⇄ 多选」切换（仅改 TagGroups 配置，不改已落盘标签）。</summary>
    ToggleExclusive,
}

/// <summary>一次标签/组编辑的上下文（由侧栏构造，交 TagEditDialog 展示并收集输入）。</summary>
public sealed class TagEditRequest
{
    /// <summary>操作种类。</summary>
    public TagEditKind Kind { get; init; }

    /// <summary>目标组 Id（未分组虚拟组的相关操作为 null）。</summary>
    public string? GroupId { get; init; }

    /// <summary>目标组名（展示用）。</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>目标组当前互斥属性（ToggleExclusive 的展示基准）。</summary>
    public bool GroupExclusive { get; init; }

    /// <summary>目标标签名（重命名/删除标签）。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>影响张数（重命名/删除标签、删除组；来自最近一次标签计数快照）。</summary>
    public int AffectedCount { get; init; }
}

/// <summary>TagEditDialog 收集的用户输入。</summary>
public sealed class TagEditInput
{
    /// <summary>名称输入（组名/标签名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>互斥复选（新建组）。</summary>
    public bool Exclusive { get; init; }
}

/// <summary>
/// 标签栏视图模型（spec Step 9）。
/// </summary>
public partial class TagSidebarViewModel : ObservableObject
{
    /// <summary>未分组虚拟组的固定 Id（不存在于配置 TagGroups 中）。</summary>
    public const string UngroupedGroupId = "__ungrouped__";

    /// <summary>未分组虚拟组的显示名（固定末位）。</summary>
    public const string UngroupedGroupName = "未分组";

    private readonly MainViewModel _owner;

    public TagSidebarViewModel(MainViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>宿主注入的编辑对话框打开回调（MainWindow 构造时注入，含 _shortcutsEnabled 屏蔽）。</summary>
    public Func<TagEditRequest, Task>? ShowTagEditorAsync { get; set; }

    /// <summary>展示的组序列（配置组顺序 + 未分组虚拟组末位）。</summary>
    public ObservableCollection<TagGroupViewModel> Groups { get; } = [];

    /// <summary>是否无任何组/标签（空态引导）。</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>底部「新建标签组」固定入口。</summary>
    [RelayCommand]
    private void AddGroup() => RaiseEdit(new TagEditRequest { Kind = TagEditKind.AddGroup });

    /// <summary>
    /// 全量重建组/chip（UI 线程）：配置组 + 未分组虚拟组（索引计数中不属于任何配置组的标签）。
    /// </summary>
    /// <param name="configGroups">配置组（SettingsService.Load().TagGroups）。</param>
    /// <param name="tagCounts">最近一次索引标签计数快照。</param>
    /// <param name="activeFilters">当前激活的筛选标签集（chip 高亮）。</param>
        public void Rebuild(
            IReadOnlyList<TagGroup> configGroups,
            IReadOnlyDictionary<string, int> tagCounts,
            IReadOnlyCollection<string> activeFilters)
    {
        // 视觉对齐 demo：同步「标签名 → 组色相」索引，供瀑布流角标着色（纯展示数据，不落盘）。
        GalleryItemViewModel.UpdateTagHues(configGroups);

        Groups.Clear();
        var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in configGroups)
        {
            var chips = new List<TagChipViewModel>(group.Tags.Count);
            var totalCount = 0;
            foreach (var tag in group.Tags)
            {
                if (!configuredNames.Add(tag.Name))
                {
                    continue; // 配置层已被校验拒绝跨组重名，此处仅防御。
                }

                var count = tagCounts.TryGetValue(tag.Name, out var value) ? value : 0;
                totalCount += count;
                chips.Add(new TagChipViewModel(
                    tag.Name,
                    count,
                    activeFilters.Contains(tag.Name),
                    group.Exclusive,
                    group,
                    CreateChipEditCommands(group, tag.Name)));
            }

            Groups.Add(new TagGroupViewModel(
                group.Id,
                group.Name,
                group.Exclusive,
                isUngrouped: false,
                totalCount,
                chips,
                CreateGroupCommands(group)));
        }

        // 未分组虚拟组：索引计数中不属于任何配置组的标签（可筛选、不可配置互斥属性）。
        var ungrouped = new List<TagChipViewModel>();
        var ungroupedTotal = 0;
        foreach (var pair in tagCounts.OrderBy(p => p.Key, StringComparer.CurrentCulture))
        {
            if (configuredNames.Contains(pair.Key))
            {
                continue;
            }

                                ungroupedTotal += pair.Value;
                                ungrouped.Add(new TagChipViewModel(
                                    pair.Key,
                                    pair.Value,
                                    activeFilters.Contains(pair.Key),
                                    showRadioDot: false,
                                    ownerGroup: null,
                                    CreateUngroupedChipEditCommands(pair.Key)));
        }

        if (ungrouped.Count > 0)
        {
            Groups.Add(new TagGroupViewModel(
                UngroupedGroupId,
                UngroupedGroupName,
                exclusive: false,
                isUngrouped: true,
                ungroupedTotal,
                ungrouped,
                GroupCommands.NoCommands));
        }

        IsEmpty = Groups.Count == 0;
    }

    /// <summary>
    /// chip 点击分流入口（TagSidebarControl.OnChipTapped 转发，携带 Shift 键状态；Step 10）：
    /// 分流规则在 MainViewModel.HandleTagChipTappedAsync（单图打标/选中集批量/筛选切换）。
    /// </summary>
    public Task HandleChipTappedAsync(TagChipViewModel chip, bool shift)
        => _owner.HandleTagChipTappedAsync(chip.OwnerGroup, chip.Name, shift);

    /// <summary>配置组内标签的编辑命令集（重命名/删除）。</summary>
    private TagChipCommands CreateChipEditCommands(TagGroup group, string tagName)
        => new(
            Rename: new RelayCommand(() => RaiseEdit(BuildTagRequest(
                TagEditKind.RenameTag, group, tagName))),
            Delete: new RelayCommand(() => RaiseEdit(BuildTagRequest(
                TagEditKind.DeleteTag, group, tagName))));

    /// <summary>
    /// 未分组标签的编辑命令（2026-09-17 走查修复：存量标签同样需要重命名/删除入口——
    /// TagService 按名操作文件，与配置无关；Execute*Async 对 GroupId=null 走"仅动文件、不动配置"分支）。
    /// </summary>
    private TagChipCommands CreateUngroupedChipEditCommands(string tagName) => new(
        Rename: new RelayCommand(() => RaiseEdit(BuildUngroupedTagRequest(TagEditKind.RenameTag, tagName))),
        Delete: new RelayCommand(() => RaiseEdit(BuildUngroupedTagRequest(TagEditKind.DeleteTag, tagName))));

    private TagEditRequest BuildUngroupedTagRequest(TagEditKind kind, string tagName) => new()
    {
        Kind = kind,
        GroupId = null,
        GroupName = UngroupedGroupName,
        TagName = tagName,
        AffectedCount = _owner.GetTagCount(tagName),
    };

    private GroupCommands CreateGroupCommands(TagGroup group)
        => new(
            AddTag: new RelayCommand(() => RaiseEdit(new TagEditRequest
            {
                Kind = TagEditKind.AddTag,
                GroupId = group.Id,
                GroupName = group.Name,
                GroupExclusive = group.Exclusive,
            })),
            RenameGroup: new RelayCommand(() => RaiseEdit(new TagEditRequest
            {
                Kind = TagEditKind.RenameGroup,
                GroupId = group.Id,
                GroupName = group.Name,
                GroupExclusive = group.Exclusive,
            })),
            DeleteGroup: new RelayCommand(() => RaiseEdit(new TagEditRequest
            {
                Kind = TagEditKind.DeleteGroup,
                GroupId = group.Id,
                GroupName = group.Name,
                GroupExclusive = group.Exclusive,
                AffectedCount = CountGroupReferences(group),
            })),
            ToggleExclusive: new RelayCommand(() => RaiseEdit(new TagEditRequest
            {
                Kind = TagEditKind.ToggleExclusive,
                GroupId = group.Id,
                GroupName = group.Name,
                GroupExclusive = group.Exclusive,
            })));

    private TagEditRequest BuildTagRequest(TagEditKind kind, TagGroup group, string tagName)
        => new()
        {
            Kind = kind,
            GroupId = group.Id,
            GroupName = group.Name,
            GroupExclusive = group.Exclusive,
            TagName = tagName,
            AffectedCount = _owner.GetTagCount(tagName),
        };

    private int CountGroupReferences(TagGroup group)
    {
        var total = 0;
        foreach (var tag in group.Tags)
        {
            total += _owner.GetTagCount(tag.Name);
        }

        return total;
    }

    private void RaiseEdit(TagEditRequest request)
    {
        if (ShowTagEditorAsync is not null)
        {
            _ = ShowTagEditorAsync(request);
        }
    }
}

/// <summary>组命令集（侧栏组头操作）。</summary>
/// <param name="AddTag">在该组中新建标签。</param>
/// <param name="RenameGroup">重命名组。</param>
/// <param name="DeleteGroup">删除组（含影响张数确认）。</param>
/// <param name="ToggleExclusive">互斥 ⇄ 多选切换。</param>
public sealed record GroupCommands(
    ICommand? AddTag,
    ICommand? RenameGroup,
    ICommand? DeleteGroup,
    ICommand? ToggleExclusive)
{
    /// <summary>无命令（未分组虚拟组：不可配置）。</summary>
    public static GroupCommands NoCommands { get; } = new(null, null, null, null);
}

/// <summary>标签 chip 命令集（编辑操作；点击主行为经 HandleChipTappedAsync 分流，见 Step 10）。</summary>
/// <param name="Rename">重命名标签（未分组标签为 null）。</param>
/// <param name="Delete">删除标签（未分组标签为 null）。</param>
public sealed record TagChipCommands(ICommand? Rename, ICommand? Delete);

/// <summary>标签组展示模型（不可变快照，经 Rebuild 全量重建）。</summary>
public sealed class TagGroupViewModel
{
    public TagGroupViewModel(
        string id,
        string name,
        bool exclusive,
        bool isUngrouped,
        int totalCount,
        IReadOnlyList<TagChipViewModel> tags,
        GroupCommands commands)
    {
        Id = id;
        Name = name;
        Exclusive = exclusive;
        IsUngrouped = isUngrouped;
        // 组色相由组名哈希（对齐 demo hueOf；未分组固定灰蓝低饱和），纯展示字段不持久化。
        Hue = isUngrouped ? UngroupedHue : HueOfName(name);
        TotalCount = totalCount;
        Tags = tags;
        Commands = commands;
    }

    /// <summary>未分组虚拟组的固定色相（灰蓝 220，配低饱和使用）。</summary>
    public const int UngroupedHue = 220;

    /// <summary>组名 → 色相（0-359）：与 demo.js hueOf 相同的多项式 31 哈希（按 UTF-16 码元逐项累加）。</summary>
    public static int HueOfName(string name)
    {
        var h = 0;
        foreach (var c in name)
        {
            h = (h * 31 + c) % 360;
        }

        return h;
    }

    /// <summary>组 Id（未分组为 <see cref="TagSidebarViewModel.UngroupedGroupId"/>）。</summary>
    public string Id { get; }

    /// <summary>组名。</summary>
    public string Name { get; }

    /// <summary>是否互斥组（chip 单选圆点样式依据）。</summary>
    public bool Exclusive { get; }

    /// <summary>是否「未分组」虚拟组（隐藏组管理按钮）。</summary>
    public bool IsUngrouped { get; }

    /// <summary>组色相（chip 边框/底色由 x:Bind 转换器转为 HSL 画刷；纯展示，不持久化）。</summary>
    public int Hue { get; }

    /// <summary>组内标签计数之和（张数引用合计）。</summary>
    public int TotalCount { get; }

    /// <summary>组内标签 chip。</summary>
    public IReadOnlyList<TagChipViewModel> Tags { get; }

    /// <summary>组级命令。</summary>
    public GroupCommands Commands { get; }
}

/// <summary>标签 chip 展示模型（不可变快照）。</summary>
public sealed class TagChipViewModel
{
    public TagChipViewModel(
        string name,
        int count,
        bool isFilterActive,
        bool showRadioDot,
        TagGroup? ownerGroup,
        TagChipCommands commands)
    {
        Name = name;
        Count = count;
        IsFilterActive = isFilterActive;
        ShowRadioDot = showRadioDot;
        OwnerGroup = ownerGroup;
        // chip 色相跟随所属组（未分组灰蓝 220），纯展示字段不持久化。
        Hue = ownerGroup is null ? TagGroupViewModel.UngroupedHue : TagGroupViewModel.HueOfName(ownerGroup.Name);
        Commands = commands;
    }

    /// <summary>标签名。</summary>
    public string Name { get; }

    /// <summary>该标签的图片计数（实时刷新）。</summary>
    public int Count { get; }

    /// <summary>筛选是否激活（chip 高亮反馈；筛选条 UI 属 Step 11，此为最小可见反馈）。</summary>
    public bool IsFilterActive { get; }

    /// <summary>是否显示互斥组单选圆点。</summary>
    public bool ShowRadioDot { get; }

    /// <summary>chip 色相（胶囊边框/底色；纯展示，不持久化）。</summary>
    public int Hue { get; }

    /// <summary>所属配置组（未分组虚拟组为 null——打标按非互斥叠加语义）。</summary>
    public TagGroup? OwnerGroup { get; }

    /// <summary>chip 命令集。</summary>
    public TagChipCommands Commands { get; }
}
