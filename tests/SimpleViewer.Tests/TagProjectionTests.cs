using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

/// <summary>
/// 标签投影单测（batch-tag-management spec Step 1，拍板 D1/D12，用例 T_PR_01~08）：
/// 锁定未定义集合（counts 键集 − 配置组标签集 / OrdinalIgnoreCase 差集与大小写变体排除 / 计数保留 /
/// 拼写取计数键 / 全配置与空 counts 空态）与选中集并集（并集 + 选中集内计数 / 配置组外标签不滤除 /
/// 空选中空并集 / 大小写合并取首见拼写）；排序口径统一 = 计数降序、同计数按名 Ordinal 升序。
/// </summary>
public class TagProjectionTests
{
    [Fact]
    public void T_PR_01_DifferenceAndCaseVariantExclusion()
    {
        // 未定义集合 = counts 键集 − 全部配置组标签名集（多组联合）：已配置的「风景」「街拍」排除；
        // 「scenery」是组 B「SCENERY」的大小写变体，按 OrdinalIgnoreCase 同样排除；未配置的保留。
        var counts = new Dictionary<string, int>
        {
            ["风景"] = 5,
            ["街拍"] = 4,
            ["scenery"] = 3,
            ["美食"] = 7,
            ["星标"] = 2,
        };
        var groups = new List<TagGroup>
        {
            Group("内容", "风景", "街拍"),
            Group("英文", "SCENERY"),
        };

        var undefined = TagProjection.ComputeUndefinedTags(counts, groups);

        Assert.Equal(
            [Entry("美食", 7), Entry("星标", 2)],
            undefined);
    }

    [Fact]
    public void T_PR_02_CountPreservedAndOrdered()
    {
        // 计数保留计数键值；排序 = 计数降序（C 9 在前）、同计数按名 Ordinal 升序（A < B，大小写敏感）、
        // 计数最小垫底（d 1）。
        var counts = new Dictionary<string, int>
        {
            ["B"] = 3,
            ["A"] = 3,
            ["C"] = 9,
            ["d"] = 1,
        };

        var undefined = TagProjection.ComputeUndefinedTags(counts, []);

        Assert.Equal(
            [Entry("C", 9), Entry("A", 3), Entry("B", 3), Entry("d", 1)],
            undefined);
    }

    [Fact]
    public void T_PR_03_AllConfiguredOrEmptyCounts_YieldsEmpty()
    {
        // 全部标签已配置（含跨组 + 大小写变体命中）→ 空列表；counts 空 → 空列表（E1 未定义区空态数据层，
        // 上层据此整区隐藏）。
        var groups = new List<TagGroup> { Group("内容", "风景"), Group("英文", "SCENERY") };

        var allConfigured = new Dictionary<string, int>
        {
            ["风景"] = 5,
            ["Scenery"] = 3,
        };
        Assert.Empty(TagProjection.ComputeUndefinedTags(allConfigured, groups));

        Assert.Empty(TagProjection.ComputeUndefinedTags(new Dictionary<string, int>(), groups));
    }

    [Fact]
    public void T_PR_04_SpellingTakesCountKey()
    {
        // 拼写口径（D12）：显示拼写取计数键（文件名侧聚合拼写）——「TRIP」是配置「Trip」的大小写变体被排除，
        // 未配置的「tripTokyo」原样保留：不被配置侧拼写「纠正」，也不做任何大小写规范化。
        var counts = new Dictionary<string, int>
        {
            ["tripTokyo"] = 2,
            ["TRIP"] = 4,
        };
        var groups = new List<TagGroup> { Group("旅行", "Trip") };

        var undefined = TagProjection.ComputeUndefinedTags(counts, groups);

        var entry = Assert.Single(undefined);
        Assert.Equal("tripTokyo", entry.Name);   // Ordinal 精确拼写（xunit 字符串相等按 Ordinal）
        Assert.Equal(2, entry.Count);
    }

    [Fact]
    public void T_PR_05_UnionWithSelectionCounts()
    {
        // 选中集并集 + 各标签选中集内计数（计数 = 含该标签的图片数，非出现总次数）：
        // 30 张中 18 张含「Z」→「Z 18」；12 张含「风景」；6 张含「美食」（与 Z 共存不重复计 Z）。
        var sequences = new List<IReadOnlyList<string>>();
        for (var i = 0; i < 12; i++)
        {
            sequences.Add(["Z"]);           // 12 张仅含 Z
        }

        for (var i = 0; i < 6; i++)
        {
            sequences.Add(["Z", "美食"]);    // 6 张 Z + 美食 → Z 计满 18
        }

        for (var i = 0; i < 12; i++)
        {
            sequences.Add(["风景"]);         // 12 张不含 Z
        }

        var union = TagProjection.ComputeSelectionTagUnion(sequences);

        Assert.Equal(
            [Entry("Z", 18), Entry("风景", 12), Entry("美食", 6)],
            union);
    }

    [Fact]
    public void T_PR_06_UnionKeepsTagsOutsideConfigGroups()
    {
        // 并集不做配置过滤：配置组外标签（未定义标签）照常进并集（C2 数据层——右栏可见未定义标签并可直接
        // 批量移除）；空标签列表的图片不贡献；同计数按 Ordinal（"Trip" < "自定义标记"）。
        var union = TagProjection.ComputeSelectionTagUnion([["Trip"], ["自定义标记"], []]);

        Assert.Equal(
            [Entry("Trip", 1), Entry("自定义标记", 1)],
            union);
    }

    [Fact]
    public void T_PR_07_EmptySelection_YieldsEmptyUnion()
    {
        // 空选中（无任何图片）→ 空并集；全部图片标签列表为空 → 同样空并集（右栏空态数据层）。
        Assert.Empty(TagProjection.ComputeSelectionTagUnion([]));

        var allUntagged = new List<IReadOnlyList<string>> { [], [] };
        Assert.Empty(TagProjection.ComputeSelectionTagUnion(allUntagged));
    }

    [Fact]
    public void T_PR_08_UnionCaseInsensitiveMergeFirstSeenSpelling()
    {
        // 并集大小写合并口径：OrdinalIgnoreCase 同名合并计数，拼写取首见——
        // 「Night」「night」「NIGHT」合并为一条计 3、拼写「Night」；「z」「Z」合并计 2、拼写「z」（首见小写）。
        var union = TagProjection.ComputeSelectionTagUnion(
        [
            ["Night", "z"],
            ["night", "Z"],
            ["NIGHT"],
        ]);

        Assert.Equal(
            [Entry("Night", 3), Entry("z", 2)],
            union);
    }

    /// <summary>构造标签组（测试速记）：组名 + 组内标签名（Id 留空，投影只消费 Name）。</summary>
    private static TagGroup Group(string name, params string[] tagNames)
        => new() { Name = name, Tags = [.. tagNames.Select(tag => new TagDefinition { Name = tag })] };

    /// <summary>构造标签计数项（测试速记）。</summary>
    private static TagCountEntry Entry(string name, int count)
        => new() { Name = name, Count = count };
}
