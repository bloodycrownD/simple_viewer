// Responsibility: Settings page state — shortcut list editing, validation, and persistence.
// Invariants: Save rejects duplicate bindings via SettingsService; MoveToFolder requires TargetPath.
// Call chain: SettingsPage → SettingsViewModel → ISettingsService.Save; MainWindow opens dialog.

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>
/// View model for the keyboard shortcuts settings page.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(ISettingsService settingsService, AppSettings? initial = null)
    {
        _settingsService = settingsService;
        var settings = initial ?? settingsService.Load();
        foreach (var binding in settings.Shortcuts)
        {
            Items.Add(ShortcutEditorItem.FromBinding(binding));
        }

        if (Items.Count > 0)
        {
            SelectedItem = Items[0];
        }
    }

    public ObservableCollection<ShortcutEditorItem> Items { get; } = [];

    public IReadOnlyList<ViewerCommand> AvailableCommands { get; } =
        Enum.GetValues<ViewerCommand>();

    [ObservableProperty]
    private ShortcutEditorItem? _selectedItem;

    [ObservableProperty]
    private bool _isRecordingKey;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool IsMoveToFolderSelected =>
        SelectedItem?.Command == ViewerCommand.MoveToFolder;

    partial void OnSelectedItemChanged(ShortcutEditorItem? value)
    {
        if (_subscribedItem is not null)
        {
            _subscribedItem.PropertyChanged -= OnSelectedItemPropertyChanged;
        }

        _subscribedItem = value;
        if (value is not null)
        {
            value.PropertyChanged += OnSelectedItemPropertyChanged;
        }

        OnPropertyChanged(nameof(IsMoveToFolderSelected));
    }

    private ShortcutEditorItem? _subscribedItem;

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShortcutEditorItem.Command) or nameof(ShortcutEditorItem.TargetPath))
        {
            OnPropertyChanged(nameof(IsMoveToFolderSelected));
        }
    }

    partial void OnIsRecordingKeyChanged(bool value)
    {
        StatusMessage = value ? "Press the key combination to assign…" : string.Empty;
    }

    /// <summary>
    /// Records the next key press into <see cref="SelectedItem"/>.
    /// </summary>
    public void RecordKey(string virtualKey, bool control, bool shift, bool menu)
    {
        if (!IsRecordingKey || SelectedItem is null)
        {
            return;
        }

        if (IsModifierOnlyKey(virtualKey))
        {
            return;
        }

        SelectedItem.ApplyRecordedKey(virtualKey, control, shift, menu);
        IsRecordingKey = false;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void Add()
    {
        var item = ShortcutEditorItem.FromBinding(new ShortcutBinding
        {
            VirtualKey = "Right",
            Modifiers = [],
            Command = ViewerCommand.NextImage,
        });
        Items.Add(item);
        SelectedItem = item;
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        if (Items.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        SelectedItem = Items[Math.Min(index, Items.Count - 1)];
    }

    [RelayCommand]
    private void StartRecordKey()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select a shortcut row first.";
            return;
        }

        IsRecordingKey = true;
    }

    /// <summary>
    /// Validates and persists bindings. Returns false when validation fails.
    /// </summary>
    public bool TrySave()
    {
        try
        {
            var settings = new AppSettings
            {
                Version = 1,
                Shortcuts = Items.Select(static i => i.ToBinding()).ToList(),
            };

            SettingsService.ValidateBindings(settings);
            _settingsService.Save(settings);
            StatusMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    private static bool IsModifierOnlyKey(string virtualKey)
    {
        return virtualKey is "Control" or "Shift" or "Menu" or "LeftWindows" or "RightWindows";
    }
}
