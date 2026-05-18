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
        return settings ?? CreateDefaultSettings();
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ValidateNoDuplicateBindings(settings);

        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
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
            Version = 1,
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
