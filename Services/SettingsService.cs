// Responsibility: Persist shortcuts and app settings under LocalAppData.
// Invariants: Missing file yields defaults written once; duplicate shortcut keys rejected on Save.
// Call chain: App / SettingsViewModel → Load; ShortcutService reads bindings from Load.

using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="ISettingsService" />
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SettingsService(string? settingsFilePath = null)
    {
        SettingsFilePath = settingsFilePath ?? GetDefaultSettingsPath();
    }

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public AppSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            var defaults = CreateDefaultSettings();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(SettingsFilePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        if (settings is null)
        {
            return CreateDefaultSettings();
        }

        // v1 → v2 迁移：TagGroups 缺失（或显式 null）补空列表，版本升 2 并回写磁盘。
        if (settings.Version < 2)
        {
            settings.TagGroups ??= [];
            settings.Version = 2;
            Save(settings);
        }

        return settings;
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ValidateBindings(settings);
        ValidateTagGroups(settings);

        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 原子写：先写同目录临时文件，再整文件替换；失败时清理临时文件并抛出，原文件不受影响。
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = SettingsFilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败可容忍（固定名临时文件会被下次保存覆盖）。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 保存前的快捷键绑定校验（重复键、空键名、MoveToFolder 目标路径、ApplyTag 标签参数）。
    /// </summary>
    public static void ValidateBindings(AppSettings settings)
    {
        ValidateNoDuplicateBindings(settings);

        foreach (var binding in settings.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(binding.VirtualKey))
            {
                throw new InvalidOperationException("Each shortcut must have a key assigned.");
            }

            if (binding.Command == ViewerCommand.MoveToFolder
                && string.IsNullOrWhiteSpace(binding.TargetPath))
            {
                throw new InvalidOperationException(
                    "Move to folder shortcuts require a target folder path.");
            }

            if (binding.Command == ViewerCommand.ApplyTag)
            {
                // 模型契约：TagId 引用 TagDefinition.Id（稳定 Id）；沿用 MoveToFolder/TargetPath 校验先例。
                if (string.IsNullOrWhiteSpace(binding.TagId))
                {
                    throw new InvalidOperationException(
                        $"打标签快捷键（{binding.VirtualKey}）必须选择一个标签参数。");
                }

                var tagExists = settings.TagGroups?
                    .SelectMany(static g => g.Tags)
                    .Any(t => string.Equals(t.Id, binding.TagId, StringComparison.Ordinal)) == true;
                if (!tagExists)
                {
                    throw new InvalidOperationException(
                        $"打标签快捷键（{binding.VirtualKey}）引用的标签不存在（可能已被删除），请重新选择标签。");
                }
            }
        }
    }

    /// <summary>
    /// 校验标签组配置（保存前置）：组名非空；标签名拒绝空名、任何空白字符
    /// （char.IsWhiteSpace 全集，含全角空格与 nbsp）及方括号；组内与跨组标签重名均拒绝
    /// （文件名标签是平铺字符串，重名无法区分；Windows 文件名不区分大小写，比较忽略大小写）。
    /// </summary>
    public static void ValidateTagGroups(AppSettings settings)
    {
        if (settings.TagGroups is null)
        {
            return;
        }

        var errors = new List<string>();
        var globalTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in settings.TagGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                errors.Add("标签组名称不能为空。");
            }

            var groupName = string.IsNullOrWhiteSpace(group.Name) ? "(未命名组)" : group.Name;
            var groupTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in group.Tags)
            {
                if (string.IsNullOrEmpty(tag.Name))
                {
                    errors.Add($"标签名不能为空（组：{groupName}）。");
                    continue;
                }

                if (tag.Name.Any(char.IsWhiteSpace))
                {
                    errors.Add($"标签名不允许包含任何空白字符（组：{groupName}，标签：{tag.Name}）。");
                    continue;
                }

                if (tag.Name.Contains('[') || tag.Name.Contains(']'))
                {
                    errors.Add($"标签名不允许包含方括号（组：{groupName}，标签：{tag.Name}）。");
                    continue;
                }

                if (!groupTagNames.Add(tag.Name))
                {
                    errors.Add($"组内标签重名（组：{groupName}，标签：{tag.Name}）。");
                    continue;
                }

                if (!globalTagNames.Add(tag.Name))
                {
                    errors.Add($"跨组标签重名（标签：{tag.Name}）：文件名标签为平铺字符串，不同组之间也不允许重名。");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
    }

    /// <summary>
    /// Returns validation errors for duplicate shortcut bindings.
    /// </summary>
    public static IReadOnlyList<string> ValidateNoDuplicateBindings(AppSettings settings)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in settings.Shortcuts)
        {
            var key = BuildBindingKey(binding);
            if (!seen.Add(key))
            {
                errors.Add($"Duplicate shortcut binding: {binding.VirtualKey} with modifiers [{string.Join(", ", binding.Modifiers)}].");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        return errors;
    }

    /// <summary>
    /// Default settings path: %LocalAppData%\SimpleViewer\settings.json.
    /// </summary>
    public static string GetDefaultSettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SimpleViewer", "settings.json");
    }

    /// <summary>
    /// Factory defaults aligned with legacy viewer shortcuts (no MoveToFolder paths).
    /// </summary>
    public static AppSettings CreateDefaultSettings()
    {
        return new AppSettings
        {
            Version = 2,
            Shortcuts =
            [
                new ShortcutBinding { VirtualKey = "Right", Modifiers = [], Command = ViewerCommand.NextImage },
                new ShortcutBinding { VirtualKey = "Left", Modifiers = [], Command = ViewerCommand.PrevImage },
                new ShortcutBinding { VirtualKey = "A", Modifiers = ["Control"], Command = ViewerCommand.RotateLeft },
                new ShortcutBinding { VirtualKey = "D", Modifiers = ["Control"], Command = ViewerCommand.RotateRight },
                new ShortcutBinding { VirtualKey = "Escape", Modifiers = [], Command = ViewerCommand.ExitApp },
                new ShortcutBinding { VirtualKey = "F2", Modifiers = [], Command = ViewerCommand.ToggleFullscreen },
                new ShortcutBinding { VirtualKey = "Delete", Modifiers = [], Command = ViewerCommand.DeleteImage },
            ],
        };
    }

    internal static string BuildBindingKey(ShortcutBinding binding)
    {
        var modifiers = binding.Modifiers
            .Select(static m => m.Trim())
            .Where(static m => m.Length > 0)
            .OrderBy(static m => m, StringComparer.OrdinalIgnoreCase);

        return $"{string.Join("+", modifiers)}+{binding.VirtualKey}";
    }
}
