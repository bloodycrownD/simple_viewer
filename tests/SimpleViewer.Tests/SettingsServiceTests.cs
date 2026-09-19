using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void T_ST_02_ValidateBindings_RejectsMoveToFolderWithoutPath()
    {
        var settings = new AppSettings
        {
            Version = 1,
            Shortcuts =
            [
                new ShortcutBinding
                {
                    VirtualKey = "D1",
                    Modifiers = ["Control"],
                    Command = ViewerCommand.MoveToFolder,
                    TargetPath = null,
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SettingsService.ValidateBindings(settings));
        Assert.Contains("目标文件夹", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T_ST_01_MissingSettingsFile_CreatesDefaultJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sv-settings-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var service = new SettingsService(settingsPath);

        var settings = service.Load();

        Assert.True(File.Exists(settingsPath));
        Assert.Equal(2, settings.Version);
        Assert.NotNull(settings.TagGroups);
        Assert.Empty(settings.TagGroups);
        Assert.Equal(7, settings.Shortcuts.Count);
        Assert.DoesNotContain(settings.Shortcuts, static b => b.Command == ViewerCommand.MoveToFolder);

        AssertShortcut(settings, "Right", [], ViewerCommand.NextImage);
        AssertShortcut(settings, "Left", [], ViewerCommand.PrevImage);
        // 2026-09-18 修复：旋转默认键让出 Ctrl+A（旧默认与图库 Ctrl+A 全选冲突，
        // 绑定优先逻辑会让全选在出厂默认下永不触发）。
        AssertShortcut(settings, "L", ["Control"], ViewerCommand.RotateLeft);
        AssertShortcut(settings, "R", ["Control"], ViewerCommand.RotateRight);
        AssertShortcut(settings, "Escape", [], ViewerCommand.ExitApp);
        AssertShortcut(settings, "F2", [], ViewerCommand.ToggleFullscreen);
        AssertShortcut(settings, "Delete", [], ViewerCommand.DeleteImage);
    }

    /// <summary>spec T-ST1：v1 配置读取 → TagGroups 空、Version 升 2 并回写磁盘。</summary>
    [Fact]
    public void T_ST_03_V1Config_MigratesToV2AndWritesBack()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var v1Json = """
                {
                  "version": 1,
                  "shortcuts": [
                    { "virtualKey": "Right", "modifiers": [], "command": "nextImage" },
                    { "virtualKey": "Delete", "modifiers": [], "command": "deleteImage" }
                  ]
                }
                """;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, v1Json);

            var settings = new SettingsService(settingsPath).Load();

            // 内存对象：Version 升 2、TagGroups 补空、既有快捷键无损。
            Assert.Equal(2, settings.Version);
            Assert.NotNull(settings.TagGroups);
            Assert.Empty(settings.TagGroups);
            Assert.Equal(2, settings.Shortcuts.Count);
            AssertShortcut(settings, "Right", [], ViewerCommand.NextImage);
            AssertShortcut(settings, "Delete", [], ViewerCommand.DeleteImage);

            // 磁盘文件：已回写 v2（再次 Load 不再触发迁移路径也能读到 v2）。
            var rewritten = File.ReadAllText(settingsPath);
            Assert.Contains("\"version\": 2", rewritten);
            Assert.Contains("\"tagGroups\": []", rewritten);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>spec T-ST2：v2 配置读写往返无损（快捷键 + 标签组共存）。</summary>
    [Fact]
    public void T_ST_04_V2Config_RoundTripsShortcutsAndTagGroups()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var original = new AppSettings
            {
                Shortcuts =
                [
                    new ShortcutBinding { VirtualKey = "Right", Modifiers = [], Command = ViewerCommand.NextImage },
                    new ShortcutBinding
                    {
                        VirtualKey = "M",
                        Modifiers = ["Control"],
                        Command = ViewerCommand.MoveToFolder,
                        TargetPath = @"C:\pictures",
                    },
                ],
                TagGroups =
                [
                    new TagGroup
                    {
                        Id = "group-1",
                        Name = "评分",
                        Exclusive = true,
                        Tags = [new TagDefinition { Id = "tag-1", Name = "好评" }],
                    },
                    new TagGroup
                    {
                        Id = "group-2",
                        Name = "主题",
                        Exclusive = false,
                        Tags =
                        [
                            new TagDefinition { Id = "tag-2", Name = "风景" },
                            new TagDefinition { Id = "tag-3", Name = "已修" },
                        ],
                    },
                ],
            };

            var service = new SettingsService(settingsPath);
            service.Save(original);
            var loaded = service.Load();

            Assert.Equal(2, loaded.Version);

            var moveBinding = Assert.Single(loaded.Shortcuts, b => b.Command == ViewerCommand.MoveToFolder);
            Assert.Equal("M", moveBinding.VirtualKey);
            Assert.Equal(@"C:\pictures", moveBinding.TargetPath);

            Assert.Equal(2, loaded.TagGroups.Count);

            Assert.Equal("group-1", loaded.TagGroups[0].Id);
            Assert.Equal("评分", loaded.TagGroups[0].Name);
            Assert.True(loaded.TagGroups[0].Exclusive);
            var rating = Assert.Single(loaded.TagGroups[0].Tags);
            Assert.Equal("tag-1", rating.Id);
            Assert.Equal("好评", rating.Name);

            Assert.Equal("group-2", loaded.TagGroups[1].Id);
            Assert.Equal("主题", loaded.TagGroups[1].Name);
            Assert.False(loaded.TagGroups[1].Exclusive);
            Assert.Equal(2, loaded.TagGroups[1].Tags.Count);
            Assert.Equal("风景", loaded.TagGroups[1].Tags[0].Name);
            Assert.Equal("已修", loaded.TagGroups[1].Tags[1].Name);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>spec T-ST3：标签组校验——组内重名/跨组重名/非法字符（空白、方括号、空名）拒绝。</summary>
    [Fact]
    public void T_ST_05_TagGroupValidation_RejectsInvalidGroups()
    {
        // 合法配置不抛异常。
        var valid = new AppSettings
        {
            TagGroups =
            [
                new TagGroup
                {
                    Id = "g1", Name = "评分", Exclusive = true,
                    Tags = [new TagDefinition { Id = "t1", Name = "好评" }],
                },
                new TagGroup
                {
                    Id = "g2", Name = "主题",
                    Tags = [new TagDefinition { Id = "t2", Name = "风景" }],
                },
            ],
        };
        Assert.Null(Record.Exception(() => SettingsService.ValidateTagGroups(valid)));

        // 组内重名。
        var inGroupDuplicate = WithGroups(new TagGroup
        {
            Id = "g1", Name = "评分",
            Tags =
            [
                new TagDefinition { Id = "t1", Name = "好评" },
                new TagDefinition { Id = "t2", Name = "好评" },
            ],
        });
        var ex = Assert.Throws<InvalidOperationException>(
            () => SettingsService.ValidateTagGroups(inGroupDuplicate));
        Assert.Contains("组内标签重名", ex.Message);

        // 跨组重名。
        var crossGroupDuplicate = WithGroups(
            new TagGroup { Id = "g1", Name = "评分", Tags = [new TagDefinition { Id = "t1", Name = "好评" }] },
            new TagGroup { Id = "g2", Name = "主题", Tags = [new TagDefinition { Id = "t2", Name = "好评" }] });
        ex = Assert.Throws<InvalidOperationException>(
            () => SettingsService.ValidateTagGroups(crossGroupDuplicate));
        Assert.Contains("跨组标签重名", ex.Message);

        // 标签空名。
        ex = Assert.Throws<InvalidOperationException>(
            () => SettingsService.ValidateTagGroups(WithGroups(
                new TagGroup { Id = "g1", Name = "评分", Tags = [new TagDefinition { Id = "t1", Name = "" }] })));
        Assert.Contains("标签名不能为空", ex.Message);

        // 空白字符全集：普通空格、全角空格（U+3000）、不换行空格（U+00A0）。
        foreach (var name in new[] { "风 景", "风\u3000景", "风\u00A0景" })
        {
            ex = Assert.Throws<InvalidOperationException>(
                () => SettingsService.ValidateTagGroups(WithGroups(
                    new TagGroup { Id = "g1", Name = "主题", Tags = [new TagDefinition { Id = "t1", Name = name }] })));
            Assert.Contains("空白字符", ex.Message);
        }

        // 方括号（TagSpaces 文件名协议保留字符）。
        foreach (var name in new[] { "[风景]", "风景]", "[风景" })
        {
            ex = Assert.Throws<InvalidOperationException>(
                () => SettingsService.ValidateTagGroups(WithGroups(
                    new TagGroup { Id = "g1", Name = "主题", Tags = [new TagDefinition { Id = "t1", Name = name }] })));
            Assert.Contains("方括号", ex.Message);
        }

        // 组名空白。
        ex = Assert.Throws<InvalidOperationException>(
            () => SettingsService.ValidateTagGroups(WithGroups(
                new TagGroup { Id = "g1", Name = "  ", Tags = [] })));
        Assert.Contains("标签组名称不能为空", ex.Message);
    }

    /// <summary>spec T-ST4：保存原子性——替换失败（目标只读）时原文件内容不被破坏、无临时文件残留。</summary>
    [Fact]
    public void T_ST_06_SaveFailure_KeepsOriginalFileIntact()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var originalJson = "{\n  \"version\": 2,\n  \"shortcuts\": [],\n  \"tagGroups\": []\n}";
            File.WriteAllText(settingsPath, originalJson);

            // 目标文件只读：临时文件写入成功但 File.Move 覆盖失败 → 模拟写入中途失败。
            File.SetAttributes(settingsPath, FileAttributes.ReadOnly);

            var service = new SettingsService(settingsPath);
            var ex = Record.Exception(() => service.Save(SettingsService.CreateDefaultSettings()));

            Assert.NotNull(ex);
            Assert.Equal(originalJson, File.ReadAllText(settingsPath));
            Assert.False(File.Exists(settingsPath + ".tmp"));
        }
        finally
        {
            // 恢复属性以便清理临时目录。
            File.SetAttributes(settingsPath, FileAttributes.Normal);
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>spec T-SK3（Step 12）：ApplyTag 绑定缺 TagId 被 ValidateBindings 拒绝（中文提示）。</summary>
    [Fact]
    public void T_ST_07_ApplyTagBindingWithoutTagId_Rejected()
    {
        var settings = WithTagGroups(new ShortcutBinding
        {
            VirtualKey = "T",
            Modifiers = [],
            Command = ViewerCommand.ApplyTag,
            TagId = null,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => SettingsService.ValidateBindings(settings));
        Assert.Contains("必须选择一个标签", ex.Message);
    }

    /// <summary>spec T-SK3（Step 12）：ApplyTag 绑定的 TagId 引用不存在的标签被拒绝（中文提示）。</summary>
    [Fact]
    public void T_ST_08_ApplyTagBindingWithUnknownTagId_Rejected()
    {
        var settings = WithTagGroups(new ShortcutBinding
        {
            VirtualKey = "T",
            Modifiers = [],
            Command = ViewerCommand.ApplyTag,
            TagId = "missing-tag",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => SettingsService.ValidateBindings(settings));
        Assert.Contains("标签不存在", ex.Message);
    }

    /// <summary>spec T-SK3 反例：ApplyTag 绑定携带合法 TagId（引用存在的标签）通过校验，不误拒。</summary>
    [Fact]
    public void T_ST_09_ApplyTagBindingWithValidTagId_PassesValidation()
    {
        var settings = WithTagGroups(
            new ShortcutBinding
            {
                VirtualKey = "T",
                Modifiers = ["Control"],
                Command = ViewerCommand.ApplyTag,
                TagId = "tag-1",
            },
            new ShortcutBinding
            {
                VirtualKey = "Number1",
                Modifiers = [],
                Command = ViewerCommand.ApplyTag,
                TagId = "tag-2",
            });

        Assert.Null(Record.Exception(() => SettingsService.ValidateBindings(settings)));
    }

    /// <summary>cr/P2-12：含重复绑定的 v1 配置——迁移回写被保存校验拒绝（重复绑定），Load 静默容忍并返回已迁移的内存对象，不抛异常。</summary>
    [Fact]
    public void T_ST_10_V1ConfigWithDuplicateBindings_LoadToleratesMigrationWriteBackFailure()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var v1Json = """
                {
                  "version": 1,
                  "shortcuts": [
                    { "virtualKey": "Right", "modifiers": [], "command": "nextImage" },
                    { "virtualKey": "Right", "modifiers": [], "command": "prevImage" }
                  ]
                }
                """;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, v1Json);

            var settings = new SettingsService(settingsPath).Load();

            // 内存对象：迁移已完成且可用（回写失败被静默吞掉，不上抛）。
            Assert.Equal(2, settings.Version);
            Assert.NotNull(settings.TagGroups);
            Assert.Empty(settings.TagGroups);
            Assert.Equal(2, settings.Shortcuts.Count);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>cr/P2-12："shortcuts": null 的 v1 损坏配置——Validate null 防御 + 迁移补空，Load 不抛且返回可直接消费的对象。</summary>
    [Fact]
    public void T_ST_11_V1ConfigWithNullShortcuts_LoadReturnsUsableSettings()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var v1Json = """
                {
                  "version": 1,
                  "shortcuts": null
                }
                """;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, v1Json);

            var settings = new SettingsService(settingsPath).Load();

            Assert.Equal(2, settings.Version);
            Assert.NotNull(settings.Shortcuts); // 补空：消费方（如 SettingsViewModel 构造器的 foreach）不 NRE
            Assert.Empty(settings.Shortcuts);
            Assert.NotNull(settings.TagGroups);
            Assert.Empty(settings.TagGroups);
        }
        finally
        {
            CleanupTempDirectory(settingsPath);
        }
    }

    /// <summary>构造含标签组配置与指定快捷键绑定的设置（标签组含 tag-1/tag-2 两个可选标签）。</summary>
    private static AppSettings WithTagGroups(params ShortcutBinding[] shortcuts)
    {
        return new AppSettings
        {
            Shortcuts = [.. shortcuts],
            TagGroups =
            [
                new TagGroup
                {
                    Id = "g1",
                    Name = "主题",
                    Tags =
                    [
                        new TagDefinition { Id = "tag-1", Name = "风景" },
                        new TagDefinition { Id = "tag-2", Name = "已修" },
                    ],
                },
            ],
        };
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

    private static AppSettings WithGroups(params TagGroup[] groups)
    {
        return new AppSettings { TagGroups = [.. groups] };
    }

    private static string CreateTempSettingsPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "sv-settings-" + Guid.NewGuid().ToString("N"),
            "settings.json");
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
