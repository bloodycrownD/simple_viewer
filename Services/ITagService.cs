using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// 标签操作服务：打标/移除/重命名标签均以同目录重命名文件落盘（TagSpaces 文件名标签协议），
/// 含互斥语义 enforcement 与批量失败聚合。所有操作一律经
/// <see cref="ITagFilenameService.BuildNewPath"/> 预检（260 长度/目标冲突/标签名合法）后再 <see cref="File.Move"/>，
/// 绝不复用 FileOperationService.MoveToFolder 的"同名先删后移"覆盖语义（决策 D10）。
/// 删除标签/组不在此列：batch-tag-management Step 2 起删除纯化为配置操作（MainViewModel，0 文件改名）。
/// </summary>
/// <remarks>
/// 服务自身不扫描磁盘：所有方法的候选文件集合（paths）由调用方给出（如瀑布流选中集、全库路径集合），
/// 保持纯操作语义。互斥语义以执行时该组的 <see cref="TagGroup.Exclusive"/> 属性为准，
/// 组属性切换不追溯已落盘标签。
/// </remarks>
public interface ITagService
{
    /// <summary>
    /// 批量打标：互斥组先剔除同组内已有标签再追加（组外/未分组标签一律保留）；
    /// 兼容组（非互斥组）直接追加（重复打同一标签为幂等命中，文件名不变）。
    /// </summary>
    /// <param name="paths">候选文件全路径集合。</param>
    /// <param name="tag">要打上的标签。</param>
    /// <param name="group">标签所属组，提供互斥上下文（组内标签名单）。</param>
    Task<BatchOperationResult> ApplyTagAsync(IReadOnlyList<string> paths, TagDefinition tag, TagGroup group);

    /// <summary>
    /// 批量移除：从每个文件名中移除指定标签名（文件不含该标签时为幂等命中）。
    /// </summary>
    /// <param name="paths">候选文件全路径集合。</param>
    /// <param name="tagName">要移除的标签名。</param>
    Task<BatchOperationResult> RemoveTagAsync(IReadOnlyList<string> paths, string tagName);

    /// <summary>
    /// 全库重命名标签：对候选集合中含旧标签名的文件将其替换为新标签名（位置保持）；
    /// 不含旧标签名的文件跳过（不计成功也不计失败——成功数仅反映被实际更新的文件）。
    /// 候选集由调用方负责给出（服务不扫描磁盘）。
    /// </summary>
    /// <param name="paths">候选文件全路径集合（应包含可能携带该标签的文件）。</param>
    /// <param name="oldTagName">旧标签名。</param>
    /// <param name="newTagName">新标签名（合法性由重命名管线校验，非法时逐文件聚合失败原因）。</param>
    Task<BatchOperationResult> RenameTagAsync(IReadOnlyList<string> paths, string oldTagName, string newTagName);
}

/// <summary>
/// 互斥语义纯函数：给定当前标签集合、目标组与要打的标签，计算新标签集合。无副作用，独立可测（Step 3 决策 D10）。
/// </summary>
public static class TagSemantics
{
    /// <summary>
    /// 计算打标后的新标签集合：
    /// 互斥组——先剔除同组内已有标签（大小写不敏感）再追加目标标签，组外/未分组标签一律保留；
    /// 兼容组（非互斥组）——全部保留后追加目标标签（已存在同名标签时不重复追加，幂等）。
    /// </summary>
    /// <param name="currentTags">当前标签集合（通常来自文件名解析，保序）。</param>
    /// <param name="group">目标组（提供互斥属性与组内标签名单）。</param>
    /// <param name="tagName">要打的标签名。</param>
    /// <returns>新标签集合（原顺序保留，目标标签追加在尾部）。</returns>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> currentTags, TagGroup group, string tagName)
    {
        ArgumentNullException.ThrowIfNull(currentTags);
        ArgumentNullException.ThrowIfNull(group);

        List<string> result;
        if (group.Exclusive)
        {
            // 互斥组：仅剔除同组标签，组外/未分组标签原样保留。
            var groupTagNames = GetTagNames(group);
            result = currentTags.Where(t => !groupTagNames.Contains(t)).ToList();
            result.Add(tagName);
        }
        else
        {
            // 兼容组（非互斥组）：叠加；重复打同一标签不重复追加（幂等）。
            result = currentTags.ToList();
            if (!result.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(tagName);
            }
        }

        return result;
    }

    /// <summary>
    /// 组内非空标签名集合（大小写不敏感，与 SettingsService 标签重名拒绝口径一致：同名标签仅可存在一处）。
    /// </summary>
    internal static HashSet<string> GetTagNames(TagGroup group)
        => new(
            group.Tags.Select(static t => t.Name).Where(static n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);
}
