// 职责：筛选面板视图模型（tag-filter-tree Step 5）——条件树不可变渲染快照构建（组=卡片全层统一、
//       卡片嵌套卡片）、值选择候选快照（配置组 + 标签计数，与侧栏/目录同源）、
//       面板编辑操作转发（全部经 MainViewModel.EditFilter 集中管线：树编辑→互斥清 untagged→
//       ApplyTagFilter 一次到位，条件实时生效）、行内值选择展开状态（会话内 UI 态）。
// 不变量：快照对象不可变（Core 节点引用仅作编辑定位身份，引用即 Id——会话内树对象），
//         任何树变化经本类编辑方法 → EditFilter → Rebuild 全量重建（demo renderPanel 同构，
//         UI 侧无增量状态同步负担）；展开状态按条件节点引用记忆，重建后同引用行保持展开；
//         本类不持有根组引用（经 ReadFilter 只读进入，修改一律经 EditFilter）；
//         Core 层零依赖（TagFilterState 纯函数），无 UI 类型。
// 调用链：MainWindow（Flyout 宿主构造）→ TagFilterPanelControl（编辑事件转发 + 快照消费）→
//         本类 → MainViewModel.EditFilter → TagFilterState 编辑函数。

using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>
/// 面板组卡片快照（不可变；含最外层根组——组=卡片全层统一）。
/// 持有 Core 组节点引用仅供编辑操作定位，<see cref="CanAddGroup"/> 为 UI 层深度双保险
/// （状态层 AddGroup 同样拒绝超深，spec D2 MaxDepth=3 含根组）。
/// </summary>
public sealed class FilterPanelGroupModel
{
    /// <summary>所属 Core 组节点（编辑定位身份）。</summary>
    public required FilterGroupNode Node { get; init; }

    /// <summary>是否根组（根组无 ✕ 删除、无「满足以下」前后缀差异由 UI 决定）。</summary>
    public required bool IsRoot { get; init; }

    /// <summary>组深度（根 = 1，最深 <see cref="TagFilterState.MaxDepth"/>）。</summary>
    public required int Depth { get; init; }

    /// <summary>组连接词快照。</summary>
    public required FilterOp Op { get; init; }

    /// <summary>子节点序列（<see cref="FilterPanelCondModel"/> / 本类，顺序即表达式顺序）。</summary>
    public required IReadOnlyList<object> Children { get; init; }

    /// <summary>是否显示「＋ 条件组」按钮（深度 &lt; MaxDepth 才可再嵌组；UI 与状态层双保险）。</summary>
    public required bool CanAddGroup { get; init; }
}

/// <summary>面板条件行快照（不可变）。</summary>
public sealed class FilterPanelCondModel
{
    /// <summary>所属 Core 条件节点（编辑定位身份）。</summary>
    public required FilterConditionNode Node { get; init; }

    /// <summary>匹配词快照（In / NotIn，UI 着色依据）。</summary>
    public required FilterMatcher Matcher { get; init; }

    /// <summary>标签名值集快照（空集 = 条件未启用，UI 渲染「未选」）。</summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>是否否定条件（NotIn：行内 chips 与匹配词红色语义）。</summary>
    public bool Negated => Matcher == FilterMatcher.NotIn;
}

/// <summary>值选择候选的组快照（配置组 + 组内标签计数；与侧栏同源数据）。</summary>
public sealed class FilterPanelChoiceGroup
{
    /// <summary>组名。</summary>
    public required string Name { get; init; }

    /// <summary>是否互斥组（组名旁「互斥」标记）。</summary>
    public required bool Exclusive { get; init; }

    /// <summary>组内标签候选。</summary>
    public required IReadOnlyList<FilterPanelTagChoice> Tags { get; init; }
}

/// <summary>值选择候选的单标签快照。</summary>
public sealed class FilterPanelTagChoice
{
    /// <summary>标签名。</summary>
    public required string Name { get; init; }

    /// <summary>库内计数（最近一次索引计数快照）。</summary>
    public required int Count { get; init; }
}

/// <summary>
/// 筛选面板视图模型（tag-filter-tree Step 5）：快照构建 + 编辑操作转发 + 值选择展开状态。
/// 全量 Rebuild 惯例——每次编辑经 MainViewModel.EditFilter 管线后整体重建快照。
/// </summary>
public sealed class TagFilterPanelViewModel
{
    private readonly MainViewModel _owner;

    /// <summary>当前行内展开值选择的条件（null = 全收起；demo popover 单开语义，同一时刻至多一行）。</summary>
    private FilterConditionNode? _expandedValuesCond;

    public TagFilterPanelViewModel(MainViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>条件树快照根（组=卡片；未 Rebuild 前为 null）。</summary>
    public FilterPanelGroupModel? Root { get; private set; }

    /// <summary>值选择候选组序列（Rebuild 时从 MainViewModel 拉取，与侧栏同源）。</summary>
    public IReadOnlyList<FilterPanelChoiceGroup> ChoiceGroups { get; private set; } = [];

    /// <summary>人话表达式段序列（BuildExpression 快照，面板底预览渲染）。</summary>
    public IReadOnlyList<ExprSegment> ExprSegments { get; private set; } = [];

    /// <summary>是否空树（无任何条件行；表达式区显示「无筛选」文案，demo 同构）。</summary>
    public bool HasNoFilter { get; private set; } = true;

    /// <summary>当前展开值选择的条件节点引用（UI 渲染行内勾选区依据；引用作 Id）。</summary>
    public FilterConditionNode? ExpandedValuesCond => _expandedValuesCond;

    /// <summary>
    /// 全量重建快照（树 / 表达式段 / 值选择候选）：面板打开时与每次编辑后调用。
    /// 树读取经 MainViewModel.ReadFilter 只读进入（本类不持有根组引用）。
    /// </summary>
    public void Rebuild()
    {
        Root = _owner.ReadFilter(root => BuildGroupSnapshot(root, isRoot: true, depth: 1));
        ExprSegments = _owner.ReadFilter(TagFilterState.BuildExpression);
        HasNoFilter = Root is null || Root.Children.Count == 0;

        var (groups, counts) = _owner.GetFilterTagChoices();
        ChoiceGroups = groups
            .Select(group => new FilterPanelChoiceGroup
            {
                Name = group.Name,
                Exclusive = group.Exclusive,
                Tags = group.Tags
                    .Select(tag => new FilterPanelTagChoice
                    {
                        Name = tag.Name,
                        Count = counts.TryGetValue(tag.Name, out var count) ? count : 0,
                    })
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>切换某条件行的值选择展开态（纯 UI 态，不编辑树；再次点击同行收起，单开语义）。</summary>
    public void ToggleExpandedValues(FilterConditionNode condition)
        => _expandedValuesCond = ReferenceEquals(_expandedValuesCond, condition)
            ? null
            : condition;

    /// <summary>向组追加一条空条件（In / 空值 = 未启用），并自动展开其值选择（新建行直接进入选值）。</summary>
    public void AddCondition(FilterGroupNode parent)
    {
        FilterConditionNode? created = null;
        EditAndRebuild(root => created = TagFilterState.AddCondition(parent));
        if (created is not null)
        {
            _expandedValuesCond = created;
        }
    }

    /// <summary>向组追加子条件组（默认 Or，demo add-group 同构；状态层拒绝超深时忽略）。</summary>
    public void AddGroup(FilterGroupNode parent)
        => EditAndRebuild(root => TagFilterState.AddGroup(root, parent));

    /// <summary>删除节点（条件行或非根组；删除展开中的条件则同步收起）。</summary>
    public void RemoveNode(FilterNode node)
    {
        if (ReferenceEquals(_expandedValuesCond, node))
        {
            _expandedValuesCond = null;
        }

        EditAndRebuild(root => TagFilterState.RemoveNode(root, node));
    }

    /// <summary>切换组连接词（全部=And / 任一=Or）。</summary>
    public void SetGroupOp(FilterGroupNode group, FilterOp op)
        => EditAndRebuild(root => TagFilterState.SetOp(group, op));

    /// <summary>切换条件匹配词（包含任一=In / 不包含任一=NotIn）。</summary>
    public void SetMatcher(FilterConditionNode condition, FilterMatcher matcher)
        => EditAndRebuild(root => TagFilterState.SetMatcher(condition, matcher));

    /// <summary>切换条件值（值选择勾选行 / 已选 chip 的 ✕ 共用；移除按已存拼写）。</summary>
    public void ToggleValue(FilterConditionNode condition, string value)
        => EditAndRebuild(root => TagFilterState.ToggleValue(condition, value));

    /// <summary>清空条件树（面板底「清空条件」；重置为空 And 根组，demo clearCondBtn 同构）。</summary>
    public void ClearAll()
    {
        _expandedValuesCond = null;
        EditAndRebuild(TagFilterState.Clear);
    }

    /// <summary>编辑统一走 MainViewModel.EditFilter 集中管线（树编辑→互斥清 untagged→重应用），完成后重建快照。</summary>
    private void EditAndRebuild(Action<FilterGroupNode> edit)
    {
        _owner.EditFilter(edit);
        Rebuild();
    }

    /// <summary>递归构建组快照（条件叶子 / 子组；深度以根 = 1 计，条件是叶子不计层）。</summary>
    private static FilterPanelGroupModel BuildGroupSnapshot(FilterGroupNode node, bool isRoot, int depth)
    {
        var children = new List<object>(node.Children.Count);
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case FilterGroupNode group:
                    children.Add(BuildGroupSnapshot(group, isRoot: false, depth + 1));
                    break;
                case FilterConditionNode condition:
                    children.Add(new FilterPanelCondModel
                    {
                        Node = condition,
                        Matcher = condition.Matcher,
                        Values = [.. condition.Values],
                    });
                    break;
            }
        }

        return new FilterPanelGroupModel
        {
            Node = node,
            IsRoot = isRoot,
            Depth = depth,
            Op = node.Op,
            Children = children,
            CanAddGroup = depth < TagFilterState.MaxDepth,
        };
    }
}
