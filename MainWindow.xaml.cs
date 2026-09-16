// 职责：主窗口 chrome——键盘路由（含 Esc 三态模式感知路由 D6）、对话框宿主、视图模型宿主回调注入、双模式壳装配。
// 不变量：设置对话框打开期间全局快捷键整体屏蔽（_shortcutsEnabled）；命中的按键标记已处理；
//         ExitApp 分派点先经 Esc 三态路由拦截（单图+有图库→返回图库；瀑布流+选中集→清空选中（Step 10）；其余→原退出行为）。
// 调用链：App → MainWindow → ShortcutService.TryMatch → MainViewModel 命令。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleViewer.Models;
using SimpleViewer.Services;
using SimpleViewer.ViewModels;
using SimpleViewer.Views;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SimpleViewer;

/// <summary>
/// 主壳窗口：承载单图/图库双模式 UI 与视图模型绑定。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinWidth = 800;
    private const int MinHeight = 600;

    private readonly IShortcutService _shortcutService;
    private readonly ISettingsService _settingsService;
    private bool _shortcutsEnabled = true;

    public MainViewModel ViewModel { get; }

    public MainWindow(
        MainViewModel viewModel,
        IShortcutService shortcutService,
        ISettingsService settingsService)
    {
        ViewModel = viewModel;
        _shortcutService = shortcutService;
        _settingsService = settingsService;

        ViewModel.PickImageFileAsync = PickImageFileAsync;
        ViewModel.PickLibraryFolderAsync = PickLibraryFolderAsync;
        ViewModel.ConfirmDeleteAsync = ConfirmDeleteAsync;
        ViewModel.OpenSettingsAsync = ShowSettingsDialogAsync;
        ViewModel.FullscreenChanged += OnFullscreenChanged;
        ViewModel.ExitRequested += OnExitRequested;

        InitializeComponent();

        // 单图视图构造注入（沿用 SettingsPage“先赋值后 InitializeComponent”惯例；
        // 宿主 ContentControl 的可见性由 x:Bind 按 VM 模式属性互斥切换，D14）。
        SingleImageHost.Content = new SingleImageView(ViewModel);

        ConfigureWindowChrome();
        ApplySystemBackdrop();
    }

    private void ConfigureWindowChrome()
    {
        const int defaultWidth = 1280;
        const int defaultHeight = 800;

        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(defaultWidth, defaultHeight));
        appWindow.Changed += OnAppWindowChanged;

        Title = "Simple Viewer";
    }

    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        if (size.Width >= MinWidth && size.Height >= MinHeight)
        {
            return;
        }

        sender.Resize(new Windows.Graphics.SizeInt32(
            Math.Max(size.Width, MinWidth),
            Math.Max(size.Height, MinHeight)));
    }

    private void ApplySystemBackdrop()
    {
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = (int)e.NewSize.Width;
        var height = (int)e.NewSize.Height;
        _ = ViewModel.OnViewportSizeChangedAsync(width, height);
    }

    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        AppWindow.SetPresenter(
            isFullscreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_shortcutsEnabled)
        {
            return;
        }

        if (IsTextInputFocused())
        {
            return;
        }

        var control = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var menu = IsKeyDown(VirtualKey.Menu);
        var match = _shortcutService.TryMatch(e.Key, control, shift, menu);
        if (match is null)
        {
            return;
        }

        e.Handled = true;
        DispatchShortcut(match);
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }

    private static bool IsTextInputFocused()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(
            App.CurrentWindow?.Content?.XamlRoot);
        return focused is TextBox or AutoSuggestBox;
    }

    private void DispatchShortcut(ShortcutMatchResult match)
    {
        switch (match.Command)
        {
            case ViewerCommand.NextImage:
                if (ViewModel.NextCommand.CanExecute(null))
                {
                    ViewModel.NextCommand.Execute(null);
                }

                break;
            case ViewerCommand.PrevImage:
                if (ViewModel.PrevCommand.CanExecute(null))
                {
                    ViewModel.PrevCommand.Execute(null);
                }

                break;
            case ViewerCommand.RotateLeft:
                if (ViewModel.RotateLeftCommand.CanExecute(null))
                {
                    ViewModel.RotateLeftCommand.Execute(null);
                }

                break;
            case ViewerCommand.RotateRight:
                if (ViewModel.RotateRightCommand.CanExecute(null))
                {
                    ViewModel.RotateRightCommand.Execute(null);
                }

                break;
            case ViewerCommand.ToggleFullscreen:
                ViewModel.ToggleFullscreenCommand.Execute(null);
                break;
            case ViewerCommand.DeleteImage:
                if (ViewModel.DeleteCommand.CanExecute(null))
                {
                    ViewModel.DeleteCommand.Execute(null);
                }

                break;
            case ViewerCommand.ExitApp:
                // Esc 三态路由（D6）：单图且有图库 → 返回瀑布流；瀑布流且有选中集 → 清空（Step 10 接入）；
                // 其余维持原退出行为。对话框打开期间快捷键已被 _shortcutsEnabled 整体屏蔽，Esc 优先关闭对话框。
                if (ViewModel.TryRouteEscape())
                {
                    break;
                }

                ViewModel.RequestExit();
                break;
            case ViewerCommand.MoveToFolder:
                if (!string.IsNullOrWhiteSpace(match.MoveTargetPath)
                    && ViewModel.MoveToFolderCommand.CanExecute(match.MoveTargetPath))
                {
                    ViewModel.MoveToFolderCommand.Execute(match.MoveTargetPath);
                }

                break;
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        _shortcutsEnabled = false;
        try
        {
            var settingsVm = new SettingsViewModel(_settingsService);
            var page = new SettingsPage(settingsVm);

            var dialog = new ContentDialog
            {
                Title = "键盘快捷键",
                Content = page,
                XamlRoot = Content.XamlRoot,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!page.TrySave())
                {
                    args.Cancel = true;
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _shortcutsEnabled = true;
        }
    }

    private async Task<bool> ConfirmDeleteAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "删除图片？",
            Content = "将此文件移入回收站？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PickImageFileAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };

        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".gif");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>图库根目录选择器（FolderPicker，仿 PickImageFileAsync 的 InitializeWithWindow 模式）。</summary>
    private async Task<string?> PickLibraryFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };

        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
