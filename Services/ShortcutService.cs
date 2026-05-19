// Responsibility: Map VirtualKey + modifiers to ViewerCommand using persisted bindings.
// Invariants: First exact match wins; MoveToFolder includes TargetPath when configured.
// Call chain: MainWindow KeyDown → TryMatch → MainViewModel command execution.

using SimpleViewer.Models;
using Windows.System;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IShortcutService" />
public sealed class ShortcutService : IShortcutService
{
    private readonly ISettingsService _settingsService;

    public ShortcutService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public ShortcutMatchResult? TryMatch(VirtualKey key, bool control, bool shift, bool menu)
    {
        var keyName = key.ToString();
        var settings = _settingsService.Load();

        foreach (var binding in settings.Shortcuts)
        {
            if (!string.Equals(binding.VirtualKey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ModifiersMatch(binding.Modifiers, control, shift, menu))
            {
                continue;
            }

            return new ShortcutMatchResult
            {
                Command = binding.Command,
                MoveTargetPath = binding.Command == ViewerCommand.MoveToFolder ? binding.TargetPath : null,
            };
        }

        return null;
    }

    private static bool ModifiersMatch(IReadOnlyList<string> modifiers, bool control, bool shift, bool menu)
    {
        var wantsControl = modifiers.Any(static m => m.Equals("Control", StringComparison.OrdinalIgnoreCase));
        var wantsShift = modifiers.Any(static m => m.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var wantsMenu = modifiers.Any(static m => m.Equals("Menu", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Alt", StringComparison.OrdinalIgnoreCase));

        return wantsControl == control && wantsShift == shift && wantsMenu == menu;
    }
}
