namespace SimpleViewer.Models;

/// <summary>
/// Maps a virtual key and modifier keys to a viewer command.
/// </summary>
public sealed class ShortcutBinding
{
    public string VirtualKey { get; set; } = string.Empty;

    public List<string> Modifiers { get; set; } = [];

    public ViewerCommand Command { get; set; }

    public string? TargetPath { get; set; }
}
