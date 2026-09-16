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

    /// <summary>
    /// spec T-SK1（Step 12）：缓存命中不读盘——文件未变更时二次 TryMatch 不再触发磁盘读。
    /// 验证探针：删除 settings.json 后再次 TryMatch 仍返回首次结果（若实现重读盘，
    /// Load 会因文件缺失重建默认配置——默认 7 条绑定不含 T 键——匹配将变为 null）。
    /// </summary>
    [Fact]
    public void T_SK_03_CacheHit_DoesNotReadDiskWhenFileUnchanged()
    {
        var settingsPath = CreateSettingsFile(new AppSettings
        {
            Shortcuts =
            [
                new ShortcutBinding { VirtualKey = "T", Modifiers = [], Command = ViewerCommand.NextImage },
            ],
        });
        try
        {
            var shortcutService = new ShortcutService(new SettingsService(settingsPath));

            var first = shortcutService.TryMatch(VirtualKey.T, control: false, shift: false, menu: false);
            Assert.NotNull(first);
            Assert.Equal(ViewerCommand.NextImage, first!.Command);

            // 文件删除（LastWriteTime 无法再变化）：缓存仍应命中，不触发重读。
            File.Delete(settingsPath);

            var second = shortcutService.TryMatch(VirtualKey.T, control: false, shift: false, menu: false);
            Assert.NotNull(second);
            Assert.Equal(ViewerCommand.NextImage, second!.Command);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>
    /// spec T-SK2（Step 12）：文件变更后缓存失效重读——修改 settings.json 后新绑定即时生效。
    /// </summary>
    [Fact]
    public void T_SK_04_SettingsFileChanged_CacheInvalidatedAndReloaded()
    {
        var settingsPath = CreateSettingsFile(new AppSettings
        {
            Shortcuts =
            [
                new ShortcutBinding { VirtualKey = "T", Modifiers = [], Command = ViewerCommand.NextImage },
            ],
        });
        try
        {
            var settingsService = new SettingsService(settingsPath);
            var shortcutService = new ShortcutService(settingsService);

            var before = shortcutService.TryMatch(VirtualKey.T, control: false, shift: false, menu: false);
            Assert.NotNull(before);
            Assert.Equal(ViewerCommand.NextImage, before!.Command);

            // 修改 settings.json（LastWriteTimeUtc 变化）：T 改绑 PrevImage。
            // 连续两次写入可能落在同一时钟粒度内（LastWriteTimeUtc 相同），显式推进时间戳
            // 构造“更晚时刻被外部修改”的文件系统状态，消除测试时序抖动。
            settingsService.Save(new AppSettings
            {
                Shortcuts =
                [
                    new ShortcutBinding { VirtualKey = "T", Modifiers = [], Command = ViewerCommand.PrevImage },
                ],
            });
            File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddSeconds(1));

            var after = shortcutService.TryMatch(VirtualKey.T, control: false, shift: false, menu: false);
            Assert.NotNull(after);
            Assert.Equal(ViewerCommand.PrevImage, after!.Command);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>
    /// Step 12 补充：ApplyTag 绑定命中时 TagId 随匹配透传（供 DispatchShortcut 分发打标入口）。
    /// </summary>
    [Fact]
    public void T_SK_05_ApplyTagBinding_MatchPassesTagIdThrough()
    {
        var settingsPath = CreateSettingsFile(new AppSettings
        {
            Shortcuts =
            [
                new ShortcutBinding
                {
                    VirtualKey = "Number1",
                    Modifiers = [],
                    Command = ViewerCommand.ApplyTag,
                    TagId = "tag-1",
                },
            ],
            TagGroups =
            [
                new TagGroup
                {
                    Id = "g1",
                    Name = "主题",
                    Tags = [new TagDefinition { Id = "tag-1", Name = "风景" }],
                },
            ],
        });
        try
        {
            var shortcutService = new ShortcutService(new SettingsService(settingsPath));

            var match = shortcutService.TryMatch(VirtualKey.Number1, control: false, shift: false, menu: false);

            Assert.NotNull(match);
            Assert.Equal(ViewerCommand.ApplyTag, match!.Command);
            Assert.Equal("tag-1", match.TagId);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
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

    private static void CleanupTempDirectory(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
