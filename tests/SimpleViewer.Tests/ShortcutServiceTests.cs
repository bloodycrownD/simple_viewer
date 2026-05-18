using SimpleViewer.Models;
using SimpleViewer.Services;
using Windows.System;

namespace SimpleViewer.Tests;

public class ShortcutServiceTests
{
    [Fact]
    public void T_SK_01_RightArrow_MatchesNextImage()
    {
        var settingsPath = CreateSettingsFile(new AppSettings
        {
            Version = 1,
            Shortcuts =
            [
                new ShortcutBinding { VirtualKey = "Right", Modifiers = [], Command = ViewerCommand.NextImage },
            ],
        });

        var settingsService = new SettingsService(settingsPath);
        var shortcutService = new ShortcutService(settingsService);

        var match = shortcutService.TryMatch(VirtualKey.Right, control: false, shift: false, menu: false);

        Assert.NotNull(match);
        Assert.Equal(ViewerCommand.NextImage, match!.Command);
    }

    [Fact]
    public void T_SK_02_Save_RejectsDuplicateBindings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "sv-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        var settingsService = new SettingsService(settingsPath);

        var settings = new AppSettings
        {
            Version = 1,
            Shortcuts =
            [
                new ShortcutBinding { VirtualKey = "Right", Modifiers = [], Command = ViewerCommand.NextImage },
                new ShortcutBinding { VirtualKey = "Right", Modifiers = [], Command = ViewerCommand.PrevImage },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => settingsService.Save(settings));
    }

    private static string CreateSettingsFile(AppSettings settings)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sv-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var service = new SettingsService(path);
        service.Save(settings);
        return path;
    }
}
