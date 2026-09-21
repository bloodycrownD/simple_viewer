using SimpleViewer.Services;

namespace SimpleViewer.Tests;

/// <summary>
/// 标签筛选条件树单测（tag-filter-tree spec Step 1/2，同构基准 demo\filter.js）：
/// 锁定求值（in / not in / 空值恒真 / 空组恒真 / 嵌套 / 三层 / 忽略大小写）、表达式段（括号与否定规则）、
/// 树编辑原语（增删条件组 / 切 Op / 切 Matcher / ToggleValue / Clear）、MaxDepth 拒超深、
/// QuickAdd 三分支（demo addQuickCond）、引用收集、标签删/改名联动、untagged 独立位互斥。
/// 旧 T_DF_S01~S07（多标签 OR 切换语义）已随 spec 显式推翻废弃。
/// </summary>
public class TagFilterStateTests
{
    [Fact]
    public void T_FT1_In_AnyValueMatches()
    {
        // In（包含任一）：项标签与值集任一命中即通过——多值即多标签 OR。
        var condition = Cond(FilterMatcher.In, "风景", "街拍");

        Assert.True(TagFilterState.Evaluate(condition, ["街拍"]));
        Assert.True(TagFilterState.Evaluate(condition, ["美食", "风景"]));
        Assert.False(TagFilterState.Evaluate(condition, ["美食"]));
        Assert.False(TagFilterState.Evaluate(condition, []));
    }

    [Fact]
    public void T_FT2_NotIn_PassesOnlyWhenNoValueHits()
    {
        // NotIn（不包含任一）：仅当项标签与值集无一命中才通过。
        var condition = Cond(FilterMatcher.NotIn, "已修", "废片");

        Assert.True(TagFilterState.Evaluate(condition, ["待修"]));
        Assert.True(TagFilterState.Evaluate(condition, []));
        Assert.False(TagFilterState.Evaluate(condition, ["已修"]));
        Assert.False(TagFilterState.Evaluate(condition, ["夜景", "已修"]));
    }

    [Fact]
    public void T_FT3_EmptyValues_AlwaysTrue()
    {
        // 空 Values = 条件未启用 = 恒真（In / NotIn 同；demo：未选值条件不生效）。
        Assert.True(TagFilterState.Evaluate(Cond(FilterMatcher.In), ["美食"]));
        Assert.True(TagFilterState.Evaluate(Cond(FilterMatcher.NotIn), ["已修"]));
        Assert.True(TagFilterState.Evaluate(Cond(FilterMatcher.In), []));
    }

    [Fact]
    public void T_FT4_EmptyGroupAlwaysTrue_RootWithoutActiveConditionsIsNoFilter()
    {
        // 空组恒真（demo：空组忽略）。
        var emptyRoot = new FilterGroupNode();
        Assert.True(TagFilterState.Evaluate(emptyRoot, ["风景"]));
        Assert.True(TagFilterState.Evaluate(emptyRoot, []));

        // 根组只有空值条件（无有效条件）= 无筛选 = 全量通过。
        var inertRoot = Group(FilterOp.And, Cond(FilterMatcher.In), Cond(FilterMatcher.NotIn));
        Assert.True(TagFilterState.Evaluate(inertRoot, ["任意"]));
        Assert.True(TagFilterState.Evaluate(inertRoot, []));
    }

    [Fact]
    public void T_FT5_NestedCombination()
    {
        // (含[风景 街拍] 或 含[星标]) 且 不含[已修]——嵌套 Or 组与根 And 的组合求值。
        var root = Group(FilterOp.And,
            Group(FilterOp.Or,
                Cond(FilterMatcher.In, "风景", "街拍"),
                Cond(FilterMatcher.In, "星标")),
            Cond(FilterMatcher.NotIn, "已修"));

        Assert.True(TagFilterState.Evaluate(root, ["星标"]));            // Or 右支命中
        Assert.True(TagFilterState.Evaluate(root, ["街拍"]));            // Or 左支命中
        Assert.True(TagFilterState.Evaluate(root, ["美食", "风景"]));    // 多标签任一命中
        Assert.False(TagFilterState.Evaluate(root, ["星标", "已修"]));   // Or 命中但 NotIn 失败
        Assert.False(TagFilterState.Evaluate(root, ["美食", "已修"]));   // 两支皆失败
        Assert.False(TagFilterState.Evaluate(root, ["美食"]));           // Or 失败（NotIn 虽真，And 整体假）
    }

    [Fact]
    public void T_FT6_ThreeLevelNesting_EvaluatesCorrectly()
    {
        // 三层嵌套边界：根(And) > 组(Or) > 组(And)[含(风景) 且 不含(已修)]，Or 另有 含(人像)，根另有 含(星标)。
        var root = Group(FilterOp.And,
            Group(FilterOp.Or,
                Group(FilterOp.And,
                    Cond(FilterMatcher.In, "风景"),
                    Cond(FilterMatcher.NotIn, "已修")),
                Cond(FilterMatcher.In, "人像")),
            Cond(FilterMatcher.In, "星标"));

        Assert.True(TagFilterState.Evaluate(root, ["风景", "星标"]));              // 最深 And 满足
        Assert.False(TagFilterState.Evaluate(root, ["风景", "星标", "已修"]));    // 最深 NotIn 失败
        Assert.False(TagFilterState.Evaluate(root, ["星标"]));                     // 最深 And 失败，Or 亦无支命中
        Assert.True(TagFilterState.Evaluate(root, ["人像", "星标"]));              // Or 走第二支
    }

    [Fact]
    public void T_FT7_TagComparisonIsOrdinalIgnoreCase()
    {
        // 标签比较 OrdinalIgnoreCase（与索引/文件名协议一致）。
        var inCondition = Cond(FilterMatcher.In, "SCENERY");
        Assert.True(TagFilterState.Evaluate(inCondition, ["scenery"]));
        Assert.True(TagFilterState.Evaluate(inCondition, ["Scenery"]));

        var notInCondition = Cond(FilterMatcher.NotIn, "Scenery");
        Assert.False(TagFilterState.Evaluate(notInCondition, ["SCENERY"]));
        Assert.True(TagFilterState.Evaluate(notInCondition, ["city"]));
    }

    [Fact]
    public void T_FT8_BuildExpressionSegments()
    {
        // (含[风景 街拍] 或 含[星标]) 且 不含[已修]：
        // 非根多部件组（Or 组）加括号、根组不加；部件间插组连接词；NotIn 条件段带否定标记。
        var root = Group(FilterOp.And,
            Group(FilterOp.Or,
                Cond(FilterMatcher.In, "风景", "街拍"),
                Cond(FilterMatcher.In, "星标")),
            Cond(FilterMatcher.NotIn, "已修"));

        var segments = TagFilterState.BuildExpression(root);

        Assert.Equal(7, segments.Count);
        AssertParen(segments[0], open: true);
        AssertCond(segments[1], FilterMatcher.In, ["风景", "街拍"], negated: false);
        AssertOp(segments[2], FilterOp.Or);
        AssertCond(segments[3], FilterMatcher.In, ["星标"], negated: false);
        AssertParen(segments[4], open: false);
        AssertOp(segments[5], FilterOp.And);
        AssertCond(segments[6], FilterMatcher.NotIn, ["已修"], negated: true);

        // 空组不产段、单部件组不加括号不产连接词：根[ 空组, 含(星标) ] → 仅一个条件段。
        var sparse = Group(FilterOp.And, new FilterGroupNode(), Cond(FilterMatcher.In, "星标"));
        var sparseSegments = TagFilterState.BuildExpression(sparse);
        var sparseCond = Assert.IsType<CondSegment>(sparseSegments.Single());
        Assert.Equal(["星标"], sparseCond.Values);

        // 空根 = 无筛选 = 无段。
        Assert.Empty(TagFilterState.BuildExpression(new FilterGroupNode()));
    }

    [Fact]
    public void T_FT9_TreeEditingPrimitives()
    {
        var root = new FilterGroupNode();

        // AddCondition：追加空 In 条件并返回该节点。
        var condition = TagFilterState.AddCondition(root);
        Assert.Same(condition, root.Children.Single());
        Assert.Equal(FilterMatcher.In, condition.Matcher);
        Assert.Empty(condition.Values);

        // ToggleValue：不在则追加（返回 true=现在值集中）；再切按 OrdinalIgnoreCase 识别已存值并移除。
        Assert.True(TagFilterState.ToggleValue(condition, "Scenery"));
        Assert.Equal(["Scenery"], condition.Values);
        Assert.False(TagFilterState.ToggleValue(condition, "scenery"));
        Assert.Empty(condition.Values);

        // SetMatcher / SetOp：直改。
        TagFilterState.SetMatcher(condition, FilterMatcher.NotIn);
        Assert.Equal(FilterMatcher.NotIn, condition.Matcher);
        TagFilterState.SetMatcher(condition, FilterMatcher.In);

        var childGroup = TagFilterState.AddGroup(root, root);
        Assert.NotNull(childGroup);
        Assert.Equal(FilterOp.Or, childGroup!.Op);   // demo add-group 新组默认 Or
        Assert.Contains(childGroup, root.Children);

        // RemoveNode：删树内节点成功；根与树外节点返回 false。
        Assert.True(TagFilterState.RemoveNode(root, childGroup));
        Assert.DoesNotContain(childGroup, root.Children);
        Assert.False(TagFilterState.RemoveNode(root, root));
        Assert.False(TagFilterState.RemoveNode(root, new FilterConditionNode()));

        // Clear：清空子节点并回默认 And（demo「清空条件」= 重置空 And 根组）。
        TagFilterState.AddCondition(root);
        root.Op = FilterOp.Or;
        TagFilterState.Clear(root);
        Assert.Empty(root.Children);
        Assert.Equal(FilterOp.And, root.Op);
    }

    [Fact]
    public void T_FT10_MaxDepth_AddGroupIntoThirdLevelRejected()
    {
        // 组最深 MaxDepth=3 层（含根）：根(1) > 组(2) > 组(3) 可建，向第 3 层组内再加组被拒（返回 null、不留半成品）。
        var root = new FilterGroupNode();
        var second = TagFilterState.AddGroup(root, root);
        Assert.NotNull(second);
        var third = TagFilterState.AddGroup(root, second!);
        Assert.NotNull(third);

        Assert.Null(TagFilterState.AddGroup(root, third!));
        Assert.Empty(third!.Children);

        // 条件是叶子不受组深限制：第 3 层组内仍可 ＋ 条件（demo 仅「＋ 条件组」受深度门控）。
        var leaf = TagFilterState.AddCondition(third!);
        Assert.Same(leaf, Assert.Single(third.Children));
    }

    [Fact]
    public void T_FT11_QuickAddThreeBranches()
    {
        // 分支③（否则追加）：新根（And）→ 直接追加单值 In 条件。
        var root = new FilterGroupNode();
        Assert.True(TagFilterState.QuickAdd(root, "风景"));
        var first = Assert.IsType<FilterConditionNode>(root.Children.Single());
        Assert.Equal(FilterMatcher.In, first.Matcher);
        Assert.Equal(["风景"], first.Values);

        // 根 Op=And 不走合并：再加速分追加为新行。
        Assert.True(TagFilterState.QuickAdd(root, "星标"));
        Assert.Equal(2, root.Children.Count);

        // 分支②（合并）：根 Op=Or 且已有单值 In 行 → 合并进首个该行（一行多值）。
        TagFilterState.Clear(root);
        root.Op = FilterOp.Or;
        TagFilterState.QuickAdd(root, "风景");
        Assert.True(TagFilterState.QuickAdd(root, "星标"));
        var merged = Assert.IsType<FilterConditionNode>(root.Children.Single());
        Assert.Equal(["风景", "星标"], merged.Values);

        // 根 Or 但既有 In 行均多值 → 无合并目标，仍追加新行。
        TagFilterState.Clear(root);
        root.Op = FilterOp.Or;
        TagFilterState.QuickAdd(root, "风景");
        TagFilterState.QuickAdd(root, "街拍");    // 合并进单值行 → 多值行
        TagFilterState.QuickAdd(root, "星标");    // 无单值行目标 → 追加
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(["风景", "街拍"], Assert.IsType<FilterConditionNode>(root.Children[0]).Values);

        // 分支①（已存在忽略）：全树（含嵌套组、含多值行、忽略大小写）已有同值 In 条件 → 返回 false 且不改树。
        var nested = TagFilterState.AddGroup(root, root);
        Assert.NotNull(nested);
        nested!.Children.Add(Cond(FilterMatcher.In, "Night"));
        Assert.False(TagFilterState.QuickAdd(root, "night"));
        Assert.False(TagFilterState.QuickAdd(root, "风景"));
        Assert.False(TagFilterState.QuickAdd(root, "星标"));
        Assert.Equal(3, root.Children.Count);
    }

    [Fact]
    public void T_FT12_CollectReferencedTagsIncludesNestedValues()
    {
        // 全树（含嵌套组、含 NotIn 条件）的全部值都进集合；空值条件本无值不贡献；
        // 集合比较器 OrdinalIgnoreCase——侧栏高亮判定直接 Contains。
        var root = Group(FilterOp.And,
            Cond(FilterMatcher.In, "风景", "街拍"),
            Group(FilterOp.Or,
                Cond(FilterMatcher.NotIn, "已修"),
                Cond(FilterMatcher.In, "Scenery")),
            Cond(FilterMatcher.In));

        var tags = TagFilterState.CollectReferencedTags(root);

        Assert.Equal(4, tags.Count);
        Assert.Contains("风景", tags);
        Assert.Contains("街拍", tags);
        Assert.Contains("已修", tags);
        Assert.Contains("scenery", tags);      // 存的是 "Scenery"，忽略大小写命中
        Assert.DoesNotContain("美食", tags);
    }

    [Fact]
    public void T_FT13_RemoveAndRenameTagReferences()
    {
        var first = Cond(FilterMatcher.In, "风景", "街拍");
        var nested = Cond(FilterMatcher.In, "风景");
        var notIn = Cond(FilterMatcher.NotIn, "已修");
        var root = Group(FilterOp.And, first, Group(FilterOp.Or, nested), notIn);

        // RemoveTagReferences：全树（含嵌套组）按 OrdinalIgnoreCase 清值；
        // 被清空的条件保留为「未启用」恒真行（不隐式删行），其余值不受影响。
        Assert.True(TagFilterState.RemoveTagReferences(root, "风景"));
        Assert.Equal(["街拍"], first.Values);
        Assert.Empty(nested.Values);
        Assert.True(TagFilterState.Evaluate(root, ["街拍"]));      // 未启用行不再约束
        Assert.False(TagFilterState.RemoveTagReferences(root, "风景"));   // 已无该值 → 无改动

        // RenameTagReferences：等值项（忽略大小写匹配旧名）统一改为新拼写。
        notIn.Values.Add("Street");
        Assert.True(TagFilterState.RenameTagReferences(root, "street", "街道"));
        Assert.Equal(["已修", "街道"], notIn.Values);
        Assert.True(TagFilterState.RenameTagReferences(root, "街拍", "城市"));
        Assert.Equal(["城市"], first.Values);
        Assert.False(TagFilterState.RenameTagReferences(root, "不存在", "新名"));
    }

    [Fact]
    public void T_FT14_ToggleUntaggedSemanticsAndTreeMutualExclusion()
    {
        // 激活 untagged = 清空条件树（互斥清树）并返回 true；再点关闭返回 false（激活期间树恒空，关闭回全量）。
        var root = Group(FilterOp.Or, Cond(FilterMatcher.In, "风景"));
        Assert.True(TagFilterState.ToggleUntagged(root, untagged: false));
        Assert.Empty(root.Children);
        Assert.Equal(FilterOp.And, root.Op);    // 清空同时回默认 And（demo「清空条件」同构）
        Assert.False(TagFilterState.ToggleUntagged(root, untagged: true));

        // untagged 谓词（原 Matches 的 untagged 分支保留独立）：项无任何标签才命中。
        Assert.True(TagFilterState.MatchesUntagged([]));
        Assert.True(TagFilterState.MatchesUntagged(null));
        Assert.False(TagFilterState.MatchesUntagged(["风景"]));
    }

    /// <summary>构造条件节点（测试速记）。</summary>
    private static FilterConditionNode Cond(FilterMatcher matcher, params string[] values)
        => new() { Matcher = matcher, Values = [.. values] };

    /// <summary>构造组节点（测试速记）。</summary>
    private static FilterGroupNode Group(FilterOp op, params FilterNode[] children)
        => new() { Op = op, Children = [.. children] };

    /// <summary>断言段为条件段且各字段符合预期。</summary>
    private static void AssertCond(ExprSegment segment, FilterMatcher matcher, IReadOnlyList<string> values, bool negated)
    {
        var cond = Assert.IsType<CondSegment>(segment);
        Assert.Equal(matcher, cond.Matcher);
        Assert.Equal(values, cond.Values);
        Assert.Equal(negated, cond.Negated);
    }

    /// <summary>断言段为连接词段且连接词符合预期。</summary>
    private static void AssertOp(ExprSegment segment, FilterOp op)
        => Assert.Equal(op, Assert.IsType<OpSegment>(segment).Op);

    /// <summary>断言段为括号段且开/闭符合预期。</summary>
    private static void AssertParen(ExprSegment segment, bool open)
        => Assert.Equal(open, Assert.IsType<ParenSegment>(segment).Open);
}
