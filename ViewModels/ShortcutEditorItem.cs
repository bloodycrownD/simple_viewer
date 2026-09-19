// 职责：设置页中可编辑的快捷键行。
// 不变量：MoveToFolder 携带 TargetPath、ApplyTag 携带 TagId（引用 TagDefinition.Id 稳定 Id）；
//         按键或修饰键变化时 DisplayKey 同步刷新。
// 调用链：SettingsViewModel ↔ ShortcutEditorItem ↔ SettingsPage 绑定。

using CommunityToolkit.Mvvm.ComponentModel;
using SimpleViewer.Helpers;
using SimpleViewer.Models;

namespace SimpleViewer.ViewModels;

/// <summary>
/// 设置编辑器中的一行快捷键绑定。
/// </summary>
public partial class ShortcutEditorItem : ObservableObject
{
    [ObservableProperty]
    private string _virtualKey = string.Empty;

    [ObservableProperty]
    private List<string> _modifiers = [];

    [ObservableProperty]
    private ViewerCommand _command;

    [ObservableProperty]
    private string? _targetPath;

    /// <summary>打标签（ApplyTag）绑定引用的标签稳定 Id（TagDefinition.Id；重命名标签不影响绑定）。</summary>
    [ObservableProperty]
    private string? _tagId;

    [ObservableProperty]
    private string _displayKey = "（未设置）";

    /// <summary>命令的中文显示名（列表与下拉呈现，D12 中文化）。</summary>
    public string CommandDisplayText => ShortcutCommandNames.GetDisplayName(Command);

    /// <summary>
    /// 由持久化绑定创建编辑行。
    /// </summary>
    public static ShortcutEditorItem FromBinding(ShortcutBinding binding)
    {
        var item = new ShortcutEditorItem
        {
            VirtualKey = binding.VirtualKey,
            Modifiers = [.. binding.Modifiers],
            Command = binding.Command,
            TargetPath = binding.TargetPath,
            TagId = binding.TagId,
        };
        item.RefreshDisplayKey();
        return item;
    }

    /// <summary>
    /// 转换回 <see cref="ShortcutBinding"/> 以持久化（参数字段仅在对应命令时保留）。
    /// </summary>
    public ShortcutBinding ToBinding()
    {
        return new ShortcutBinding
        {
            VirtualKey = VirtualKey,
            Modifiers = [.. Modifiers],
            Command = Command,
            TargetPath = Command == ViewerCommand.MoveToFolder ? TargetPath : null,
            TagId = Command == ViewerCommand.ApplyTag ? TagId : null,
        };
    }

    /// <summary>
    /// 应用设置页按键捕获 UI 录制的组合键。
    /// </summary>
    public void ApplyRecordedKey(string virtualKey, bool control, bool shift, bool menu)
    {
        VirtualKey = virtualKey;
        Modifiers = [];
        if (control)
        {
            Modifiers.Add("Control");
        }

        if (shift)
        {
            Modifiers.Add("Shift");
        }

        if (menu)
        {
            Modifiers.Add("Menu");
        }

        RefreshDisplayKey();
    }

    partial void OnVirtualKeyChanged(string value) => RefreshDisplayKey();

    partial void OnModifiersChanged(List<string> value) => RefreshDisplayKey();

    partial void OnCommandChanged(ViewerCommand value) => OnPropertyChanged(nameof(CommandDisplayText));

    private void RefreshDisplayKey()
    {
        // Helper 空键回退为英文占位符；此处显示层替换为中文（D12 中文化）。
        var formatted = ShortcutDisplayHelper.FormatBinding(Modifiers, VirtualKey);
        DisplayKey = formatted == "(none)" ? "（未设置）" : formatted;
    }
}

/// <summary>
/// 快捷键命令的中文显示名映射（设置页列表与下拉呈现；D12：UI 文案硬编码简体中文）。
/// </summary>
public static class ShortcutCommandNames
{
    private static readonly IReadOnlyDictionary<ViewerCommand, string> DisplayNames =
        new Dictionary<ViewerCommand, string>
        {
            [ViewerCommand.NextImage] = "下一张",
            [ViewerCommand.PrevImage] = "上一张",
            [ViewerCommand.RotateLeft] = "向左旋转",
            [ViewerCommand.RotateRight] = "向右旋转",
            [ViewerCommand.ToggleFullscreen] = "切换全屏",
            [ViewerCommand.DeleteImage] = "删除图片",
            [ViewerCommand.ExitApp] = "退出应用",
            [ViewerCommand.MoveToFolder] = "移动到文件夹",
            [ViewerCommand.ApplyTag] = "打标签",
        };

    /// <summary>取命令中文名（未知值回退枚举名，保证不抛异常）。</summary>
    public static string GetDisplayName(ViewerCommand command)
        => DisplayNames.TryGetValue(command, out var name) ? name : command.ToString();
}
