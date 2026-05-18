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
        Assert.Contains(settings.Shortcuts, static b => b.Command == ViewerCommand.NextImage && b.VirtualKey == "Right");
        Assert.Contains(settings.Shortcuts, static b => b.Command == ViewerCommand.DeleteImage && b.VirtualKey == "Delete");
    }
}
