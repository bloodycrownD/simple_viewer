// 职责：主窗口 chrome——键盘路由（含 Esc 三态模式感知路由 D6、Ctrl+A 全选命中集 Step 10）、对话框宿主、
//       视图模型宿主回调注入、双模式壳装配与批量打标进度/回执区（D13 InfoBar，XAML 内嵌）。
// 不变量：设置对话框打开期间全局快捷键整体屏蔽（_shortcutsEnabled）；命中的按键标记已处理；
//         Ctrl+A 仅在快捷键表未占用时接管（用户自定义绑定优先）；
//         ExitApp 分派点先经 Esc 三态路由拦截（单图+有图库→返回图库；瀑布流+选中集→清空选中；其余→原退出行为）。
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

        // 标签栏（Step 9）：设置服务经宿主注入（保持 MainViewModel 构造签名稳定，App 组装不变）；
        // 编辑对话框宿主回调（含 _shortcutsEnabled 屏蔽）在此注入侧栏 VM。
        ViewModel.AttachSettingsService(_settingsService);
        ViewModel.TagSidebar.ShowTagEditorAsync = ShowTagEditorAsync;

        InitializeComponent();

        // 单图视图构造注入（沿用 SettingsPage“先赋值后 InitializeComponent”惯例；
        // 宿主 ContentControl 的可见性由 x:Bind 按 VM 模式属性互斥切换，D14）。
        SingleImageHost.Content = new SingleImageView(ViewModel);

        // 瀑布流本体（Step 8）：同一互斥切换机制；Esc 返回后滚动位置由 Visibility 切换天然保持。
        WaterfallHost.Content = new WaterfallView(ViewModel);

        // 标签栏本体（Step 9）：配置组初始呈现（计数随扫描/编辑刷新）。
        TagSidebarHost.Content = new TagSidebarControl(ViewModel, ViewModel.TagSidebar);
        _ = ViewModel.InitializeTagSidebarAsync();

        ConfigureWindowChrome();
        ApplySystemBackdrop();
        ApplyThemeFromSettings();
    }

    /// <summary>主题三态循环顺序（demo 深色优先：默认 Dark）。</summary>
    private static readonly string[] ThemeCycle = ["Dark", "Light", "System"];

    /// <summary>按设置应用根主题（未知/缺失值容错为跟随系统）。</summary>
    private void ApplyThemeFromSettings()
    {
        var preferred = _settingsService.Load().PreferredTheme;
        ApplyTheme(preferred);
    }

    /// <summary>
    /// 应用根主题（RootGrid.RequestedTheme 影响 RootGrid 内全部 ThemeResource 解析，标题栏跟随）：
    /// Dark/Light 显式指定；System（含未知值）清除覆盖回系统主题。
    /// 同步更新代码侧颜色标志并重建侧栏/筛选条（TagSidebarConverters 的 x:Bind 颜色函数不认
    /// RootGrid 主题覆盖，须按 IsDarkTheme 双值重算——深色下 chip 文字发黑的走查修复）。
    /// </summary>
    private void ApplyTheme(string preferred)
    {
        ThemeButton.Label = preferred switch
        {
            "Dark" => "深色",
            "Light" => "浅色",
            _ => "跟随系统",
        };
        RootGrid.RequestedTheme = preferred switch
        {
            "Dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            _ => Microsoft.UI.Xaml.ElementTheme.Default,
        };
        Views.TagSidebarConverters.IsDarkTheme = RootGrid.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
        ViewModel.RefreshThemeDependentVisuals();
    }

    /// <summary>工具栏「主题」按钮：三态循环并持久化（load-modify-save，保留其他字段）。</summary>
    private void OnThemeButtonClick(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        var current = ThemeCycle.Contains(settings.PreferredTheme, StringComparer.Ordinal)
            ? settings.PreferredTheme
            : "System";
        var next = ThemeCycle[(Array.IndexOf(ThemeCycle, current) + 1) % ThemeCycle.Length];
        settings.PreferredTheme = next;
        _settingsService.Save(settings);
        ApplyTheme(next);
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
            // Ctrl+A：瀑布流全选当前命中集（Step 10）。仅当用户未把 Ctrl+A 绑定为命令时接管
            // （绑定优先）；图库模式且已打开图库才生效，与 Esc 清空选中配套。
            if (e.Key == VirtualKey.A && control && !shift && !menu
                && ViewModel.CurrentMode == ViewerMode.Gallery
                && ViewModel.HasGallery)
            {
                e.Handled = true;
                ViewModel.SelectAllCards();
            }

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
            case ViewerCommand.ApplyTag:
                // 快捷键打标（Step 12，D7）：单图模式 = 当前图 toggle 打标；图库模式 = 选中集批量（空则忽略）；
                // 模式分流与互斥语义在 MainViewModel.ApplyTagByShortcutAsync（复用 Step 10 管线）。
                if (!string.IsNullOrWhiteSpace(match.TagId))
                {
                    _ = ViewModel.ApplyTagByShortcutAsync(match.TagId);
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

    /// <summary>
    /// 标签/组编辑对话框宿主（Step 9，D13：沿用 SettingsPage 的 ContentDialog + TrySave 模板）。
    /// 对话框期间快捷键整体屏蔽（_shortcutsEnabled）；PrimaryButtonClick 经 deferral 异步等待执行，
    /// 执行失败（返回 false）时取消关闭、错误显示于对话框内。
    /// </summary>
    private async Task ShowTagEditorAsync(TagEditRequest request)
    {
        _shortcutsEnabled = false;
        try
        {
            var editor = new TagEditDialog(request, ViewModel.ExecuteTagEditAsync);
            var dialog = new ContentDialog
            {
                Title = editor.Title,
                Content = editor,
                XamlRoot = Content.XamlRoot,
                PrimaryButtonText = editor.PrimaryButtonText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!await editor.TrySaveAsync())
                    {
                        args.Cancel = true;
                    }
                }
                finally
                {
                    deferral.Complete();
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
