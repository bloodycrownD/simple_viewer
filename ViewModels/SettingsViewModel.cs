// 职责：设置页状态——快捷键列表编辑、校验与持久化。
// 不变量：保存时经 SettingsService 拒绝重复绑定；MoveToFolder 必带 TargetPath、ApplyTag 必带 TagId
//         （引用存在的标签，TagDefinition.Id 契约）；TrySave 为 load-modify-save（保留 TagGroups 等字段）。
// 调用链：SettingsPage → SettingsViewModel → ISettingsService.Save；MainWindow 打开对话框。

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.ViewModels;

/// <summary>
/// 命令下拉选项（设置页呈现）：Value = 枚举名（绑定回写用），DisplayName = 中文名（D12）。
/// </summary>
public sealed record CommandOption(string Value, string DisplayName);

/// <summary>
/// 标签下拉选项（ApplyTag 参数编辑）：TagId = 稳定 Id（绑定引用值），
/// DisplayName = “组名/标签名”（从设置 TagGroups 展平，Step 12）。
/// </summary>
public sealed record TagOption(string TagId, string DisplayName);

/// <summary>
/// 键盘快捷键设置页的视图模型。
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

        // 命令下拉（枚举名 → 中文显示名）与标签参数下拉（TagGroups 展平快照；
        // 对话框打开期间标签配置不并发变更——标签编辑入口在主窗口侧栏，与设置对话框互斥）。
        CommandOptions = Enum.GetValues<ViewerCommand>()
            .Select(c => new CommandOption(c.ToString(), ShortcutCommandNames.GetDisplayName(c)))
            .ToList();
        TagOptions = BuildTagOptions(settings.TagGroups);
    }

    public ObservableCollection<ShortcutEditorItem> Items { get; } = [];

    /// <summary>命令下拉选项（中文显示名 + 枚举名值）。</summary>
    public IReadOnlyList<CommandOption> CommandOptions { get; }

    /// <summary>标签下拉选项（ApplyTag 参数；空列表 = 用户尚未配置任何标签）。</summary>
    public IReadOnlyList<TagOption> TagOptions { get; }

    /// <summary>是否没有任何可选标签（显示引导文案）。</summary>
    public bool HasTagOptions => TagOptions.Count > 0;

    /// <summary>无标签可选的引导文案可见性。</summary>
    public bool NoTagOptionsHint => !HasTagOptions;

    public string SelectedDisplayKey => SelectedItem?.DisplayKey ?? "（未设置）";

    public string? SelectedCommandName
    {
        get => SelectedItem?.Command.ToString();
        set
        {
            if (SelectedItem is null || string.IsNullOrEmpty(value))
            {
                return;
            }

            if (Enum.TryParse<ViewerCommand>(value, out var command))
            {
                SelectedItem.Command = command;
                OnPropertyChanged(nameof(IsMoveToFolderSelected));
                OnPropertyChanged(nameof(IsApplyTagSelected));
            }
        }
    }

    /// <summary>ApplyTag 参数下拉的选中 TagId（SelectedItem.TagId 的代理）。</summary>
    public string? SelectedTagId
    {
        get => SelectedItem?.TagId;
        set
        {
            if (SelectedItem is not null && !string.IsNullOrEmpty(value))
            {
                SelectedItem.TagId = value;
            }
        }
    }

    public string SelectedTargetPath
    {
        get => SelectedItem?.TargetPath ?? string.Empty;
        set
        {
            if (SelectedItem is not null)
            {
                SelectedItem.TargetPath = value;
            }
        }
    }

    [ObservableProperty]
    private ShortcutEditorItem? _selectedItem;

    [ObservableProperty]
    private bool _isRecordingKey;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool IsMoveToFolderSelected =>
        SelectedItem?.Command == ViewerCommand.MoveToFolder;

    /// <summary>当前选中行的命令是否为 ApplyTag（显示标签参数下拉）。</summary>
    public bool IsApplyTagSelected =>
        SelectedItem?.Command == ViewerCommand.ApplyTag;

    /// <summary>Called from settings UI when command ComboBox selection changes.</summary>
    public void NotifyCommandSelectionChanged()
    {
        OnPropertyChanged(nameof(IsMoveToFolderSelected));
        OnPropertyChanged(nameof(IsApplyTagSelected));
        OnPropertyChanged(nameof(SelectedCommandName));
        OnPropertyChanged(nameof(SelectedTargetPath));
        OnPropertyChanged(nameof(SelectedTagId));
    }

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
        OnPropertyChanged(nameof(IsApplyTagSelected));
        OnPropertyChanged(nameof(SelectedDisplayKey));
        OnPropertyChanged(nameof(SelectedCommandName));
        OnPropertyChanged(nameof(SelectedTargetPath));
        OnPropertyChanged(nameof(SelectedTagId));
    }

    private ShortcutEditorItem? _subscribedItem;

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShortcutEditorItem.Command)
            or nameof(ShortcutEditorItem.TargetPath)
            or nameof(ShortcutEditorItem.TagId)
            or nameof(ShortcutEditorItem.DisplayKey))
        {
            OnPropertyChanged(nameof(IsMoveToFolderSelected));
            OnPropertyChanged(nameof(IsApplyTagSelected));
            OnPropertyChanged(nameof(SelectedDisplayKey));
            OnPropertyChanged(nameof(SelectedCommandName));
            OnPropertyChanged(nameof(SelectedTargetPath));
            OnPropertyChanged(nameof(SelectedTagId));
        }
    }

    partial void OnIsRecordingKeyChanged(bool value)
    {
        StatusMessage = value ? "请按下要绑定的按键组合…" : string.Empty;
    }

    /// <summary>
    /// 把下一次按键录制进 <see cref="SelectedItem"/>。
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
            StatusMessage = "请先在列表中选择一条快捷键。";
            return;
        }

        IsRecordingKey = true;
    }

    /// <summary>
    /// 校验并持久化绑定；校验失败返回 false（消息写入 <see cref="StatusMessage"/>，
    /// 含 ApplyTag 缺标签参数/引用不存在标签的中文提示）。
    /// </summary>
    public bool TrySave()
    {
        try
        {
            // load-modify-save：读取现设置后仅替换 Shortcuts 字段，
            // 保留 Version/TagGroups 等其他字段（避免整体重建抹掉标签组配置）。
            var settings = _settingsService.Load();
            settings.Shortcuts = Items.Select(static i => i.ToBinding()).ToList();

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

    /// <summary>从设置 TagGroups 展平标签下拉选项（“组名/标签名”显示 + 稳定 Id 值）。</summary>
    private static IReadOnlyList<TagOption> BuildTagOptions(List<TagGroup>? tagGroups)
    {
        var options = new List<TagOption>();
        if (tagGroups is null)
        {
            return options;
        }

        foreach (var group in tagGroups)
        {
            // 组名非空由 ValidateTagGroups 保证；此处防御性兜底显示名。
            var groupName = string.IsNullOrWhiteSpace(group.Name) ? "（未命名组）" : group.Name;
            foreach (var tag in group.Tags)
            {
                if (!string.IsNullOrEmpty(tag.Id))
                {
                    options.Add(new TagOption(tag.Id, $"{groupName}/{tag.Name}"));
                }
            }
        }

        return options;
    }

    private static bool IsModifierOnlyKey(string virtualKey)
    {
        return virtualKey is "Control" or "Shift" or "Menu" or "LeftWindows" or "RightWindows";
    }
}
