// Responsibility: Editable shortcut row in the settings UI.
// Invariants: MoveToFolder may carry TargetPath; DisplayKey updates when key or modifiers change.
// Call chain: SettingsViewModel ↔ ShortcutEditorItem ↔ SettingsPage bindings.

using CommunityToolkit.Mvvm.ComponentModel;
using SimpleViewer.Helpers;
using SimpleViewer.Models;

namespace SimpleViewer.ViewModels;

/// <summary>
/// One shortcut binding row in the settings editor.
/// </summary>
public partial class ShortcutEditorItem : ObservableObject
{
    [ObservableProperty]
    private string _virtualKey = string.Empty;

    [ObservableProperty]
    private List<string> _modifiers = [];

    [ObservableProperty]
    private ViewerCommand _command;

    [ObservableProperty]
    private string? _targetPath;

    [ObservableProperty]
    private string _displayKey = "(none)";

    /// <summary>
    /// Creates an editor row from a persisted binding.
    /// </summary>
    public static ShortcutEditorItem FromBinding(ShortcutBinding binding)
    {
        var item = new ShortcutEditorItem
        {
            VirtualKey = binding.VirtualKey,
            Modifiers = [.. binding.Modifiers],
            Command = binding.Command,
            TargetPath = binding.TargetPath,
        };
        item.RefreshDisplayKey();
        return item;
    }

    /// <summary>
    /// Converts this row to a <see cref="ShortcutBinding"/> for persistence.
    /// </summary>
    public ShortcutBinding ToBinding()
    {
        return new ShortcutBinding
        {
            VirtualKey = VirtualKey,
            Modifiers = [.. Modifiers],
            Command = Command,
            TargetPath = Command == ViewerCommand.MoveToFolder ? TargetPath : null,
        };
    }

    /// <summary>
    /// Applies a recorded key chord from the settings page key capture UI.
    /// </summary>
    public void ApplyRecordedKey(string virtualKey, bool control, bool shift, bool menu)
    {
        VirtualKey = virtualKey;
        Modifiers = [];
        if (control)
        {
            Modifiers.Add("Control");
        }

        if (shift)
        {
            Modifiers.Add("Shift");
        }

        if (menu)
        {
            Modifiers.Add("Menu");
        }

        RefreshDisplayKey();
    }

    partial void OnVirtualKeyChanged(string value) => RefreshDisplayKey();

    partial void OnModifiersChanged(List<string> value) => RefreshDisplayKey();

    private void RefreshDisplayKey()
    {
        DisplayKey = ShortcutDisplayHelper.FormatBinding(Modifiers, VirtualKey);
    }
}
