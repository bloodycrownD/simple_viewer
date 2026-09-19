// 职责：标签筛选状态机（纯函数，cr/P2-3 从 MainViewModel 下沉 Core）——切换语义与匹配谓词，
//       供 MainViewModel 薄包装调用与单元测试直接锁定（侧栏 / 工具栏 / 快捷键三入口共用同一状态）。
// 不变量：切换语义与原 MainViewModel 实现完全一致（零行为变化，仅实现位置移动）——
//         Ctrl = 加/减选 toggle（在集中移除、不在则加入，多标签 OR 的逐标签开关）；
//         无修饰且当前唯一选中就是该标签 = 取消筛选回全量（保留"二次点击取消"习惯）；
//         无修饰其余情况 = 单选重置（多选集或不同标签都替换为仅该标签）；
//         进入标签筛选即退出无标签模式（互斥）；激活无标签筛选即清空标签集（互斥清位）；
//         集合成员与比较均为 OrdinalIgnoreCase（标签大小写不敏感，与索引/文件名协议一致）；
//         Matches：无标签态 = 项无任何标签；否则 = 项的任一标签命中激活集（OR）。
// 调用链：MainViewModel.ToggleTagFilterAsync / ToggleUntaggedFilterAsync / MatchesTagFilter → 本类；
//         tests/SimpleViewer.Tests/TagFilterStateTests.cs 直接锁定语义。

namespace SimpleViewer.Services;

/// <summary>
/// 标签筛选状态机（纯函数，cr/P2-3）：切换结果以（标签集，无标签位）二元组返回，
/// 调用方负责写回自己的可变状态并触发后续刷新（索引查询 / 瀑布流整体替换）。
/// </summary>
public static class TagFilterState
{
    /// <summary>
    /// 切换标签筛选：
    /// <paramref name="ctrl"/> = 加/减选（在集中移除、不在则加入——多标签 OR 的逐标签 toggle）；
    /// 无修饰且当前唯一选中就是 <paramref name="tagName"/> = 取消筛选回全量；
    /// 无修饰其余情况（多选集或不同标签）= 单选重置（清空后仅保留该标签）。
    /// 任何分支都退出无标签模式（互斥清位——无标签激活时标签集恒空，必走替换/添加分支）。
    /// 比较口径 OrdinalIgnoreCase（与原 HashSet 实现一致）。
    /// </summary>
    public static (IReadOnlyList<string> Tags, bool Untagged) Toggle(
        IEnumerable<string>? current,
        bool untagged,
        string tagName,
        bool ctrl)
    {
        var tags = new List<string>(current ?? []);

        if (ctrl)
        {
            // Ctrl：加/减选。
            if (!RemoveOrdinalIgnoreCase(tags, tagName))
            {
                tags.Add(tagName);
            }
        }
        else if (tags.Count == 1 && string.Equals(tags[0], tagName, StringComparison.OrdinalIgnoreCase))
        {
            // 无修饰且当前唯一选中就是它：取消筛选回全量。
            tags.Clear();
        }
        else
        {
            // 无修饰其余情况：单选重置。
            tags.Clear();
            tags.Add(tagName);
        }

        return (tags, Untagged: false);
    }

    /// <summary>
    /// 切换「无标签」筛选（untagged-filter-entry）：
    /// 激活 = 清空标签筛选（互斥清集）并只显示无任何标签的图片；再点取消回全量（标签集原样保留——
    /// 无标签激活期间标签集恒空，保留与清空等价，但取消语义不隐式改集合）。
    /// </summary>
    public static (IReadOnlyList<string> Tags, bool Untagged) ToggleUntagged(
        IEnumerable<string>? current,
        bool untagged)
        => untagged
            ? ([.. (current ?? [])], Untagged: false)
            : ([], Untagged: true);

    /// <summary>
    /// 筛选匹配谓词（瀑布流追加块过滤与索引查询后二次过滤共用口径）：
    /// 无标签态 = <paramref name="itemTags"/> 无任何标签；否则 = 项的任一标签命中激活集（OR）。
    /// 无标签位未激活且激活集为空时恒不命中（无筛选态不走本谓词，走全量路径）。
    /// </summary>
    public static bool Matches(
        IEnumerable<string>? itemTags,
        IEnumerable<string>? filterTags,
        bool untagged)
    {
        if (untagged)
        {
            return itemTags is null || !itemTags.Any();
        }

        var tags = itemTags ?? [];
        return filterTags?.Any(active => tags.Any(tag =>
            string.Equals(tag, active, StringComparison.OrdinalIgnoreCase))) == true;
    }

    /// <summary>按 OrdinalIgnoreCase 从列表移除首个等值项（模拟原 HashSet.Remove 语义）。</summary>
    private static bool RemoveOrdinalIgnoreCase(List<string> tags, string tagName)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i], tagName, StringComparison.OrdinalIgnoreCase))
            {
                tags.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
