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
/// 承载于 <see cref="ContentDialog"/> 的键盘快捷键设置界面。
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

        var control = IsModifierDown(VirtualKey.Control);
        var shift = IsModifierDown(VirtualKey.Shift);
        var menu = IsModifierDown(VirtualKey.Menu);

        ViewModel.RecordKey(e.Key.ToString(), control, shift, menu);
        e.Handled = true;
    }

    private static bool IsModifierDown(VirtualKey modifier)
    {
        // 只判 Down：Locked 位对修饰键无意义，中文 IME 切中英文会置位（曾致快捷键录制误判 Shift）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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

    /// <summary>校验并保存绑定；校验失败（失败原因已由视图模型写入状态区）返回 false，宿主据此阻止关闭对话框。</summary>
    public bool TrySave() => ViewModel.TrySave();
}
