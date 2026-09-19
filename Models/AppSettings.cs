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

    /// <summary>
    /// 界面主题偏好（2026-09-17 走查补充）：Dark（默认，对齐 demo 深色优先）/ Light / System。
    /// 旧配置缺失时回退 Dark；非法值按 System 处理（MainWindow.ApplyTheme 容错）。
    /// </summary>
    public string PreferredTheme { get; set; } = "Dark";

    /// <summary>
    /// 上次打开的图库根目录（2026-09-17 走查补充）：下次启动无 CLI 参数时自动恢复。
    /// 空串 = 无记录（首次使用）；目录不存在时静默忽略。
    /// </summary>
    public string LastLibraryRoot { get; set; } = "";
}
