// 职责：标签投影纯函数（纯 POCO + 静态函数，Core 可测）——未定义标签集合投影（索引计数键集 − 配置组
//       标签集）与选中集标签并集（每图标签列表 → 并集 + 选中集内计数）。
//       出处：batch-tag-management spec Step 1（拍板 D1 下沉 Core / D12 拼写与大小写口径），
//       测试用例 T_PR_01~08（docs\iterations\图片打标签与瀑布流浏览\features\batch-tag-management\spec.md）。
// 不变量：两函数均为纯静态、无副作用、不修改入参；
//         全链标签比较 OrdinalIgnoreCase（与 TagFilterState / 索引 / 文件名协议同口径，spec D12）；
//         显示拼写一律取事实源侧——未定义集合取计数键拼写（文件名聚合侧）、并集取首见拼写
//         （GalleryItem.Tags 原文），均不取配置侧拼写、不做大小写规范化；
//         空串标签不贡献（文件名协议侧不存在，防御性跳过）；
//         排序统一：计数降序，同计数按名 StringComparer.Ordinal 升序（未定义区 / 右栏 chip 展示序）。
// 调用链：TagSidebarViewModel.Rebuild（Step 3 接未定义集合）与 MainViewModel.RecomputeSelectionTagUnion
//         （Step 4 接并集）消费；tests/SimpleViewer.Tests/TagProjectionTests.cs 直接锁定；
//         Core 边界：纯 POCO + 静态函数，禁 ObservableObject / WinUI 类型（Core csproj 无这些包）。

using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>标签计数项：未定义集合与选中集并集共用的结果元素（chip 展示 = 标签名 + 计数）。</summary>
public sealed record TagCountEntry
{
    /// <summary>标签显示拼写：未定义集合取计数键拼写（文件名侧）、并集取首见拼写（spec D12）。</summary>
    public required string Name { get; init; }

    /// <summary>计数：未定义集合 = 索引全表聚合计数；并集 = 选中集内含该标签的图片数。</summary>
    public required int Count { get; init; }
}

/// <summary>
/// 标签投影（纯静态函数）：未定义标签集合与选中集标签并集的 Core 可测计算。
/// 服务层（索引 / TagService）零改动的关键——两条投影都只消费既有内存数据
/// （索引计数聚合字典 / 选中集卡片标签枚举），不做 IO。
/// </summary>
public static class TagProjection
{
    /// <summary>
    /// 未定义标签集合 = tagCounts 键集 − configGroups 全部组内标签名集（多组联合，
    /// OrdinalIgnoreCase 差集——配置组标签的大小写变体正确排除）。
    /// 每项含标签名与计数：计数保留计数键值，显示拼写取计数键拼写（文件名侧，不取配置侧——spec D12）。
    /// 排序：计数降序，同计数按名 Ordinal 升序。空输入 / 全部已配置 → 空列表（E1 空态数据层）。
    /// </summary>
    public static List<TagCountEntry> ComputeUndefinedTags(
        IReadOnlyDictionary<string, int> tagCounts,
        IReadOnlyList<TagGroup> configGroups)
    {
        // 配置组标签名集（OrdinalIgnoreCase）：差集排除基准（对齐 TagSidebarViewModel 既有 configuredNames 口径）。
        var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in configGroups)
        {
            foreach (var tag in group.Tags ?? [])
            {
                var tagName = tag?.Name;
                if (string.IsNullOrEmpty(tagName))
                {
                    continue;   // 空名标签不贡献排除集（残缺定义防御）。
                }

                configuredNames.Add(tagName);
            }
        }

        var entries = new List<TagCountEntry>();
        foreach (var (name, count) in tagCounts)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;   // 空串标签不贡献（文件名协议侧不存在，防御性跳过）。
            }

            if (configuredNames.Contains(name))
            {
                continue;   // 已配置（含大小写变体）→ 不属未定义。
            }

            entries.Add(new TagCountEntry { Name = name, Count = count });   // 拼写取计数键（文件名侧）。
        }

        return SortEntries(entries);
    }

    /// <summary>
    /// 选中集标签并集：tagSequences 每个元素是一张图的标签列表。OrdinalIgnoreCase 同名合并计数、
    /// 拼写取首见（GalleryItem.Tags 原文——spec D12）；计数 = 选中集内含该标签的图片数（非出现总次数）。
    /// 不滤除配置组外标签（未定义标签照常进并集——C2 数据层）。
    /// 排序：计数降序，同计数按名 Ordinal 升序。空序列 / 全空列表 → 空列表。
    /// </summary>
    public static List<TagCountEntry> ComputeSelectionTagUnion(
        IEnumerable<IReadOnlyList<string>> tagSequences)
    {
        // 键 = 首见拼写，比较器 OrdinalIgnoreCase：后续大小写变体命中同键只增计数，键拼写保持首见。
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tags in tagSequences)
        {
            if (tags is null)
            {
                continue;   // 空图片标签列表跳过（防御）。
            }

            foreach (var tag in tags)
            {
                if (string.IsNullOrEmpty(tag))
                {
                    continue;   // 空串标签不贡献（文件名协议侧不存在，防御性跳过）。
                }

                // indexer 按比较器定位：命中同键只更新值，键（首见拼写）不变。
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        var entries = new List<TagCountEntry>(counts.Count);
        foreach (var (name, count) in counts)
        {
            entries.Add(new TagCountEntry { Name = name, Count = count });
        }

        return SortEntries(entries);
    }

    /// <summary>统一展示排序：计数降序，同计数按名 StringComparer.Ordinal 升序。</summary>
    private static List<TagCountEntry> SortEntries(List<TagCountEntry> entries)
        => [.. entries
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)];
}
