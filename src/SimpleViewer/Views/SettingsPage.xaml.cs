// Responsibility: Settings page code-behind — folder picker, key capture, save/cancel actions.
// Invariants: Key capture only while IsRecordingKey; Save validates before closing host dialog.
// Call chain: MainWindow ContentDialog → SettingsPage → SettingsViewModel → SettingsService.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleViewer.ViewModels;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SimpleViewer.Views;

/// <summary>
/// Keyboard shortcut settings UI hosted in a <see cref="ContentDialog"/>.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public event EventHandler<bool>? CloseRequested;

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecordingKey)
        {
            return;
        }

        var control = IsModifierDown(VirtualKey.Control, e);
        var shift = IsModifierDown(VirtualKey.Shift, e);
        var menu = IsModifierDown(VirtualKey.Menu, e);

        ViewModel.RecordKey(e.Key.ToString(), control, shift, menu);
        e.Handled = true;
    }

    private static bool IsModifierDown(VirtualKey modifier, KeyRoutedEventArgs e)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is null)
        {
            return;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var window = App.CurrentWindow;
        if (window is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.SelectedItem.TargetPath = folder.Path;
        }
    }

    private void OnCommandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.OnPropertyChanged(nameof(SettingsViewModel.IsMoveToFolderSelected));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.TrySave())
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, false);
    }
}
