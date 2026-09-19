using System.IO;
using SimpleViewer.Models;

// Responsibility: 标签打标/移除/重命名/删除的落盘执行（同目录 File.Move 重命名），互斥语义由 TagSemantics 纯函数承担。
// Invariants: 一律经 TagFilenameService.BuildNewPath 预检（260 长度/目标冲突/标签名合法）后再 File.Move，绝不复用
//             FileOperationService.MoveToFolder 的"同名先删后移"覆盖语义（D10）；失败逐文件聚合、成功不回滚；
//             幂等命中（新路径与原路径按 OrdinalIgnoreCase 相同——Windows 文件系统大小写不敏感口径）不执行任何实际 IO。
// Call chain: MainViewModel/快捷键分派（Step 10/12）→ ApplyTagAsync/RemoveTagAsync/RenameTagAsync/DeleteTagAsync/DeleteGroupAsync
//             → RenameAll 统一管线 → TagSemantics.Apply → TagFilenameService.BuildNewPath → File.Move。

namespace SimpleViewer.Services;

/// <inheritdoc cref="ITagService" />
public sealed class TagService : ITagService
{
    private readonly ITagFilenameService _tagFilenameService;
    private readonly Func<string, bool>? _isTagReferencedByBindings;

    /// <summary>
    /// 构造标签操作服务。
    /// </summary>
    /// <param name="tagFilenameService">文件名标签协议服务（解析/合成/预检）。</param>
    /// <param name="isTagReferencedByBindings">
    /// "标签 Id 是否被快捷键绑定引用"谓词（入参为 <see cref="TagDefinition.Id"/>，与 Step 12 的
    /// ShortcutBinding.TagId 契约对齐）；null 视为未引用（Step 12 之前的默认接线）。
    /// </param>
    public TagService(ITagFilenameService tagFilenameService, Func<string, bool>? isTagReferencedByBindings = null)
    {
        _tagFilenameService = tagFilenameService ?? throw new ArgumentNullException(nameof(tagFilenameService));
        _isTagReferencedByBindings = isTagReferencedByBindings;
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> ApplyTagAsync(IReadOnlyList<string> paths, TagDefinition tag, TagGroup group)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(group);

        // 批量重命名走线程池，避免大批量操作阻塞 UI 调用线程。
        return Task.Run(() => RenameAll(paths, currentTags => TagSemantics.Apply(currentTags, group, tag.Name)));
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> RemoveTagAsync(IReadOnlyList<string> paths, string tagName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tagName);

        return Task.Run(() => RenameAll(paths, currentTags => RemoveTagCore(currentTags, tagName)));
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> RenameTagAsync(IReadOnlyList<string> paths, string oldTagName, string newTagName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(oldTagName);
        ArgumentNullException.ThrowIfNull(newTagName);

        // 不含旧标签名的文件新集合不变 → 落入无操作跳过；重命名口径下不计成功
        //（"成功数"应反映被实际更新的文件数，全库候选中未携带旧标签的文件静默跳过）。
        // 注意：仅大小写差异的改名（如 a → A）按 Windows 大小写不敏感口径落入幂等跳过（RenameAll 统一口径）。
        return Task.Run(() => RenameAll(
            paths,
            currentTags => ReplaceTagCore(currentTags, oldTagName, newTagName),
            countNoOpAsSucceeded: false));
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> DeleteTagAsync(IReadOnlyList<string> paths, TagDefinition tag)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tag);

        // 前置校验（spec T-ST5）：被快捷键绑定引用的标签整体拒绝删除，不触碰任何文件。
        if (_isTagReferencedByBindings?.Invoke(tag.Id) == true)
        {
            return Task.FromResult(BatchOperationResult.Reject(
                $"标签“{tag.Name}”被快捷键绑定引用，请先修改或移除相关绑定再删除。"));
        }

        return Task.Run(() => RenameAll(paths, currentTags => RemoveTagCore(currentTags, tag.Name)));
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> DeleteGroupAsync(IReadOnlyList<string> paths, TagGroup group)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(group);

        // 级联移除组内全部标签；以执行时组的标签名单为准（快照语义，避免迭代中被外部修改）。
        var groupTagNames = TagSemantics.GetTagNames(group);
        return Task.Run(() => RenameAll(
            paths,
            currentTags => currentTags.Where(t => !groupTagNames.Contains(t)).ToArray()));
    }

    /// <summary>
    /// 统一重命名管线：解析当前标签 → 计算新标签集合 → BuildNewPath 预检 → 幂等跳过或 File.Move 落盘。
    /// 可预期失败（占用/冲突/超长/名称非法/路径为空）逐文件聚合进 Failures，成功项不回滚。
    /// </summary>
    /// <param name="paths">候选文件全路径集合。</param>
    /// <param name="computeNewTags">由当前标签集合计算新标签集合的纯函数（由各操作注入语义）。</param>
    /// <param name="countNoOpAsSucceeded">
    /// 新路径与原路径相同（OrdinalIgnoreCase，Windows 大小写不敏感口径：文件已处于目标状态、未执行实际 IO）
    /// 时是否计入成功数：打标/移除/删除等"目标状态"语义计成功（true，缺省）；全库重命名等"实际更新"语义不计（false）。
    /// </param>
    private BatchOperationResult RenameAll(
        IEnumerable<string> paths,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> computeNewTags,
        bool countNoOpAsSucceeded = true)
    {
        var succeeded = 0;
        var failures = new List<TagOperationFailure>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add(new TagOperationFailure(path ?? string.Empty, "文件路径不能为空。"));
                continue;
            }

            if (!_tagFilenameService.TryParse(Path.GetFileName(path), out _, out _, out var currentTags))
            {
                failures.Add(new TagOperationFailure(path, "无法解析原文件名。"));
                continue;
            }

            var newTags = computeNewTags(currentTags);
            var build = _tagFilenameService.BuildNewPath(path, newTags);
            if (!build.Success)
            {
                failures.Add(new TagOperationFailure(path, build.Error ?? "无法构建新路径。"));
                continue;
            }

            // 幂等命中：新路径与原路径相同即视为无操作，不执行任何 IO。比较口径 OrdinalIgnoreCase
            //（Windows 文件系统大小写不敏感）——与同步侧 TryComposeNewPath 的幂等判定统一
            //（2026-09-19 口径修复：旧 Ordinal 口径下"仅大小写差异的重命名"会真实执行 File.Move，
            // 而同步侧按 IgnoreCase 判幂等跳过，造成磁盘已改、内存/索引停留旧路径的分裂）。
            if (string.Equals(build.NewFullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                if (countNoOpAsSucceeded)
                {
                    succeeded++;
                }

                continue;
            }

            try
            {
                File.Move(path, build.NewFullPath!);
                succeeded++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 覆盖占用（文件被其他进程/本应用句柄锁定）、源文件不存在、权限不足等；
                // 目标冲突与超长已由 BuildNewPath 前置拦截，此处为 TOCTOU 兜底。
                failures.Add(new TagOperationFailure(path, $"重命名失败：{ex.Message}"));
            }
        }

        return new BatchOperationResult(succeeded, failures);
    }

    /// <summary>从标签集合移除指定标签名的全部出现（大小写不敏感，与标签重名拒绝口径一致）。</summary>
    private static IReadOnlyList<string> RemoveTagCore(IReadOnlyList<string> currentTags, string tagName)
        => currentTags.Where(t => !string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>将旧标签名替换为新标签名（保持原位置）；集合不含旧标签名时原样返回（落入幂等跳过）。</summary>
    private static IReadOnlyList<string> ReplaceTagCore(IReadOnlyList<string> currentTags, string oldTagName, string newTagName)
    {
        if (!currentTags.Contains(oldTagName, StringComparer.OrdinalIgnoreCase))
        {
            return currentTags;
        }

        var result = new List<string>(currentTags.Count);
        foreach (var tag in currentTags)
        {
            if (string.Equals(tag, oldTagName, StringComparison.OrdinalIgnoreCase))
            {
                // 文件名标签为平铺字符串正常不重复；防御性保证替换名仅出现一次。
                if (!result.Contains(newTagName, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(newTagName);
                }
            }
            else
            {
                result.Add(tag);
            }
        }

        return result;
    }
}
