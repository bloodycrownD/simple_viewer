// Responsibility: 图库扫描项模型——路径/目录/基名/扩展名/标签列表/宽高/文件大小/预分词自然排序 key。
// Invariants: 项不可变；DisplayName = 基名 + 扩展名（已剥离尾部标签段）；宽高未知为 0 且 AspectRatio 回退 1:1；SortKey 一次性分配后不再重算（D9）。
// Call chain: LibraryScanService（Step 4 扫描构建）→ Step 5 索引 / Step 8 瀑布流消费。

using System.Globalization;

namespace SimpleViewer.Models;

/// <summary>
/// 图库扫描产出的单个图片项（不可变）。
/// 宽高来自文件头部快速读取（不整文件解码），读取失败时为 0 并由 <see cref="AspectRatio"/> 回退 1:1。
/// </summary>
public sealed class GalleryItem
{
    /// <summary>文件全路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>所在目录全路径（无尾随分隔符）。</summary>
    public string DirectoryName { get; init; } = string.Empty;

    /// <summary>基名（已剥离尾部标签段，不含扩展名）。</summary>
    public string BaseName { get; init; } = string.Empty;

    /// <summary>扩展名（含前导点，保留原大小写；无扩展名时为空串）。</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>标签列表（来自文件名尾部方括号段，保序；无标签为空列表）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>像素宽（头部快速读取；未知为 0）。</summary>
    public int Width { get; init; }

    /// <summary>像素高（头部快速读取；未知为 0）。</summary>
    public int Height { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>预分词自然排序 key（基于显示名一次性分配；打标重命名不改变显示名，故行序稳定，D15）。</summary>
    public IReadOnlyList<string> SortKey { get; init; } = Array.Empty<string>();

    /// <summary>显示名 = 基名 + 扩展名（剥离标签段，供瀑布流卡片与单图标题展示）。</summary>
    public string DisplayName => BaseName + Extension;

    /// <summary>宽高比（宽/高）；宽高未知（任一为 0）时回退 1:1（D9）。</summary>
    public double AspectRatio => Width > 0 && Height > 0 ? (double)Width / Height : 1.0;
}

/// <summary>
/// 基于预分词 <see cref="GalleryItem.SortKey"/> 的自然序比较器。
/// 语义对齐 <c>Helpers.NaturalStringComparer</c>（数字段按数值比较、其余段 OrdinalIgnoreCase、段数少者小），
/// 但消费预分词 key，规避每次 Compare 双侧重新 Tokenize 的分配开销（D9；该比较器本身保持不动，供单图目录场景继续使用）。
/// </summary>
public sealed class GalleryItemNaturalComparer : IComparer<GalleryItem>
{
    /// <summary>单例（无状态比较器）。</summary>
    public static GalleryItemNaturalComparer Instance { get; } = new();

    private GalleryItemNaturalComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(GalleryItem? x, GalleryItem? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        return CompareKeys(x.SortKey, y.SortKey);
    }

    /// <summary>逐 token 比较：双侧可解析为 int 时按数值比较，否则 OrdinalIgnoreCase；前缀相同时段数少者小。</summary>
    public static int CompareKeys(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var count = Math.Min(left.Count, right.Count);
        for (var i = 0; i < count; i++)
        {
            var leftToken = left[i];
            var rightToken = right[i];
            var leftIsNumber = int.TryParse(leftToken, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = int.TryParse(rightToken, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            int result;
            if (leftIsNumber && rightIsNumber)
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                result = string.Compare(leftToken, rightToken, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    /// <summary>
    /// 按数字/非数字边界一次性切词（与 <c>NaturalStringComparer.Tokenize</c> 语义一致；独立实现以免改动该比较器）。
    /// </summary>
    public static string[] Tokenize(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var parts = new List<string>();
        var start = 0;
        while (start < value.Length)
        {
            var isDigit = char.IsDigit(value[start]);
            var end = start + 1;
            while (end < value.Length && char.IsDigit(value[end]) == isDigit)
            {
                end++;
            }

            parts.Add(value[start..end].ToString());
            start = end;
        }

        return parts.ToArray();
    }
}
