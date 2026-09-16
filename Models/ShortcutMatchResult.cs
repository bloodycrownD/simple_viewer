namespace SimpleViewer.Models;

/// <summary>
/// 一次快捷键匹配尝试的结果。
/// </summary>
public sealed class ShortcutMatchResult
{
    public ViewerCommand Command { get; init; }

    public string? MoveTargetPath { get; init; }

    /// <summary>
    /// ApplyTag 命中的标签稳定 Id（<see cref="TagDefinition.Id"/>），随绑定透传给打标入口；
    /// 其余命令为 null。
    /// </summary>
    public string? TagId { get; init; }
}
