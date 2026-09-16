// 职责：设置页交互代码——文件夹选择器、按键捕获、保存/取消动作。
// 不变量：仅在 IsRecordingKey 期间捕获按键；保存（TrySave）校验失败时阻止关闭宿主对话框
//         （含 ApplyTag 缺标签参数/引用不存在标签的中文提示）。
// 调用链：MainWindow ContentDialog → SettingsPage → SettingsViewModel → SettingsService。

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
        ViewModel.NotifyCommandSelectionChanged();
    }

    /// <summary>Validates and saves bindings. Returns false when validation fails.</summary>
    public bool TrySave() => ViewModel.TrySave();
}
