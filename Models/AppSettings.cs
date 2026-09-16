namespace SimpleViewer.Models;

/// <summary>
/// Persisted application settings (shortcuts and schema version).
/// </summary>
public sealed class AppSettings
{
    /// <summary>设置 schema 版本（v2 = 含标签组）。</summary>
    public int Version { get; set; } = 2;

    public List<ShortcutBinding> Shortcuts { get; set; } = [];

    /// <summary>
    /// 标签组配置（schema v2 新增）。
    /// 旧版 v1 配置缺失此字段时加载为空列表；JSON 中显式 null 由 Load 迁移分支补空。
    /// </summary>
    public List<TagGroup> TagGroups { get; set; } = [];
}
