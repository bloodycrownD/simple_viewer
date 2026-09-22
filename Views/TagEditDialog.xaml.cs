// 职责：标签/组编辑对话框内容控件 code-behind（spec Step 9，D13 模板）——按 TagEditKind 装配
//       标题/说明/输入区，TrySaveAsync 收集输入并调用执行委托（MainViewModel.ExecuteTagEditAsync）。
// 不变量：执行委托返回非 null 错误时显示于本对话框并返回 false（宿主 args.Cancel = true，不关闭）；
//         名称输入的初始值经 code-behind 在构造时写入（重命名预填旧名）；
//         对话框期间快捷键屏蔽由宿主（MainWindow._shortcutsEnabled）负责。
// 调用链：TagSidebarViewModel.ShowTagEditorAsync → MainWindow.ShowTagEditorAsync → ContentDialog
//         → TagEditDialog.TrySaveAsync → MainViewModel.ExecuteTagEditAsync（TagService/索引/SettingsService）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleViewer.ViewModels;

namespace SimpleViewer.Views;

/// <summary>
/// 标签/组编辑对话框内容控件（新建/重命名/删除/互斥切换）。
/// </summary>
public sealed partial class TagEditDialog : UserControl
{
    private readonly TagEditRequest _request;
    private readonly Func<TagEditRequest, TagEditInput, Task<string?>> _executeAsync;

    public TagEditDialog(TagEditRequest request, Func<TagEditRequest, TagEditInput, Task<string?>> executeAsync)
    {
        _request = request;
        _executeAsync = executeAsync;
        InitializeComponent();

        Title = BuildTitle(request);
        Description = BuildDescription(request);
        PrimaryButtonText = BuildPrimaryButtonText(request);
        ShowNameInput = request.Kind is TagEditKind.AddGroup or TagEditKind.AddTag
            or TagEditKind.RenameGroup or TagEditKind.RenameTag;
        ShowExclusiveInput = request.Kind == TagEditKind.AddGroup;
        NameLabel = request.Kind is TagEditKind.AddGroup or TagEditKind.RenameGroup ? "组名" : "标签名";
        // 组名是纯配置、不写入文件名（校验仅要求非空，可含空格）；仅标签名受 TagSpaces 文件名语法约束。
        NamePlaceholder = request.Kind is TagEditKind.AddGroup or TagEditKind.RenameGroup
            ? "组名仅用于标签库展示，不影响文件名"
            : "不能含空格或方括号（TagSpaces 文件名语法约束）";
        InitialName = request.Kind switch
        {
            TagEditKind.RenameTag => request.TagName,
            TagEditKind.RenameGroup => request.GroupName,
            _ => string.Empty,
        };
        InitialExclusive = false;

        // 输入初始值在元素树构建后写入（重命名预填旧名）。
        NameInput.Text = InitialName;
    }

    /// <summary>宿主 ContentDialog 标题。</summary>
    public string Title { get; }

    /// <summary>操作说明（含影响张数）。</summary>
    public string Description { get; }

    /// <summary>宿主 ContentDialog 主按钮文本。</summary>
    public string PrimaryButtonText { get; }

    /// <summary>是否显示名称输入。</summary>
    public bool ShowNameInput { get; }

    /// <summary>是否显示互斥复选（仅新建组）。</summary>
    public bool ShowExclusiveInput { get; }

    /// <summary>名称输入标签文本。</summary>
    public string NameLabel { get; }

    /// <summary>名称输入占位提示（组名与标签名的约束不同）。</summary>
    public string NamePlaceholder { get; }

    /// <summary>名称输入初始值（重命名预填）。</summary>
    public string InitialName { get; }

    /// <summary>互斥复选初始值。</summary>
    public bool InitialExclusive { get; }

    /// <summary>bool → 可见（XAML 静态函数绑定用）。</summary>
    public static Visibility VisibilityOf(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 校验并执行（宿主 PrimaryButtonClick 经 deferral 异步等待）：
    /// 返回 false 时宿主置 args.Cancel = true（对话框不关闭，错误显示于本控件）。
    /// </summary>
    public async Task<bool> TrySaveAsync()
    {
        var input = new TagEditInput
        {
            Name = NameInput.Text.Trim(),
            Exclusive = ExclusiveInput.IsChecked == true,
        };

        if (ShowNameInput && string.IsNullOrEmpty(input.Name))
        {
            ShowError("名称不能为空。");
            return false;
        }

        var error = await _executeAsync(_request, input);
        if (error is not null)
        {
            ShowError(error);
            return false;
        }

        return true;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private static string BuildTitle(TagEditRequest request) => request.Kind switch
    {
        TagEditKind.AddGroup => "新建标签组",
        TagEditKind.AddTag => $"在「{request.GroupName}」中添加标签",
        TagEditKind.RenameGroup => "重命名标签组",
        TagEditKind.RenameTag => "重命名标签",
        TagEditKind.DeleteTag => "删除标签",
        TagEditKind.DeleteGroup => $"删除标签组「{request.GroupName}」",
        TagEditKind.ToggleExclusive => request.GroupExclusive ? "切换为兼容组" : "切换为互斥组",
        _ => "标签编辑",
    };

    private static string BuildPrimaryButtonText(TagEditRequest request) => request.Kind switch
    {
        TagEditKind.DeleteTag or TagEditKind.DeleteGroup => "删除",
        TagEditKind.ToggleExclusive => "切换",
        _ => "保存",
    };

    private static string BuildDescription(TagEditRequest request) => request.Kind switch
    {
        TagEditKind.AddGroup => "新建一组标签。互斥组（组内标签单选，新标签替换旧标签）；兼容组（组内标签可共存叠加）。",
        TagEditKind.AddTag => $"在「{request.GroupName}」中添加标签。{(request.GroupExclusive ? "该组为互斥组：打标时替换组内旧标签。" : "该组为兼容组：打标时共存叠加。")}",
        TagEditKind.RenameGroup => "重命名组名。组名仅用于左栏展示，不影响任何文件名。",
        TagEditKind.RenameTag => $"「{request.TagName}」被 {request.AffectedCount} 张图片引用。\n重命名将更新这些图片的文件名（打标即改名）。",
        // 删除文案（batch-tag-management Step 2 新口径）：删除 = 仅移除定义（0 文件改名），
        // 文件上的标签保留并落入未定义标签区（其清理出口为未定义区「删除」连锁，Step 3）。
        TagEditKind.DeleteTag => $"仅移除标签定义，{request.AffectedCount} 张图片上的该标签将保留，并出现在未定义标签区。",
        TagEditKind.DeleteGroup => "仅移除该组及组内全部标签定义，图片上的标签将保留，并出现在未定义标签区。",
        TagEditKind.ToggleExclusive => request.GroupExclusive
            ? $"将「{request.GroupName}」切换为兼容组。已打上的标签不变，仅影响后续打标交互。"
            : $"将「{request.GroupName}」切换为互斥组。已打上的标签不变，此后组内打标将替换同组旧标签。",
        _ => string.Empty,
    };
}
