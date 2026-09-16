namespace SimpleViewer.Services;

/// <summary>
/// TagSpaces 文件名标签协议（base[t1 t2].ext）的解析/合成/校验服务。
/// 事实源永远是文件名本身：仅基名尾部的方括号段视为标签集合，标签以空格分隔。
/// </summary>
public interface ITagFilenameService
{
    /// <summary>
    /// 尝试解析文件名（不含目录）中的尾部标签段。
    /// 仅识别基名末尾的方括号段；非尾部方括号（如 a[b]c.jpg）不视为标签，原样保留在基名中。
    /// 段内标签以空白字符分隔，多重空白（含全角空格/nbsp）归一为单一边界。
    /// </summary>
    /// <param name="fileName">文件名，可含扩展名。</param>
    /// <param name="baseName">输出：不含标签段的基名。</param>
    /// <param name="extension">输出：扩展名（含前导点，保留原大小写；无扩展名时为空串）。</param>
    /// <param name="tags">输出：标签列表（保序，无标签时为空列表）。</param>
    /// <returns>文件名非空可解析返回 true；文件名为空返回 false（输出参数置空值）。</returns>
    bool TryParse(string? fileName, out string baseName, out string extension, out IReadOnlyList<string> tags);

    /// <summary>
    /// 合成文件名：Tags 为空时 baseName + extension；否则 baseName[t1 t2] + extension。
    /// 与 <see cref="TryParse"/> 往返一致。标签未通过 <see cref="ValidateTagName"/> 时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    string Compose(string baseName, string extension, IReadOnlyList<string> tags);

    /// <summary>
    /// 校验标签名合法性：拒绝空、含任何空白字符（char.IsWhiteSpace 全集，含全角空格与 nbsp）、含方括号。
    /// </summary>
    /// <returns>合法返回 true。</returns>
    bool ValidateTagName(string? name);

    /// <summary>
    /// 依据原全路径与新标签集合构建重命名目标全路径（纯预检，不执行任何文件系统变更）。
    /// 预检项：标签合法性、新路径长度不超过 260 字符（D11 双保险）、目标文件名冲突。
    /// 可预期失败一律返回明确原因，不抛异常；源文件存在性由调用方（TagService）保证。
    /// </summary>
    TagFilenamePathResult BuildNewPath(string oldFullPath, IReadOnlyList<string> newTags);
}

/// <summary>
/// <see cref="ITagFilenameService.BuildNewPath"/> 的结果：成功携带新全路径；失败携带用户可读原因。
/// </summary>
/// <param name="Success">是否构建成功。</param>
/// <param name="NewFullPath">成功时的目标全路径；失败时为 null。</param>
/// <param name="Error">失败时的用户可读原因；成功时为 null。</param>
public sealed record TagFilenamePathResult(bool Success, string? NewFullPath, string? Error)
{
    /// <summary>构建成功结果。</summary>
    public static TagFilenamePathResult Ok(string newFullPath) => new(true, newFullPath, null);

    /// <summary>构建失败结果（携带用户可读原因）。</summary>
    public static TagFilenamePathResult Fail(string error) => new(false, null, error);
}
