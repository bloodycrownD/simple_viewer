namespace SimpleViewer.Models;

/// <summary>
/// 标签定义：文件名标签的用户可配置元数据。
/// <see cref="Id"/> 在创建时生成、持久化后不变；重命名只改 <see cref="Name"/>。
/// </summary>
public sealed class TagDefinition
{
    /// <summary>稳定 Id（快捷键绑定将引用此 Id）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标签名（写入文件名方括号段的平铺字符串）。</summary>
    public string Name { get; set; } = string.Empty;
}
