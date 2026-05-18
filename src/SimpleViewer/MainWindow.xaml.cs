// Responsibility: Primary window chrome, keyboard routing, dialogs, and view model host callbacks.
// Invariants: Global shortcuts disabled while settings dialog is open; matched keys are marked handled.
// Call chain: App → MainWindow → ShortcutService.TryMatch → MainViewModel commands.

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
/// Primary shell window hosting the image viewer UI and view model bindings.
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
        ViewModel.ConfirmDeleteAsync = ConfirmDeleteAsync;
        ViewModel.OpenSettingsAsync = ShowSettingsDialogAsync;
        ViewModel.FullscreenChanged += OnFullscreenChanged;
        ViewModel.ExitRequested += OnExitRequested;

        InitializeComponent();
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
                Title = "Keyboard shortcuts",
                Content = page,
                XamlRoot = Content.XamlRoot,
                CloseButtonText = string.Empty,
                DefaultButton = ContentDialogButton.None,
            };

            page.CloseRequested += (_, _) => dialog.Hide();
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
            Title = "Delete image?",
            Content = "Move this file to the Recycle Bin?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
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
}
