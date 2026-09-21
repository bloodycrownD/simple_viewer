// 职责：左侧标签栏视图模型（spec Step 9/10）——配置组的展示模型、chip 交互转发
//       （点击 = 切换筛选；2026-09-19 交互重构：打标移交拖拽/详情页右栏/快捷键）、编辑请求上抛。
// 不变量：组/chip 为不可变快照对象——任何变化（计数刷新/筛选切换/配置编辑）经 Rebuild 全量重建
//         （侧栏规模为几十个 chip，重建开销可忽略，换取免 INPC 的简单性）；
//         聚合类 UI 完全忽略无组标签（2026-09-19 用户拍板推翻 4679a12 引入的「未分组」虚拟组
//         与「曾见即留」记忆）：侧栏只遍历配置组——文件名中不属于任何配置组的标签不显示、
//         不可筛选；无组脏数据的清理出口 = 单图右栏 chips 的 ✕（RemoveCurrentImageTagAsync），
//         或在配置组内新建同名标签自然「收编」（按名匹配配置，无需额外代码）；
//         chip 点击经 HandleChipTappedAsync 转发（MainViewModel 切筛选并处理单图→图库回切）；
//         所有编辑操作经 TagEditRequest 上抛给宿主对话框（MainWindow 注入 ShowTagEditorAsync），
//         落盘/索引/配置持久化统一在 MainViewModel.ExecuteTagEditAsync。
// 调用链：MainViewModel.RefreshTagDataAsync → TagSidebarViewModel.Rebuild → TagSidebarControl（绑定）；
//         chip 点击 → TagSidebarControl.OnChipClicked → HandleChipTappedAsync → MainViewModel.HandleTagChipTapped；
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

    /// <summary>组「互斥 ⇄ 兼容」切换（仅改 TagGroups 配置，不改已落盘标签）。</summary>
    ToggleExclusive,
}

/// <summary>一次标签/组编辑的上下文（由侧栏构造，交 TagEditDialog 展示并收集输入）。</summary>
public sealed class TagEditRequest
{
    /// <summary>操作种类。</summary>
    public TagEditKind Kind { get; init; }

    /// <summary>目标组 Id（编辑入口均由侧栏配置组行构造，理论恒非空；null 仅为配置被外部修改的防御，cr/P2-2 注释对齐）。</summary>
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
    private readonly MainViewModel _owner;

    public TagSidebarViewModel(MainViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>宿主注入的编辑对话框打开回调（MainWindow 构造时注入，含 _shortcutsEnabled 屏蔽）。</summary>
    public Func<TagEditRequest, Task>? ShowTagEditorAsync { get; set; }

    /// <summary>
    /// 当前折叠的组 Id 集合（会话内记忆，不落盘）：默认空 = 全部展开（与树形化前的平铺信息可见性一致）；
    /// 新建组不在集合中天然展开。展开状态变化经 ToggleGroupExpansion 全量 Rebuild 生效（免 INPC，
    /// 沿用「不可变快照 + 重建」不变量）；Rebuild 时按现存组 Id 交集清理已删除组的残留项。
    /// </summary>
    private readonly HashSet<string> _collapsedGroupIds = [];

    /// <summary>展示的组序列（仅配置组，按配置顺序；2026-09-19 口径：不再追加「未分组」虚拟组）。</summary>
    public ObservableCollection<TagGroupViewModel> Groups { get; } = [];

    /// <summary>是否无任何组/标签（空态引导）。</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>底部「新建标签组」固定入口。</summary>
    [RelayCommand]
    private void AddGroup() => RaiseEdit(new TagEditRequest { Kind = TagEditKind.AddGroup });

    /// <summary>
    /// 全量重建组/chip（UI 线程）：仅配置组——索引计数中不属于任何配置组的标签被忽略
    /// （2026-09-19 用户拍板：聚合类 UI 完全忽略无组标签，对齐 demo 只遍历配置组）。
    /// </summary>
    /// <param name="configGroups">配置组（SettingsService.Load().TagGroups）。</param>
    /// <param name="tagCounts">最近一次索引标签计数快照。</param>
    /// <param name="activeFilters">当前激活的筛选标签集（chip 高亮）。</param>
    /// <returns>「标签名 → 组色相」索引是否变化（宿主据此对已呈现瀑布流卡片补发 Badges 重通知，
    /// 消除打标后角标底色滞后一轮的问题——见 GalleryItemViewModel.NotifyBadgeHuesChanged）。</returns>
        public bool Rebuild(
            IReadOnlyList<TagGroup> configGroups,
            IReadOnlyDictionary<string, int> tagCounts,
            IReadOnlyCollection<string> activeFilters)
    {
        // 视觉对齐 demo：同步「标签名 → 组色相」索引，供瀑布流角标着色（纯展示数据，不落盘）。
        var tagHuesChanged = GalleryItemViewModel.UpdateTagHues(configGroups);

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
                totalCount,
                chips,
                CreateGroupCommands(group),
                isExpanded: !_collapsedGroupIds.Contains(group.Id)));
        }

        // 清理折叠集合中已删除组的残留项（按现存组 Id 交集，避免集合无界增长）。
        _collapsedGroupIds.IntersectWith(new HashSet<string>(Groups.Select(static g => g.Id)));

        IsEmpty = Groups.Count == 0;
        return tagHuesChanged;
    }

    /// <summary>
    /// 展开/折叠切换（组头整行按钮的点击入口，TagSidebarControl.OnGroupHeaderClicked 转发）：
    /// 切换折叠集合成员资格后经 MainViewModel.RebuildTagSidebar 全量重建侧栏
    /// （新建 VM 对象 + x:Bind OneTime 重求值，无需属性通知）。
    /// </summary>
    public void ToggleGroupExpansion(string groupId)
    {
        if (!_collapsedGroupIds.Remove(groupId))
        {
            _collapsedGroupIds.Add(groupId);
        }

        _owner.RebuildTagSidebar();
    }

    /// <summary>
    /// chip 点击转发入口（TagSidebarControl.OnChipClicked 转发，2026-09-19 交互重构）：
    /// 点击一律 = 筛选（tag-filter-tree：QuickAdd 追加条件；已引用则忽略）；打标走拖拽/详情页右栏/快捷键。
    /// ctrl（旧 Ctrl 加减选语义已随条件树化废弃）：保留参数与 Task 返回签名仅为避免本步改动扩散到
    /// 侧栏控件（TagSidebarControl 的 fire-and-forget 调用），MainViewModel 侧一律忽略。
    /// </summary>
    public Task HandleChipTappedAsync(TagChipViewModel chip, bool ctrl)
    {
        _owner.HandleTagChipTapped(chip.Name, ctrl);
        return Task.CompletedTask;
    }

    /// <summary>配置组内标签的编辑命令集（重命名/删除）——侧栏标签行均属配置组
    /// （2026-09-19 口径：无组标签不经侧栏展示/编辑，清理走右栏 chips ✕ 或配置组同名收编）。</summary>
    private TagChipCommands CreateChipEditCommands(TagGroup group, string tagName)
        => new(
            Rename: new RelayCommand(() => RaiseEdit(BuildTagRequest(
                TagEditKind.RenameTag, group, tagName))),
            Delete: new RelayCommand(() => RaiseEdit(BuildTagRequest(
                TagEditKind.DeleteTag, group, tagName))));

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
/// <param name="ToggleExclusive">互斥 ⇄ 兼容切换。</param>
public sealed record GroupCommands(
    ICommand AddTag,
    ICommand RenameGroup,
    ICommand DeleteGroup,
    ICommand ToggleExclusive);

/// <summary>标签 chip 命令集（编辑操作；点击主行为经 HandleChipTappedAsync 分流，见 Step 10）。</summary>
/// <param name="Rename">重命名标签。</param>
/// <param name="Delete">删除标签。</param>
public sealed record TagChipCommands(ICommand Rename, ICommand Delete);

/// <summary>标签组展示模型（不可变快照，经 Rebuild 全量重建）。</summary>
public sealed class TagGroupViewModel
{
    public TagGroupViewModel(
        string id,
        string name,
        bool exclusive,
        int totalCount,
        IReadOnlyList<TagChipViewModel> tags,
        GroupCommands commands,
        bool isExpanded = true)
    {
        Id = id;
        Name = name;
        Exclusive = exclusive;
        // 组色相由组名哈希（对齐 demo hueOf），纯展示字段不持久化。
        Hue = HueOfName(name);
        TotalCount = totalCount;
        Tags = tags;
        Commands = commands;
        IsExpanded = isExpanded;
    }

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

    /// <summary>组 Id。</summary>
    public string Id { get; }

    /// <summary>组名。</summary>
    public string Name { get; }

    /// <summary>是否互斥组（chip 单选圆点样式依据）。</summary>
    public bool Exclusive { get; }

    /// <summary>组色相（chip 边框/底色由 x:Bind 转换器转为 HSL 画刷；纯展示，不持久化）。</summary>
    public int Hue { get; }

    /// <summary>组内标签计数之和（张数引用合计）。</summary>
    public int TotalCount { get; }

    /// <summary>是否展开（目录树态：false 时组内标签行收起；由折叠集合派生，经 Rebuild 重建生效）。</summary>
    public bool IsExpanded { get; }

    /// <summary>组内标签 chip。</summary>
    public IReadOnlyList<TagChipViewModel> Tags { get; }

    /// <summary>组级命令。</summary>
    public GroupCommands Commands { get; }
}

/// <summary>标签 chip 展示模型（不可变快照；恒属一个配置组——2026-09-19 口径下侧栏不再展示无组标签）。</summary>
public sealed class TagChipViewModel
{
    public TagChipViewModel(
        string name,
        int count,
        bool isFilterActive,
        bool showRadioDot,
        TagGroup ownerGroup,
        TagChipCommands commands)
    {
        Name = name;
        Count = count;
        IsFilterActive = isFilterActive;
        ShowRadioDot = showRadioDot;
        OwnerGroup = ownerGroup;
        // chip 色相跟随所属组，纯展示字段不持久化。
        Hue = TagGroupViewModel.HueOfName(ownerGroup.Name);
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

    /// <summary>所属配置组（拖拽打标消费；非空——无组标签不经侧栏展示）。</summary>
    public TagGroup OwnerGroup { get; }

    /// <summary>chip 命令集。</summary>
    public TagChipCommands Commands { get; }
}
