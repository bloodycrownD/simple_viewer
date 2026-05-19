// Responsibility: Human-readable shortcut key labels for settings UI and list display.
// Invariants: Modifier order is Control, Shift, Menu (Alt); virtual key names pass through unchanged.
// Call chain: SettingsViewModel / ShortcutEditorItem → FormatBinding → UI TextBlocks.

namespace SimpleViewer.Helpers;

/// <summary>
/// Formats virtual keys and modifiers for display in the settings UI.
/// </summary>
public static class ShortcutDisplayHelper
{
    /// <summary>
    /// Builds a display string such as <c>Ctrl+Shift+A</c>.
    /// </summary>
    public static string FormatBinding(IReadOnlyList<string> modifiers, string virtualKey)
    {
        var parts = new List<string>();
        if (modifiers.Any(static m => m.Equals("Control", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.Any(static m => m.Equals("Shift", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("Shift");
        }

        if (modifiers.Any(static m =>
                m.Equals("Menu", StringComparison.OrdinalIgnoreCase)
                || m.Equals("Alt", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("Alt");
        }

        if (!string.IsNullOrWhiteSpace(virtualKey))
        {
            parts.Add(virtualKey);
        }

        return parts.Count == 0 ? "(none)" : string.Join("+", parts);
    }
}
