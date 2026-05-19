using SimpleViewer.Models;
using Windows.System;

namespace SimpleViewer.Services;

/// <summary>
/// Matches keyboard input against configured shortcut bindings.
/// </summary>
public interface IShortcutService
{
    ShortcutMatchResult? TryMatch(VirtualKey key, bool control, bool shift, bool menu);
}
