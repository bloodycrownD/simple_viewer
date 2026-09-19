using SimpleViewer.Helpers;
using SimpleViewer.Models;

namespace SimpleViewer.Tests;

/// <summary>
/// 自然排序双实现 parity 测试（cr/P1-8）：
/// <see cref="GalleryItemNaturalComparer"/>（预分词 SortKey，决定瀑布流序）与
/// <see cref="NaturalStringComparer"/>（逐次 Tokenize，决定单图翻页目录序）是同语义的
/// 独立双实现（D9 决策保留），此前无任何测试锁定行为一致——任一侧 Tokenize/比较逻辑
/// 改动将静默分叉（瀑布流序 ≠ 翻页序）。本测试锁定两侧对同一输入的成对比较符号与
/// 全量排序结果完全一致；实现代码不动，仅补测试。
/// </summary>
public class NaturalSortParityTests
{
    /// <summary>用 Models 侧分词逻辑构造的字符串比较器（对齐 GalleryItem.SortKey 的消费方式）。</summary>
    private static readonly IComparer<string> KeyBasedComparer =
        Comparer<string>.Create((a, b) => GalleryItemNaturalComparer.CompareKeys(
            GalleryItemNaturalComparer.Tokenize(a),
            GalleryItemNaturalComparer.Tokenize(b)));

    /// <summary>
    /// parity 输入组：经典数值段（img1/img2/img10/img20）、前导零（a02/a2 数值相等）、
    /// int 溢出段（9999999999 超上限回退字符串序）、数字/非数字混合段、大小写
    /// （OrdinalIgnoreCase）、空串边界。
    /// </summary>
    public static TheoryData<string[]> ParityCases => new()
    {
        new[] { "img1", "img2", "img10", "img20" },
        new[] { "img20", "img10", "img2", "img1", "img100" },
        new[] { "a02", "a2", "a010", "a10" },
        new[] { "9999999999", "99999999998", "10000000000", "1000000000" },
        new[] { "img1a", "img1b", "a1b2", "a12b", "x1y10z2", "x1y9z2" },
        new[] { "IMG2", "img2", "Img10", "img1" },
        new[] { "a", "", "0", "00" },
    };

    /// <summary>
    /// parity：两侧比较器对同一组输入的成对比较符号一致（含相等 = 0），
    /// 且各自全量排序后的序列完全一致（List.Sort 确定性算法，相等元素在相同输入上落位相同）。
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityCases))]
    public void T_Parity_01_BothComparersAgree(string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            for (var j = 0; j < names.Length; j++)
            {
                Assert.Equal(
                    Math.Sign(KeyBasedComparer.Compare(names[i], names[j])),
                    Math.Sign(NaturalStringComparer.Instance.Compare(names[i], names[j])));
            }
        }

        var byKeys = names.ToList();
        byKeys.Sort(KeyBasedComparer);
        var byNatural = names.ToList();
        byNatural.Sort(NaturalStringComparer.Instance);
        Assert.Equal(byNatural, byKeys);
    }

    /// <summary>
    /// 瀑布流真实链路 parity：GalleryItem.SortKey（Tokenize 预分词）+
    /// <see cref="GalleryItemNaturalComparer"/> 实例排序，与 <see cref="NaturalStringComparer"/>
    /// 对同组显示名排序的结果完全一致（溢出/前导零/混合段/大小写混合输入）。
    /// </summary>
    [Fact]
    public void T_Parity_02_GalleryItemComparerMatchesNaturalStringComparer()
    {
        string[] names = ["b2", "b10", "a02", "a2", "9999999999", "x1y10z2", "V2final", "v10final"];
        var items = names
            .Select(n => new GalleryItem
            {
                Path = n,
                BaseName = n,
                SortKey = GalleryItemNaturalComparer.Tokenize(n),
            })
            .ToList();

        var byItems = items.ToList();
        byItems.Sort(GalleryItemNaturalComparer.Instance);
        var byNatural = names.ToList();
        byNatural.Sort(NaturalStringComparer.Instance);

        Assert.Equal(byNatural, byItems.Select(static i => i.BaseName).ToList());
    }
}
