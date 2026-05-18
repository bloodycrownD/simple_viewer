namespace SimpleViewer.Models;

/// <summary>
/// Outcome of a shortcut key match attempt.
/// </summary>
public sealed class ShortcutMatchResult
{
    public ViewerCommand Command { get; init; }

    public string? MoveTargetPath { get; init; }
}
