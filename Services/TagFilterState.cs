// 职责：标签筛选条件树（纯 POCO + 静态函数，Core 可测）——条件树模型 / 单一内存求值 / 人话表达式段 /
//       树编辑纯函数 / 左栏快捷追加 / 标签删改名联动 / untagged 独立位（tag-filter-tree spec D2~D4，
//       求值与编辑语义同构基准 demo\filter.js）。
// 不变量：求值语义与 demo evalCond/evalNode 逐条同构——
//         In = 项标签与值集任一命中（OrdinalIgnoreCase）、NotIn = 无一命中、空 Values = 恒真（未启用）、
//         空组恒真、组 And = 所有子真 / Or = 任一子真；根组无有效条件 = 无筛选（全量）。
//         表达式段与 demo exprChips 同构：空组/空子树不产段，部件间插组连接词，非根多部件组加括号。
//         树编辑全部原位改（会话内单棵可变树，编辑后由调用方全量重建 UI——demo refreshAll 同构）；
//         深度上限 MaxDepth=3（含根组，状态层强制而非仅 UI 层）：AddGroup 超深返回 null 拒绝，
//         条件是叶子不受组深限制（demo 仅「＋ 条件组」受深度门控，任何层的组都可 ＋ 条件）。
//         QuickAdd 与 demo addQuickCond 三分支同构（见方法注释）；条件值统一存标签名字符串（spec D3，
//         事实源是文件名），全链比较 OrdinalIgnoreCase（与索引/文件名协议一致）。
//         untagged 独立位与树互斥：激活 untagged 即清树（ToggleUntagged），树编辑时清 untagged 位由
//         调用方持有标志实现（spec D4，Core 不持有该标志）。
// 调用链：MainViewModel（Step 3 起接线：ApplyFilterAsync / MatchesTagFilter / QuickAdd / 标签联动）与
//         tests/SimpleViewer.Tests/TagFilterStateTests.cs 直接锁定；
//         Core 边界：纯 POCO + 静态函数，禁 ObservableObject / WinUI 类型（Core csproj 无这些包）。

namespace SimpleViewer.Services;

/// <summary>组连接词（demo f-op-select「全部/任一」）：And = 满足以下全部条件；Or = 满足以下任一条件。</summary>
public enum FilterOp
{
    /// <summary>所有子节点为真时组为真。</summary>
    And,

    /// <summary>任一子节点为真时组为真。</summary>
    Or,
}

/// <summary>条件匹配词（demo f-select「包含任一/不包含任一」）。</summary>
public enum FilterMatcher
{
    /// <summary>包含任一：项标签命中 Values 中任一值。</summary>
    In,

    /// <summary>不包含任一：项标签与 Values 无一命中。</summary>
    NotIn,
}

/// <summary>条件树节点基类（判别用 pattern matching：FilterGroupNode / FilterConditionNode）。</summary>
public abstract class FilterNode
{
}

/// <summary>条件组节点：以 And/Or 组合子节点，组可嵌套（最深 <see cref="TagFilterState.MaxDepth"/> 层，含根组）。</summary>
public sealed class FilterGroupNode : FilterNode
{
    /// <summary>组连接词。根组默认 And（枚举默认值）；面板「＋ 条件组」新建子组默认 Or（demo add-group 同构）。</summary>
    public FilterOp Op;

    /// <summary>子节点（条件行或子组），有序——顺序即表达式顺序。</summary>
    public List<FilterNode> Children = [];
}

/// <summary>条件行节点：匹配词 + 标签名值集。值 = 标签名字符串（spec D3，非 TagDefinition.Id）。</summary>
public sealed class FilterConditionNode : FilterNode
{
    /// <summary>匹配词，默认 In。</summary>
    public FilterMatcher Matcher = FilterMatcher.In;

    /// <summary>标签名值集（比较 OrdinalIgnoreCase）。空集 = 条件未启用（求值恒真，UI 渲染「未选」）。</summary>
    public List<string> Values = [];
}

/// <summary>人话表达式段基类：筛选条 chips 据段序列渲染（demo exprChips 同构，spec D2）。</summary>
public abstract class ExprSegment
{
}

/// <summary>
/// 条件段：一个条件行。文本如「标签：风景 / 街拍」；否定段（NotIn）UI 渲染红色 + 「非」前缀；
/// ✕ 删除该节点——<see cref="Node"/> 引用即段身份（NodeId 的引用形式：会话内树对象，引用当 Id 用）。
/// </summary>
public sealed class CondSegment : ExprSegment
{
    /// <summary>所属条件节点（供筛选条 ✕ 直接 RemoveNode；快照渲染下编辑后整体重建，引用恒有效）。</summary>
    public required FilterConditionNode Node { get; init; }

    /// <summary>匹配词快照。</summary>
    public required FilterMatcher Matcher { get; init; }

    /// <summary>标签名值集快照（空集由 UI 渲染「未选」）。</summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>是否否定段（Matcher = NotIn）。</summary>
    public required bool Negated { get; init; }
}

/// <summary>连接词段：And → 「且」、Or → 「或」，插在相邻部件之间。</summary>
public sealed class OpSegment : ExprSegment
{
    /// <summary>组连接词。</summary>
    public required FilterOp Op { get; init; }
}

/// <summary>括号段：非根多部件组的包裹（demo exprChips 括号规则）。</summary>
public sealed class ParenSegment : ExprSegment
{
    /// <summary>true = 左括号「(」；false = 右括号「)」。</summary>
    public required bool Open { get; init; }
}

/// <summary>
/// 标签筛选条件树状态机（纯静态函数）：求值 / 表达式 / 收集 / 树编辑 / 快捷追加 / 标签联动 / untagged 位。
/// 树是会话内单棵可变对象，编辑函数原位改；调用方编辑后统一重应用筛选并全量重建 UI（demo refreshAll 同构）。
/// </summary>
public static class TagFilterState
{
    /// <summary>组嵌套深度上限（含根组，状态层强制）：根(1) → 子组(2) → 孙组(3)，再深拒绝。条件是叶子不计层。</summary>
    public const int MaxDepth = 3;

    /// <summary>
    /// 单一内存求值器（demo evalNode/evalCond 同构）：对一项的标签集求节点布尔值。
    /// 条件 In = 任一命中、NotIn = 无一命中、空 Values 恒真；空组恒真；组 And = 所有子真 / Or = 任一子真。
    /// 比较 OrdinalIgnoreCase。扫描追加块过滤与筛选应用共用本函数（spec D1 单一求值器）。
    /// </summary>
    public static bool Evaluate(FilterNode node, IReadOnlyList<string> itemTags)
    {
        if (node is FilterConditionNode condition)
        {
            // 空 Values = 未启用 = 恒真（demo：未选值条件不生效）。
            if (condition.Values.Count == 0)
            {
                return true;
            }

            var hit = condition.Values.Any(value =>
                itemTags.Any(tag => string.Equals(tag, value, StringComparison.OrdinalIgnoreCase)));
            return condition.Matcher == FilterMatcher.In ? hit : !hit;
        }

        var group = (FilterGroupNode)node;

        // 空组恒真（demo：空组忽略）。
        if (group.Children.Count == 0)
        {
            return true;
        }

        foreach (var child in group.Children)
        {
            var result = Evaluate(child, itemTags);
            if (group.Op == FilterOp.And && !result)
            {
                return false;   // And 短路：遇假即假。
            }

            if (group.Op == FilterOp.Or && result)
            {
                return true;    // Or 短路：遇真即真。
            }
        }

        return group.Op == FilterOp.And;    // And 走完全真；Or 走完仍无一真。
    }

    /// <summary>
    /// 构建人话表达式段序列（demo exprChips 同构）：条件段 / 连接词段 / 括号段。
    /// 括号规则 = 非根且多部件（&gt;1 个非空子部件）的组加括号；根组与单部件组不加。
    /// 空组/空子树不产出任何段（含空 Values 的条件仍产段——「未选」行照常展示，demo 同构）；
    /// 空根返回空列表（无筛选）。
    /// </summary>
    public static List<ExprSegment> BuildExpression(FilterNode root)
    {
        var segments = new List<ExprSegment>();
        AppendParts(root, isRoot: true, segments);
        return segments;
    }

    /// <summary>
    /// 收集全树引用的标签名（含嵌套组、含 NotIn 条件、含空值条件外的全部值——空值条件本无值）。
    /// 返回 OrdinalIgnoreCase 比较器集合：侧栏高亮判定直接 Contains（spec D4 高亮口径）。
    /// </summary>
    public static HashSet<string> CollectReferencedTags(FilterNode root)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkConditions(root, condition =>
        {
            foreach (var value in condition.Values)
            {
                tags.Add(value);
            }
        });
        return tags;
    }

    /// <summary>
    /// 向组追加一条空条件（In / 空值 = 未启用），返回新条件节点（面板可据此直接定位/打开值选择）。
    /// 条件是叶子不受组深限制——demo 仅「＋ 条件组」受深度门控，任何层的组都可 ＋ 条件。
    /// </summary>
    public static FilterConditionNode AddCondition(FilterGroupNode parent)
    {
        var condition = new FilterConditionNode();
        parent.Children.Add(condition);
        return condition;
    }

    /// <summary>
    /// 向父组追加子组（默认 Or，demo add-group 同构），返回新组节点。
    /// 深度校验（状态层强制）：组最深 <see cref="MaxDepth"/> 层（含根组）——父组已在第 MaxDepth 层时
    /// 子组将超深，拒绝并返回 null（不留半成品）。
    /// </summary>
    public static FilterGroupNode? AddGroup(FilterGroupNode root, FilterGroupNode parent)
    {
        var parentDepth = GroupDepth(root, parent, 1);
        if (parentDepth < 0 || parentDepth >= MaxDepth)
        {
            return null;    // 树外节点（防御）或超深：拒绝。
        }

        var group = new FilterGroupNode { Op = FilterOp.Or };
        parent.Children.Add(group);
        return group;
    }

    /// <summary>
    /// 删除节点（从其父组的 Children 移除，引用相等定位）。根组无父不可删（demo 根组无 ✕）；
    /// 节点不在树内返回 false。
    /// </summary>
    public static bool RemoveNode(FilterGroupNode root, FilterNode node)
    {
        var parent = FindParent(root, node);
        if (parent is null)
        {
            return false;
        }

        parent.Children.Remove(node);
        return true;
    }

    /// <summary>切换组连接词（And ⇄ Or）。</summary>
    public static void SetOp(FilterGroupNode group, FilterOp op)
    {
        group.Op = op;
    }

    /// <summary>切换条件匹配词（In ⇄ NotIn）。</summary>
    public static void SetMatcher(FilterConditionNode condition, FilterMatcher matcher)
    {
        condition.Matcher = matcher;
    }

    /// <summary>
    /// 切换条件值：值已在值集（OrdinalIgnoreCase）则移除、不在则追加，返回切换后是否在值集中
    /// （值选择浮层勾选态直接用返回值）。移除按已存拼写移除（追加存调用方拼写）。
    /// </summary>
    public static bool ToggleValue(FilterConditionNode condition, string value)
    {
        for (var i = 0; i < condition.Values.Count; i++)
        {
            if (string.Equals(condition.Values[i], value, StringComparison.OrdinalIgnoreCase))
            {
                condition.Values.RemoveAt(i);
                return false;
            }
        }

        condition.Values.Add(value);
        return true;
    }

    /// <summary>清空条件树：子节点清空、Op 回默认 And（demo「清空条件」= 重置为空 And 根组，同构）。</summary>
    public static void Clear(FilterGroupNode root)
    {
        root.Op = FilterOp.And;
        root.Children.Clear();
    }

    /// <summary>
    /// 左栏快捷追加（demo addQuickCond 三分支同构），返回 false = 已存在被忽略：
    /// ① 全树已存在含该值的 In 条件（OrdinalIgnoreCase）→ 忽略返回 false（demo「已在筛选中」）；
    /// ② 根组 Op=Or 且全树已有单值 In 条件 → 合并进首个该行（OR 语义直观复用一行）；
    /// ③ 否则根组追加一条单值 In 条件。
    /// </summary>
    public static bool QuickAdd(FilterGroupNode root, string tagName)
    {
        // 分支①：已存在同值 In 条件（全树扫描，含嵌套组）。
        var exists = false;
        WalkConditions(root, condition =>
        {
            if (condition.Matcher == FilterMatcher.In && ContainsOrdinalIgnoreCase(condition.Values, tagName))
            {
                exists = true;
            }
        });
        if (exists)
        {
            return false;
        }

        // 分支②：仅根组为 Or 时才合并；目标是全树首个单值 In 条件（demo walkConds 顺序）。
        if (root.Op == FilterOp.Or)
        {
            FilterConditionNode? target = null;
            WalkConditions(root, condition =>
            {
                if (target is null && condition.Matcher == FilterMatcher.In && condition.Values.Count == 1)
                {
                    target = condition;
                }
            });
            if (target is not null)
            {
                target.Values.Add(tagName);
                return true;
            }
        }

        // 分支③：根组追加单值 In 条件。
        root.Children.Add(new FilterConditionNode
        {
            Matcher = FilterMatcher.In,
            Values = [tagName],
        });
        return true;
    }

    /// <summary>
    /// 左栏简单轨道——无修饰点击（2026-09-24 恢复 2026-09-19 用户拍板的 Explorer 单选心智，
    /// 推翻 tag-filter-tree「点击一律 QuickAdd 追加」的左栏语义）：
    /// 单选替换——树清空重建为根（Or）+ 单条单值 In 条件；
    /// 当前筛选恰好唯一激活该标签时清空树（再点取消，回全量）。
    /// </summary>
    /// <returns>树是否变化（恒 true，除非标签名空白）。</returns>
    public static bool SelectSingleTag(FilterGroupNode root, string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        // 再点取消：仅当树恰为本函数产出的「单选简单形态」（根 Or + 唯一单值 In 条件 = 本标签）——
        // 复杂树（如 in+notIn 同时涉及该标签）不算「唯一激活」，点它仍是替换（单选筛选）。
        if (root.Op == FilterOp.Or
            && root.Children.Count == 1
            && root.Children[0] is FilterConditionNode { Matcher: FilterMatcher.In } sole
            && sole.Values.Count == 1
            && string.Equals(sole.Values[0], tagName, StringComparison.OrdinalIgnoreCase))
        {
            Clear(root);
            return true;
        }

        root.Children.Clear();
        root.Op = FilterOp.Or;
        root.Children.Add(new FilterConditionNode
        {
            Matcher = FilterMatcher.In,
            Values = [tagName],
        });
        return true;
    }

    /// <summary>
    /// 左栏简单轨道——Ctrl+点击加减选（toggle）：
    /// 已引用 → RemoveTagReferences 全树移除（清空的条件保留为未启用恒真行，面板可见可再赋值）；
    /// 未引用 → QuickAdd 三分支并入（尊重现有树结构——复杂筛选是简单筛选的超集）。
    /// </summary>
    /// <returns>树是否变化。</returns>
    public static bool ToggleTagInFilter(FilterGroupNode root, string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        return CollectReferencedTags(root).Contains(tagName, StringComparer.OrdinalIgnoreCase)
            ? RemoveTagReferences(root, tagName)
            : QuickAdd(root, tagName);
    }

    /// <summary>
    /// 标签删除联动：全树移除该标签的全部引用（OrdinalIgnoreCase）。
    /// 被清空的条件保留为「未启用」恒真行（面板可见、可再赋值；不隐式删行）。
    /// 返回是否发生了改动（调用方据此决定是否重应用筛选）。
    /// </summary>
    public static bool RemoveTagReferences(FilterNode root, string tagName)
    {
        var changed = false;
        WalkConditions(root, condition =>
        {
            var before = condition.Values.Count;
            condition.Values.RemoveAll(value =>
                string.Equals(value, tagName, StringComparison.OrdinalIgnoreCase));
            if (condition.Values.Count != before)
            {
                changed = true;
            }
        });
        return changed;
    }

    /// <summary>
    /// 标签改名联动：全树将等值项（OrdinalIgnoreCase 匹配旧名）统一改为新拼写。
    /// 返回是否发生了改动。
    /// </summary>
    public static bool RenameTagReferences(FilterNode root, string oldName, string newName)
    {
        var changed = false;
        WalkConditions(root, condition =>
        {
            for (var i = 0; i < condition.Values.Count; i++)
            {
                if (string.Equals(condition.Values[i], oldName, StringComparison.OrdinalIgnoreCase))
                {
                    condition.Values[i] = newName;
                    changed = true;
                }
            }
        });
        return changed;
    }

    /// <summary>
    /// 切换「无标签」独立位（untagged ∅ 入口语义保留，与条件树互斥——spec D4）：
    /// 未激活 → 激活 = 清空条件树（互斥清树）并返回 true；已激活 → 再点关闭返回 false
    /// （激活期间树恒被清空，关闭即回全量，无需恢复）。
    /// 反方向互斥（任何树编辑时清 untagged 位）由调用方持有该标志并置 false——Core 不持有标志。
    /// </summary>
    public static bool ToggleUntagged(FilterGroupNode root, bool untagged)
    {
        if (untagged)
        {
            return false;   // 已激活 → 关闭。
        }

        Clear(root);        // 激活 → 互斥清树。
        return true;
    }

    /// <summary>「无标签」谓词（原 Matches 的 untagged 分支保留独立）：项无任何标签才命中。</summary>
    public static bool MatchesUntagged(IEnumerable<string>? itemTags)
        => itemTags is null || !itemTags.Any();

    /// <summary>按序访问全树条件节点（含嵌套组；demo walkConds 同构顺序：父先于子、子按 Children 序）。</summary>
    private static void WalkConditions(FilterNode node, Action<FilterConditionNode> visit)
    {
        if (node is FilterConditionNode condition)
        {
            visit(condition);
            return;
        }

        foreach (var child in ((FilterGroupNode)node).Children)
        {
            WalkConditions(child, visit);
        }
    }

    /// <summary>递归拼段（BuildExpression 实现）：条件 → 单条件段；组 → 子部件以组连接词相连，
    /// 非根且多部件时加括号；空组/全空子树不产出任何段（demo exprChips 的 filter(Boolean) 同构）。</summary>
    private static void AppendParts(FilterNode node, bool isRoot, List<ExprSegment> result)
    {
        if (node is FilterConditionNode condition)
        {
            result.Add(new CondSegment
            {
                Node = condition,
                Matcher = condition.Matcher,
                Values = [.. condition.Values],
                Negated = condition.Matcher == FilterMatcher.NotIn,
            });
            return;
        }

        var group = (FilterGroupNode)node;

        // 先收集各子节点的段，丢掉空部件（空组/空子树），部件间再插连接词 / 包括号。
        var parts = new List<List<ExprSegment>>();
        foreach (var child in group.Children)
        {
            var part = new List<ExprSegment>();
            AppendParts(child, isRoot: false, part);
            if (part.Count > 0)
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        var wrap = !isRoot && parts.Count > 1;
        if (wrap)
        {
            result.Add(new ParenSegment { Open = true });
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                result.Add(new OpSegment { Op = group.Op });
            }

            result.AddRange(parts[i]);
        }

        if (wrap)
        {
            result.Add(new ParenSegment { Open = false });
        }
    }

    /// <summary>计算 target 组的深度（根 = 1）；target 不在树内返回 -1（防御）。</summary>
    private static int GroupDepth(FilterGroupNode group, FilterGroupNode target, int depth)
    {
        if (ReferenceEquals(group, target))
        {
            return depth;
        }

        foreach (var child in group.Children)
        {
            if (child is FilterGroupNode childGroup)
            {
                var found = GroupDepth(childGroup, target, depth + 1);
                if (found > 0)
                {
                    return found;
                }
            }
        }

        return -1;
    }

    /// <summary>找 node 的父组（引用相等比较子节点）；根组或树外节点返回 null。</summary>
    private static FilterGroupNode? FindParent(FilterGroupNode group, FilterNode node)
    {
        foreach (var child in group.Children)
        {
            if (ReferenceEquals(child, node))
            {
                return group;
            }

            if (child is FilterGroupNode childGroup)
            {
                var found = FindParent(childGroup, node);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>值集是否含指定值（OrdinalIgnoreCase）。</summary>
    private static bool ContainsOrdinalIgnoreCase(IEnumerable<string> values, string value)
        => values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}
