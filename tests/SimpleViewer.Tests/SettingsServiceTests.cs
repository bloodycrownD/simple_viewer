using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void T_ST_01_MissingSettingsFile_CreatesDefaultJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sv-settings-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var service = new SettingsService(settingsPath);

        var settings = service.Load();

        Assert.True(File.Exists(settingsPath));
        Assert.Equal(1, settings.Version);
        Assert.Equal(7, settings.Shortcuts.Count);
        Assert.DoesNotContain(settings.Shortcuts, static b => b.Command == ViewerCommand.MoveToFolder);

        AssertShortcut(settings, "Right", [], ViewerCommand.NextImage);
        AssertShortcut(settings, "Left", [], ViewerCommand.PrevImage);
        AssertShortcut(settings, "A", ["Control"], ViewerCommand.RotateLeft);
        AssertShortcut(settings, "D", ["Control"], ViewerCommand.RotateRight);
        AssertShortcut(settings, "Escape", [], ViewerCommand.ExitApp);
        AssertShortcut(settings, "F2", [], ViewerCommand.ToggleFullscreen);
        AssertShortcut(settings, "Delete", [], ViewerCommand.DeleteImage);
    }

    private static void AssertShortcut(
        AppSettings settings,
        string virtualKey,
        string[] modifiers,
        ViewerCommand command)
    {
        var binding = Assert.Single(
            settings.Shortcuts,
            b => b.VirtualKey == virtualKey && b.Command == command);

        Assert.Equal(modifiers.Length, binding.Modifiers.Count);
        foreach (var modifier in modifiers)
        {
            Assert.Contains(binding.Modifiers, m => string.Equals(m, modifier, StringComparison.OrdinalIgnoreCase));
        }
    }
}
