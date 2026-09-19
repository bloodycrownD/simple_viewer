// Responsibility: TagSpaces 文件名标签协议（base[t1 t2].ext）的解析/合成/标签名校验与重命名目标路径预检。
// Invariants: 仅基名尾部完整方括号段视为标签；标签名不含任何空白字符与方括号；BuildNewPath 对可预期失败返回结果而非抛异常；
//             长度双口径预检——Linux 组件名 255 UTF-8 字节（最严格平台）+ Windows 全路径 260 字符，拼接规则唯一来源在 TagFilenameBudget。
// Call chain: TagService（Step 3 打标重命名）→ TryParse/Compose/BuildNewPath；ValidateTagName 规则与 SettingsService 标签组校验共用口径。

namespace SimpleViewer.Services;

/// <inheritdoc cref="ITagFilenameService" />
public sealed class TagFilenameService : ITagFilenameService
{
    /// <summary>Windows 传统 MAX_PATH 口径：新路径长度达到该值即返回失败（D11：系统长路径策略之外的前置双保险；PRD 口径"将达到 260 即阻止"）。</summary>
    public const int MaxPathLength = 260;

    /// <inheritdoc />
    public bool TryParse(string? fileName, out string baseName, out string extension, out IReadOnlyList<string> tags)
    {
        baseName = string.Empty;
        extension = string.Empty;
        tags = Array.Empty<string>();

        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        // 容错：调用方误传全路径时仅取文件名部分；扩展名保留前导点与原大小写。
        var name = Path.GetFileName(fileName);
        extension = Path.GetExtension(name);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

        if (nameWithoutExt.EndsWith(']'))
        {
            // 取最后一个 '['：其后的 ']' 之前内容即候选标签段。
            var open = nameWithoutExt.LastIndexOf('[');
            if (open >= 0)
            {
                var content = nameWithoutExt[(open + 1)..^1];

                // 段内不允许再出现方括号（如 a[[b]、a[b]c]），否则视为普通基名字符，不作为标签段。
                if (content.IndexOf('[') < 0 && content.IndexOf(']') < 0)
                {
                    // Split((char[]?)null, ...) 按 char.IsWhiteSpace 全集切分并丢弃空段，
                    // 天然归一多重半角空格/全角空格/nbsp 等一切空白边界。
                    var parsedTags = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parsedTags.Length > 0)
                    {
                        baseName = nameWithoutExt[..open];
                        tags = parsedTags;
                        return true;
                    }

                    // 空段（如 a[].jpg）不视为有效标签段，整体保留在基名中。
                }
            }
        }

        baseName = nameWithoutExt;
        return true;
    }

    /// <inheritdoc />
    public string Compose(string baseName, string extension, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in tags)
        {
            // 合成不变量：非法标签会破坏方括号段结构，此处 fail-fast（BuildNewPath 前置校验后不会走到这里）。
            if (!ValidateTagName(tag))
            {
                throw new ArgumentException(
                    $"标签名非法：\"{tag}\"（不允许为空、含空白字符或方括号）。", nameof(tags));
            }
        }

        // 拼接规则唯一来源在 TagFilenameBudget.ComposeFileName（预算计算与实际合成共用同一口径）。
        return TagFilenameBudget.ComposeFileName(baseName, extension, tags);
    }

    /// <inheritdoc />
    public bool ValidateTagName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var ch in name)
        {
            // char.IsWhiteSpace 全集（含全角空格 \u3000、nbsp \u00A0、制表符等）；方括号会破坏段结构。
            if (char.IsWhiteSpace(ch) || ch == '[' || ch == ']')
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public TagFilenamePathResult BuildNewPath(string oldFullPath, IReadOnlyList<string> newTags)
    {
        if (string.IsNullOrWhiteSpace(oldFullPath))
        {
            return TagFilenamePathResult.Fail("原文件路径不能为空。");
        }

        ArgumentNullException.ThrowIfNull(newTags);

        foreach (var tag in newTags)
        {
            if (!ValidateTagName(tag))
            {
                return TagFilenamePathResult.Fail($"标签名非法：\"{tag}\"（不允许为空、含空白字符或方括号）。");
            }
        }

        var directory = Path.GetDirectoryName(oldFullPath);
        if (!TryParse(Path.GetFileName(oldFullPath), out var baseName, out var extension, out _))
        {
            return TagFilenamePathResult.Fail("无法解析原文件名。");
        }

        var newFileName = Compose(baseName, extension, newTags);
        var newFullPath = string.IsNullOrEmpty(directory) ? newFileName : Path.Combine(directory, newFileName);

        // 双口径长度预检（2026-09-19 标签预算），先于存在性检查：超长路径无需也无法做可靠的存在性探测。
        // ① Linux 组件名 255 UTF-8 字节（最严格平台——UTF-8 汉字 3 字节，Windows 可创建的中文长名
        //    无法向 Linux 同步/挂载；组件名 = base+[tags]+ext，不含目录）；
        // ② Windows 全路径 260 UTF-16 字符（既有口径，保留）。双超限先报组件级（Linux 字节）。
        var fileNameBytes = TagFilenameBudget.GetFileNameByteCount(baseName, extension, newTags);
        if (fileNameBytes > TagFilenameBudget.MaxFileNameComponentBytes)
        {
            var exceededBytes = fileNameBytes - TagFilenameBudget.MaxFileNameComponentBytes;
            return TagFilenamePathResult.Fail(
                $"新文件名 {fileNameBytes} 字节超过 Linux 兼容上限 " +
                $"{TagFilenameBudget.MaxFileNameComponentBytes} 字节（超出 {exceededBytes} 字节）。");
        }

        // 达到即拒绝（cr/P2-13，>= 口径）：PRD 口径为"将达到 260 即阻止"，恰好 260 字符不放行。
        if (newFullPath.Length >= MaxPathLength)
        {
            return TagFilenamePathResult.Fail(
                $"新路径长度 {newFullPath.Length} 达到 {MaxPathLength} 字符上限。");
        }

        // 新旧路径相同（含仅大小写差异）不视为冲突；目标名已被其他文件占用才返回冲突。
        if (!string.Equals(newFullPath, oldFullPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(newFullPath))
        {
            return TagFilenamePathResult.Fail($"目标文件名已存在：\"{newFileName}\"。");
        }

        return TagFilenamePathResult.Ok(newFullPath);
    }
}
