namespace SimpleViewer.Models;

/// <summary>
/// 标签组：一组标签的容器；互斥组内打标时替换，非互斥组内叠加。
/// <see cref="Id"/> 在创建时生成、持久化后不变。
/// </summary>
public sealed class TagGroup
{
    /// <summary>稳定 Id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>组名（非空）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否互斥组：true 时组内标签单选（打标替换同组标签），false 时叠加。</summary>
    public bool Exclusive { get; set; }

    /// <summary>组内标签集合（默认空列表）。</summary>
    public List<TagDefinition> Tags { get; set; } = [];
}
