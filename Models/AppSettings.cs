namespace SimpleViewer.Models;

/// <summary>
/// Persisted application settings (shortcuts and schema version).
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public List<ShortcutBinding> Shortcuts { get; set; } = [];
}
