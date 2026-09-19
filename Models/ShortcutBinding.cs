namespace SimpleViewer.Models;

/// <summary>
/// 虚拟键 + 修饰键到查看器命令的绑定。
/// </summary>
public sealed class ShortcutBinding
{
    public string VirtualKey { get; set; } = string.Empty;

    public List<string> Modifiers { get; set; } = [];

    public ViewerCommand Command { get; set; }

    public string? TargetPath { get; set; }

    /// <summary>
    /// 打标签（ApplyTag）绑定引用的标签稳定 Id（<see cref="TagDefinition.Id"/>，模型契约）：
    /// Id 在标签创建时生成、持久化后不变，重命名标签不影响绑定；
    /// 保存前由 ValidateBindings 校验必须携带且引用存在的标签（类比 MoveToFolder 之 TargetPath 先例）。
    /// </summary>
    public string? TagId { get; set; }
}
