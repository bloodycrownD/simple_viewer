using System.Text;

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
    /// 预检项：标签合法性、新文件名组件不超过 Linux 兼容上限 255 UTF-8 字节（最严格平台口径）、
    /// 新路径长度不超过 260 字符（D11 双保险）、目标文件名冲突。
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

/// <summary>
/// 文件名标签预算纯函数（参照 <c>TagSemantics</c> 惯例：静态类、无副作用、独立可测）。
/// 跨平台按最严格平台预检——Linux（ext4 等）单文件名组件 ≤ 255 UTF-8 字节：UTF-8 下汉字 3 字节，
/// 255 个字符的中文标签文件名在 Windows 可创建却无法向 Linux 同步/挂载；Windows 侧另有全路径
/// 260 UTF-16 字符上限（<see cref="TagFilenameService.MaxPathLength"/>，BuildNewPath 既有预检保留）。
/// 两口径独立拦截，双超限时先报组件级（Linux 字节）。
/// </summary>
public static class TagFilenameBudget
{
    /// <summary>Linux 文件名组件（不含目录）UTF-8 字节上限（ext4 等：255 字节）。</summary>
    public const int MaxFileNameComponentBytes = 255;

    /// <summary>
    /// 合成新文件名组件（base[t1 t2]+ext）。拼接规则的唯一来源——
    /// <see cref="TagFilenameService.Compose"/> 复用本函数，保证预算计算与实际合成口径一致；
    /// 不做标签合法性校验（由调用方预检）。
    /// </summary>
    public static string ComposeFileName(string baseName, string extension, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(tags);

        var tagSegment = tags.Count == 0 ? string.Empty : $"[{string.Join(' ', tags)}]";
        return baseName + tagSegment + extension;
    }

    /// <summary>新文件名组件（base+[tags]+ext 拼接结果）的 UTF-8 字节数。</summary>
    public static int GetFileNameByteCount(string baseName, string extension, IReadOnlyList<string> tags)
        => Encoding.UTF8.GetByteCount(ComposeFileName(baseName, extension, tags));

    /// <summary>
    /// 打上该标签集合后的剩余预算（255 − 新组件 UTF-8 字节数；负数 = 超出 |N| 字节）。
    /// </summary>
    public static int GetRemainingBytes(string baseName, string extension, IReadOnlyList<string> tags)
        => MaxFileNameComponentBytes - GetFileNameByteCount(baseName, extension, tags);

    /// <summary>
    /// 预算预检：新组件名超过 Linux 255 UTF-8 字节上限时返回用户可读的拒绝文案（含超出字节数），
    /// 未超限返回 null。打标前即时反馈与 BuildNewPath 落盘前拦截共用本口径。
    /// </summary>
    public static string? CheckFileNameBudget(string baseName, string extension, IReadOnlyList<string> tags)
    {
        var byteCount = GetFileNameByteCount(baseName, extension, tags);
        if (byteCount <= MaxFileNameComponentBytes)
        {
            return null;
        }

        var exceeded = byteCount - MaxFileNameComponentBytes;
        return $"打标后文件名 {byteCount} 字节超过 Linux 兼容上限 {MaxFileNameComponentBytes} 字节（超出 {exceeded} 字节）。";
    }
}
