// 职责：主窗口 chrome——键盘路由（含 Esc 三态模式感知路由 D6、Ctrl+A 全选命中集 Step 10、
//       图库 Enter 进入单图 cr/P1-4）、对话框宿主
//       （设置/标签编辑/标签目录选择器——均含 _shortcutsEnabled 屏蔽）、视图模型宿主回调注入、
//       双模式壳装配与打标进度/回执区（D13 InfoBar，XAML 内嵌）、
//       筛选面板 Flyout 宿主（tag-filter-tree Step 5+6：工具栏「⧩ 筛选 ▾」按钮挂 Flyout，
//       Opening 时主题对齐 + 快照重建 + 单图模式先切回图库；面板打开期间全局快捷键局部抑制
//       ——_filterFlyoutOpen 非模态共存：快捷键不打扰图库、Esc 交 Flyout light-dismiss 关闭）。
// 不变量：设置对话框打开期间全局快捷键整体屏蔽（_shortcutsEnabled）；命中的按键标记已处理；
//         Ctrl+A 仅在快捷键表未占用时接管（用户自定义绑定优先）；
//         Enter 同口径（无修饰 + 图库模式 + 快捷键表未占用时接管进入单图，cr/P1-4）；
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

    /// <summary>筛选面板 Flyout 打开中（tag-filter-tree Step 6）：全局快捷键局部抑制标志。
    /// 非模态共存语义——面板开着时快捷键不打扰图库（OnPreviewKeyDown 路径整体短路，含
    /// Ctrl+A 全选 / Enter 进单图接管），与 <see cref="_shortcutsEnabled"/>（模态对话框才
    /// 全局禁）正交：Opening 置位、Closed 复位（Esc / 点外 light-dismiss / 完成・✕ 的 Hide
    /// 三条关闭路径都经 Closed）；Esc 不走本窗口快捷键表（ExitApp 路由），交 Flyout
    /// light-dismiss 自身关闭——勿双向抢。</summary>
    private bool _filterFlyoutOpen;

    /// <summary>筛选面板本体（tag-filter-tree Step 5，工具栏 Flyout 内容；构造注入一次、全量 Rebuild 复用）。</summary>
    private readonly TagFilterPanelControl _filterPanel;

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
        // 图库删除选中集确认宿主（batch-tag-management Step 7，D9）。
        ViewModel.ConfirmDeleteSelectionAsync = ConfirmDeleteSelectionAsync;
        ViewModel.OpenSettingsAsync = ShowSettingsDialogAsync;
        ViewModel.ShowTagCatalogAsync = ShowTagCatalogDialogAsync;
        // 批量标签目录宿主（batch-tag-management Step 5）：图库右栏「＋ 添加标签」。
        ViewModel.ShowSelectionTagCatalogAsync = ShowSelectionTagCatalogDialogAsync;
        // 未定义标签区对话框宿主（batch-tag-management Step 3）：连锁删除确认 + 收纳目标组选择。
        ViewModel.ConfirmUndefinedDeleteAsync = ConfirmUndefinedDeleteAsync;
        ViewModel.PickAbsorbGroupAsync = PickAbsorbGroupAsync;
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

        // 图库右栏（选中集标签面板，batch-tag-management Step 4）：构造注入（带 MainViewModel 参数，
        // 无法在 XAML 实例化）；宿主在图库 Grid 右缘叠加，随 GalleryVisibility 单图模式天然隐藏。
        GallerySelectionPanelHost.Content = new GallerySelectionPanelControl(ViewModel);

        // 标签栏本体（Step 9）：配置组初始呈现（计数随扫描/编辑刷新）。
        TagSidebarHost.Content = new TagSidebarControl(ViewModel, ViewModel.TagSidebar);
        _ = ViewModel.InitializeTagSidebarAsync();

        // 筛选面板本体（tag-filter-tree Step 5）：Flyout 宿主注入（构造注入惯例，XAML 占位
        // FilterPanelHost）；面板编辑入口集中在 MainViewModel.EditFilter（树编辑→互斥清
        // untagged→ApplyTagFilter 一次到位），完成/✕ 经 CloseRequested 回本窗口 Hide Flyout。
        _filterPanel = new TagFilterPanelControl(ViewModel, new TagFilterPanelViewModel(ViewModel));
        _filterPanel.CloseRequested += (_, _) => TagFilterFlyout.Hide();
        FilterPanelHost.Content = _filterPanel;
        // Flyout 处于 popup 层不认 RootGrid.RequestedTheme（ApplyDialogTheme 同款坑位）：
        // Opening 时面板内容根对齐当前根主题，并拉最新快照全量重建（面板关闭期间左栏/筛选条
        // 等外部入口可能已改树或计数，demo openPanel 后 renderPanel 同构）。
        TagFilterFlyout.Opening += OnFilterFlyoutOpening;
        TagFilterFlyout.Closed += OnFilterFlyoutClosed;

        // chrome 行高度联动（2026-09-19 遮挡修复）：画布层浮层（右栏/折叠条/顶部横条）在 chrome 层
        // 之下，顶部可点区须让出工具栏实际行高（右栏收起按钮曾被工具栏横行遮盖点不到）。
        // SizeChanged 写入 VM，SingleImageView 订阅后调整浮层 Margin。
        // 2026-09-24 悬空根治：MainInfoBar 浮层化（Row2 顶部 + ZIndex=2）后不再参与本高度——
        // 旧行为回执弹出把单图横条顶离工具栏悬在画布中部（用户走查实报，repro-strip-float5 实锤）。
        ToolBarRow.SizeChanged += OnChromeRowSizeChanged;

        ConfigureWindowChrome();
        ApplySystemBackdrop();
        ApplyThemeFromSettings();
        ConfigureThumbnailDpiBucket();
    }

    /// <summary>
    /// 工具栏行尺寸变化：实际占位高度写入 VM，驱动画布层浮层（右栏/折叠条/顶部横条）的顶部避让 Margin。
    /// 2026-09-24 悬空根治：MainInfoBar 已浮层化（Row2 顶部浮层，见 XAML 注释）——本高度不再含
    /// InfoBar 分量（旧行为：回执弹出把单图横条顶离工具栏悬在画布中部，走查实报 + 实锤）；
    /// InfoBar 关闭时整体 Collapsed（走查 4：IsOpen=false 只塌内容、Visible 元素的 Margin 仍占位）。
    /// </summary>
    private void OnChromeRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.TopChromeHeight = ToolBarRow.ActualHeight;
    }

    /// <summary>
    /// DPI 感知的缩略图分桶（2026-09-17 走查修复模糊）：卡片逻辑宽 240 × 显示缩放，
    /// 向上取整到 120 的倍数（100%→240、150%→360、200%→480）。
    /// 用窗口句柄 P/Invoke 查 DPI（XamlRoot.RasterizationScale 在互斥 Visibility 容器内
    /// 首次加载时不可靠，曾导致 200% 屏仍请求 360 桶、缩略图拉伸发糊）。
    /// </summary>
    private void ConfigureThumbnailDpiBucket()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            App.DisplayScale = scale;
            ViewModels.GalleryItemViewModel.ThumbnailBucket =
                (int)Math.Ceiling(Views.MasonryLayout.TargetCardWidth * scale / 120.0) * 120;
        }
        catch
        {
            // DPI 查询失败：保持默认桶（360），仅影响清晰度不影响功能。
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
        ThemeButton.Content = preferred switch
        {
            "Dark" => "主题：深色",
            "Light" => "主题：浅色",
            _ => "主题：跟随系统",
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

    /// <summary>
    /// 按 RootGrid 主题为弹窗着色（2026-09-19 弹窗主题走查修复）：ContentDialog 宿主在 popup 层、
    /// 不在 RootGrid 视觉树内，其 ThemeResource 与底色按应用/系统主题解析，不认
    /// RootGrid.RequestedTheme 运行时覆盖（深色应用下弹窗白底）。所有 ContentDialog 展示前统一调用。
    /// </summary>
    private void ApplyDialogTheme(ContentDialog dialog)
        => dialog.RequestedTheme = RootGrid.RequestedTheme;

    /// <summary>
    /// 筛选面板 Flyout 打开（tag-filter-tree Step 5+6）：进入快捷键局部抑制（Opening 先于
    /// 显示，Esc/light-dismiss 可用前已生效）；单图模式下先切回图库让筛选结果可见（对齐
    /// HandleTagChipTapped / ToggleUntaggedFilter 先例；CLI 直开无图库时保持单图——无命中集
    /// 可看，切回只见空态）；面板内容根主题对齐（popup 层不认 RootGrid.RequestedTheme 运行时
    /// 覆盖，ApplyDialogTheme 同款坑位）+ 拉最新快照全量重建（面板关闭期间左栏点击筛选 /
    /// 筛选条 ✕ / 标签删改名等外部入口可能已改树，打开即呈现现状）。
    /// </summary>
    private void OnFilterFlyoutOpening(object? sender, object e)
    {
        _filterFlyoutOpen = true;

        if (ViewModel.CurrentMode == ViewerMode.Single && ViewModel.HasGallery)
        {
            ViewModel.CurrentMode = ViewerMode.Gallery;
        }

        // 动态收窄（五轮走查修复）：面板上限 700 逻辑，窄窗口（900 逻辑窗 - 700 面板 = 200 < 左栏
        // 280）时左缘会压住左栏标签行右端的计数徽章——按窗口可用宽收窄面板（保左栏完整 + 余量），
        // 宽窗恢复 700 上限。面板 UserControl 宽与 FlyoutPresenter 宽同步（外壳=内容+左右
        // FlyoutContentPadding 16×2 + 边框余量 40）。窗口宽取内容根（WinUI Window 无 ActualWidth）。
        var panelWidth = Math.Clamp((int)(RootGrid.ActualWidth - 400), 420, 660);
        _filterPanel.Width = panelWidth;
        TagFilterFlyout.FlyoutPresenterStyle = BuildFilterFlyoutStyle(panelWidth + 40);

        _filterPanel.RequestedTheme = RootGrid.RequestedTheme;
        _filterPanel.RebuildAll();
    }

    /// <summary>
    /// 构建筛选 Flyout 的 FlyoutPresenterStyle（五轮：宽度动态化，Opening 时按窗口宽重建）。
    /// BasedOn DefaultFlyoutPresenterStyle（generic.xaml 核实存在）保默认模板；外层垂直滚动
    /// 禁用不变（二轮修复：防贯穿全高的滚动条压底部命中数字）。
    /// </summary>
    private static Style BuildFilterFlyoutStyle(double width)
    {
        var style = new Style(typeof(FlyoutPresenter))
        {
            BasedOn = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultFlyoutPresenterStyle"],
        };
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, width));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, width));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, "Disabled"));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        return style;
    }

    /// <summary>
    /// 筛选面板 Flyout 关闭（tag-filter-tree Step 6）：退出快捷键局部抑制。Esc / 点外
    /// light-dismiss / 完成・✕ 经 Hide 的三条关闭路径都汇聚到 Closed，统一在此复位。
    /// </summary>
    private void OnFilterFlyoutClosed(object? sender, object e) => _filterFlyoutOpen = false;

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

        // 筛选面板 Flyout 打开期间局部抑制（tag-filter-tree Step 6，非模态共存）：
        // 面板开着时快捷键不打扰图库——快捷键表命中与 Ctrl+A / Enter 接管一并短路；
        // Esc 同被短路（不走 ExitApp 三态路由），交 Flyout light-dismiss 自身关闭（勿双向抢）。
        if (_filterFlyoutOpen)
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
            // Enter：图库回车进入单图（PRD 需求 4「双击或回车进入单图」，cr/P1-4）。仿 Ctrl+A
            // 接管口径：无修饰键 + 图库模式 + 已打开图库 + 快捷键表未占用（TryMatch 未命中即
            // 用户未绑定该键，绑定优先）；焦点在 TextBox 时上方已提前返回，不误触文本输入。
            else if (e.Key == VirtualKey.Enter && !control && !shift && !menu
                && ViewModel.CurrentMode == ViewerMode.Gallery
                && ViewModel.HasGallery)
            {
                e.Handled = true;
                _ = ViewModel.OpenSelectionAsSingleAsync();
            }

            return;
        }

        e.Handled = true;
        DispatchShortcut(match);
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        // 只判 Down：Locked 是 Caps/Num 类锁定键的 toggle 位，Shift/Ctrl/Alt 并无意义，
        // 但中文 IME 用 Shift 切中英文会把它置位（曾致每次点击被误判为 Shift 连选）。
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
                // 模式分流（batch-tag-management Step 7 / D9）：图库 = 删除选中集（新命令，
                // 空选中 no-op——PRD D3）；单图 = 现状删当前图（D4 验收：单图 Delete 快捷键
                // 行为不变）。快捷键保持全模式可用——删除按钮随模式隐藏（Step 6）不影响本路径
                // （PRD 不包含范围拍板）。
                if (ViewModel.CurrentMode == ViewerMode.Gallery)
                {
                    ViewModel.DeleteSelectionCommand.Execute(null);
                }
                else if (ViewModel.DeleteCommand.CanExecute(null))
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
            ApplyDialogTheme(dialog);

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
            ApplyDialogTheme(dialog);

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

    /// <summary>
    /// 标签目录选择器宿主（2026-09-19 交互重构，对照 ShowTagEditorAsync 模板）：
    /// 单图详情右栏「＋」打开；对话框期间快捷键整体屏蔽（_shortcutsEnabled）；
    /// 点选标签即关闭（TagApplied → Hide，打标异步进行不打断关闭），无主按钮（选择即动作）。
    /// </summary>
    private async Task ShowTagCatalogDialogAsync()
    {
        _shortcutsEnabled = false;
        try
        {
            var catalog = new TagCatalogDialog(ViewModel);
            var dialog = new ContentDialog
            {
                Title = "为当前图片添加标签",
                Content = catalog,
                XamlRoot = Content.XamlRoot,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            ApplyDialogTheme(dialog);

            catalog.TagApplied += dialog.Hide;
            // 单开守卫（ui/B-1 意图完备，入口清单增补）：已有对话框打开时 UIA 交错触发会令 ShowAsync
            // 抛异常冒泡（实机自检实锤场景），吞掉按已关闭处理——点选经 TagApplied → Hide，关闭路径本无后续动作。
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            _shortcutsEnabled = true;
        }
    }

    /// <summary>
    /// 批量标签目录选择器宿主（batch-tag-management Step 5，D7；对照 ShowTagCatalogDialogAsync 模板，
    /// 独立方法不动单图现签名）：图库右栏「＋ 添加标签」打开批量三态变体（Title「为选中图片添加标签」）；
    /// 对话框期间快捷键整体屏蔽（_shortcutsEnabled）；点选标签即关闭（TagApplied → Hide，
    /// 批量打标异步进行不打断关闭），无主按钮（选择即动作）。空选中由 VM 侧先行轻提示（C4），
    /// 此处不重复守卫。
    /// </summary>
    private async Task ShowSelectionTagCatalogDialogAsync()
    {
        _shortcutsEnabled = false;
        try
        {
            var catalog = new TagCatalogDialog(ViewModel, batchSelection: true);
            var dialog = new ContentDialog
            {
                Title = "为选中图片添加标签",
                Content = catalog,
                XamlRoot = Content.XamlRoot,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            ApplyDialogTheme(dialog);

            catalog.TagApplied += dialog.Hide;

            // 单开守卫：已有 ContentDialog 打开时 ShowAsync 抛异常（实机自检实锤，模态本应挡住
            // 右栏「＋」，但 UIA/自动化交错可触发）——吞异常按对话框已关闭处理，不让异常冒泡中断命令。
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // 关闭路径本无后续动作（点选标签经 TagApplied→Hide，批量打标异步进行不打断关闭）。
            }
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
        ApplyDialogTheme(dialog);

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 图库删除选中集确认对话框（batch-tag-management Step 7，对照 ConfirmDeleteAsync 注入
    /// 模式 + ConfirmUndefinedDeleteAsync 的 _shortcutsEnabled try/finally 模板）：文案含
    /// 张数与回收站提示（PRD 核心需求 5）。
    /// </summary>
    /// <param name="count">选中张数。</param>
    /// <returns>true = 继续删除。</returns>
    private async Task<bool> ConfirmDeleteSelectionAsync(int count)
    {
        _shortcutsEnabled = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "删除选中图片",
                Content = $"将 {count} 张图片移入回收站？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            ApplyDialogTheme(dialog);

            // 单开守卫：已有 ContentDialog 打开时 ShowAsync 抛异常（实机自检实锤，模态本应挡住
            // 工具栏，但 UIA/自动化交错可触发）——按用户取消处理，不让异常冒泡中断命令。
            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception)
            {
                return false;
            }
        }
        finally
        {
            _shortcutsEnabled = true;
        }
    }

    /// <summary>
    /// 未定义标签连锁删除确认对话框（batch-tag-management Step 3，对照 ConfirmDeleteAsync 注入模式 +
    /// _shortcutsEnabled try/finally）：文案含影响张数与不可逆提示（PRD B1）。
    /// </summary>
    /// <param name="tagName">未定义标签名。</param>
    /// <param name="affectedCount">影响张数（侧栏计数快照同源）。</param>
    /// <returns>true = 继续删除。</returns>
    private async Task<bool> ConfirmUndefinedDeleteAsync(string tagName, int affectedCount)
    {
        _shortcutsEnabled = false;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "连锁删除未定义标签",
                Content = $"将从 {affectedCount} 张图片的文件名移除「{tagName}」，文件将重命名且不可撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            ApplyDialogTheme(dialog);

            // 单开守卫：已有 ContentDialog 打开时 ShowAsync 抛异常（实机自检实锤，模态本应挡住
            // 工具栏，但 UIA/自动化交错可触发）——按用户取消处理，不让异常冒泡中断命令。
            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception)
            {
                return false;
            }
        }
        finally
        {
            _shortcutsEnabled = true;
        }
    }

    /// <summary>
    /// 收纳目标组选择对话框（batch-tag-management Step 3，D4）：组下拉选择 + 确认；
    /// 重名/未选择在对话框内即时提示（PrimaryButtonClick 取消关闭，不外弹）。
    /// 代码构建 UI（轻量对话框，BuildFilterFlyoutStyle 代码构建先例）；错误文字色走
    /// TagSidebarConverters.DangerTextForeground（IsDarkTheme 双值，不走 ThemeResource 运行时查找）。
    /// 返回 (GroupId, Error)：GroupId 非 null = 确认；两者均 null = 取消；Error 非 null = 对话框侧拒绝（已内联回显）。
    /// </summary>
    /// <param name="tagName">待收纳的未定义标签名。</param>
    private async Task<(string? GroupId, string? Error)> PickAbsorbGroupAsync(string tagName)
    {
        _shortcutsEnabled = false;
        try
        {
            // 组快照（对话框生命周期内配置不变——模态互斥，编辑入口都在侧栏）。
            var groups = _settingsService.Load().TagGroups;

            var errorText = new TextBlock
            {
                FontSize = 12,
                Foreground = Views.TagSidebarConverters.DangerTextForeground(),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            var groupPicker = new ComboBox
            {
                DisplayMemberPath = nameof(TagGroup.Name),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = groups,
                PlaceholderText = "选择目标标签组",
            };

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = $"在目标配置组中创建同名标签「{tagName}」——仅录入定义，不改动任何图片文件。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(groupPicker);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "收纳未定义标签",
                Content = panel,
                PrimaryButtonText = "收纳",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            ApplyDialogTheme(dialog);

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (groupPicker.SelectedItem is not TagGroup)
                {
                    errorText.Text = "请先选择目标标签组。";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                // 重名即时检查（快照口径，查全部组——ValidateTagGroups 拒绝跨组重名，B3）。
                var conflict = groups.FirstOrDefault(g => g.Tags.Any(t =>
                    string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)));
                if (conflict is not null)
                {
                    errorText.Text = $"组「{conflict.Name}」已存在同名标签「{tagName}」，请先处理该定义。";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            };

            // 单开守卫：已有 ContentDialog 打开时 ShowAsync 抛异常（实机自检实锤，模态本应挡住
            // 未定义区收纳入口，但 UIA/自动化交错可触发）——catch 返回 (null, null) 等价取消，
            // 不让异常冒泡中断命令。
            try
            {
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary && groupPicker.SelectedItem is TagGroup selected
                    ? (selected.Id, (string?)null)
                    : (null, null);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }
        finally
        {
            _shortcutsEnabled = true;
        }
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
