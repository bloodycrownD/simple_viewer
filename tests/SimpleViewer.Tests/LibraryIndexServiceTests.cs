using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class LibraryIndexServiceTests
{
    // 每个用例独立的索引目录（临时目录注入 db 路径）；rootPath 仅参与库文件名哈希，除 T_IX_05/06 外无需真实存在。

    [Fact]
    public async Task T_IX_01_UpsertChunk_SamePathSecondWriteReplacesNotDuplicates()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);
        var path = MakePath(temp.Path, "a[风景].jpg");

        // 第一次写入
        await service.UpsertChunkAsync(new[]
        {
            MakeItem(path, "a", ".jpg", new[] { "风景" }, width: 100, height: 50),
        });

        // 同 path 第二次写入（tags/宽高不同）：应为整行替换而非重复行
        await service.UpsertChunkAsync(new[]
        {
            MakeItem(path, "a", ".jpg", new[] { "风景", "已修" }, width: 200, height: 100),
        });

        var all = await service.QueryByTagsAsync(null);
        var row = Assert.Single(all); // 幂等：仅一行
        Assert.Equal(path, row.Path);
        Assert.Equal(new[] { "风景", "已修" }, row.Tags);
        Assert.Equal(200, row.Width);
        Assert.Equal(100, row.Height);
        Assert.Equal("a", row.BaseName);
        Assert.Equal("a.jpg", row.DisplayName);
    }

    [Fact]
    public async Task T_IX_02_ReplacePathAndUpdateTags_RowUpdatedAfterRename()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);

        // 场景 1：打标即改名（path 变化）→ ReplacePath：旧行消失、新行出现且 tags/base_name 正确
        var oldPath = MakePath(temp.Path, "a[风景].jpg");
        var newPath = MakePath(temp.Path, "a[风景 已修].jpg");
        await service.UpsertChunkAsync(new[] { MakeItem(oldPath, "a", ".jpg", new[] { "风景" }) });

        await service.ReplacePathAsync(oldPath, MakeItem(newPath, "a", ".jpg", new[] { "风景", "已修" }));

        var afterRename = await service.QueryByTagsAsync(null);
        var renamed = Assert.Single(afterRename);
        Assert.Equal(newPath, renamed.Path);       // 新行以新 path 出现
        Assert.Equal(new[] { "风景", "已修" }, renamed.Tags);
        Assert.Equal("a", renamed.BaseName);       // 剥离标签段的基名正确
        Assert.DoesNotContain(afterRename, i => i.Path == oldPath); // 旧行已消失

        // 场景 2：path 未变 → UpdateTags 就地重写标签列（整列替换，行数不变）
        var plainPath = MakePath(temp.Path, "b.jpg");
        await service.UpsertChunkAsync(new[] { MakeItem(plainPath, "b", ".jpg", Array.Empty<string>()) });

        await service.UpdateTagsAsync(plainPath, new[] { "人像" });

        var afterUpdate = await service.QueryByTagsAsync(null);
        Assert.Equal(2, afterUpdate.Count); // 改名行 + 就地更新行，无重复
        var updated = Assert.Single(afterUpdate, i => i.Path == plainPath);
        Assert.Equal(new[] { "人像" }, updated.Tags);
    }

    [Fact]
    public async Task T_IX_03_QueryByTags_OrSemanticsNoSubstringFalsePositive()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);

        await service.UpsertChunkAsync(new[]
        {
            MakeItem(MakePath(temp.Path, "img1.png"), "img1", ".png", new[] { "风景" }),
            MakeItem(MakePath(temp.Path, "img2.png"), "img2", ".png", new[] { "人像" }),
            MakeItem(MakePath(temp.Path, "img10.png"), "img10", ".png", new[] { "风景", "已修" }),
            // LIKE 通配符按字面匹配的对照组：a_b/a%b/a\b 与 axb/ab 是互不相同的标签
            MakeItem(MakePath(temp.Path, "x1.png"), "x1", ".png", new[] { "a_b" }),
            MakeItem(MakePath(temp.Path, "x2.png"), "x2", ".png", new[] { "axb" }),
            MakeItem(MakePath(temp.Path, "x3.png"), "x3", ".png", new[] { "a%b" }),
            MakeItem(MakePath(temp.Path, "x4.png"), "x4", ".png", new[] { @"a\b" }),
            MakeItem(MakePath(temp.Path, "x5.png"), "x5", ".png", new[] { "ab" }),
        });

        // 子串不误命中："风"是"风景"的子串，补空格写法下不命中任何行
        Assert.Empty(await service.QueryByTagsAsync(new[] { "风" }));

        // 单标签命中多行，且按自然序返回（img1 < img10）
        var scenery = await service.QueryByTagsAsync(new[] { "风景" });
        Assert.Equal(new[] { "img1.png", "img10.png" }, scenery.Select(i => i.DisplayName).ToArray());

        // OR 语义：命中任一标签即返回（人像、已修分属不同行，无交集重复）
        var or = await service.QueryByTagsAsync(new[] { "人像", "已修" });
        Assert.Equal(new[] { "img2.png", "img10.png" }, or.Select(i => i.DisplayName).ToArray());

        // 空标签列表 → 全量
        var all = await service.QueryByTagsAsync(Array.Empty<string>());
        Assert.Equal(7, all.Count);

        // LIKE 通配符转义（cr/P2-15）："a_b" 只命中字面 a_b，不误命中 axb（无 ESCAPE 时 _ 匹配任意单字符）
        var underscore = await service.QueryByTagsAsync(new[] { "a_b" });
        var hit = Assert.Single(underscore);
        Assert.Equal("x1.png", hit.DisplayName);

        // "%" 是任意串通配符：未转义时 "a%b" 会命中 a_b/axb/ab/a%b 全部；转义后仅字面 a%b
        var percent = await service.QueryByTagsAsync(new[] { "a%b" });
        var percentHit = Assert.Single(percent);
        Assert.Equal("x3.png", percentHit.DisplayName);

        // "\" 是 ESCAPE 引导字符：转义后仅字面 a\b，不吞后续字符、不产生语法歧义命中
        var backslash = await service.QueryByTagsAsync(new[] { @"a\b" });
        var backslashHit = Assert.Single(backslash);
        Assert.Equal("x4.png", backslashHit.DisplayName);
    }

    [Fact]
    public async Task T_IX_04_TagCounts_AggregatesAcrossFilesAndTags()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);

        await service.UpsertChunkAsync(new[]
        {
            MakeItem(MakePath(temp.Path, "a.jpg"), "a", ".jpg", new[] { "风景", "已修" }),
            MakeItem(MakePath(temp.Path, "b.jpg"), "b", ".jpg", new[] { "风景" }),
            MakeItem(MakePath(temp.Path, "c.jpg"), "c", ".jpg", new[] { "风景", "人像" }),
            MakeItem(MakePath(temp.Path, "plain.png"), "plain", ".png", Array.Empty<string>()), // 无标签行不产生计数
        });

        var counts = await service.TagCountsAsync();

        Assert.Equal(3, counts.Count);
        Assert.Equal(3, counts["风景"]);
        Assert.Equal(1, counts["已修"]);
        Assert.Equal(1, counts["人像"]);
        Assert.DoesNotContain(string.Empty, counts.Keys);
    }

    [Fact]
    public async Task T_IX_05_Rebuild_RemovesOrphansAndInsertsMissingDiskItems()
    {
        using var root = new TempDirectory(); // 扫描根：真实文件
        using var idx = new TempDirectory();  // 索引目录：注入 db 路径
        File.WriteAllText(System.IO.Path.Combine(root.Path, "a.png"), "x");
        Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sub"));
        File.WriteAllText(System.IO.Path.Combine(root.Path, "sub", "b.png"), "x");
        File.WriteAllText(System.IO.Path.Combine(root.Path, "c[风景].jpg"), "x");

        using var service = new LibraryIndexService(root.Path, idx.Path, new LibraryScanService());

        // 预置 db：一行孤儿（磁盘不存在）+ 一行磁盘存在但字段过期（宽高 999，将被磁盘事实刷新）
        await service.UpsertChunkAsync(new[]
        {
            MakeItem(System.IO.Path.Combine(root.Path, "ghost.png"), "ghost", ".png", new[] { "旧标签" }, width: 1, height: 1),
            MakeItem(System.IO.Path.Combine(root.Path, "a.png"), "a", ".png", Array.Empty<string>(), width: 999, height: 999),
        });

        var result = await service.RebuildAsync(root.Path);

        Assert.Equal(3, result.Scanned); // 磁盘 3 个文件全部写入（含子目录递归）
        Assert.Equal(1, result.Removed); // ghost 孤儿行被删除

        var all = await service.QueryByTagsAsync(null);
        Assert.Equal(3, all.Count);
        Assert.DoesNotContain(all, i => i.BaseName == "ghost"); // 孤儿清除
        var a = Assert.Single(all, i => i.BaseName == "a");     // 既有行被磁盘事实刷新（"x" 非图片 → 宽高回退 0）
        Assert.Equal(0, a.Width);
        var c = Assert.Single(all, i => i.BaseName == "c");     // 磁盘新文件的文件名标签补入索引
        Assert.Equal(new[] { "风景" }, c.Tags);
        Assert.Contains(all, i => i.BaseName == "b");           // 子目录文件补入
        Assert.DoesNotContain(all, i => i.Tags.Contains("旧标签")); // 孤儿行的标签不再污染计数
    }

    [Fact]
    public async Task T_IX_06_CorruptDatabaseFile_AutoRebuildEmptyOnFirstOperation()
    {
        using var temp = new TempDirectory();
        var rootPath = System.IO.Path.Combine(temp.Path, "root");

        // 阶段 1：正常建库写入数据后关闭
        string dbPath;
        using (var first = new LibraryIndexService(rootPath, temp.Path))
        {
            dbPath = first.DatabaseFilePath;
            Assert.True(System.IO.Path.IsPathRooted(dbPath));
            await first.UpsertChunkAsync(new[]
            {
                MakeItem(MakePath(temp.Path, "a.jpg"), "a", ".jpg", new[] { "风景" }),
            });
            Assert.Single(await first.QueryByTagsAsync(null));
        }

        // 阶段 2：库文件覆盖为垃圾字节（非法 SQLite 头）
        var garbage = new byte[512];
        Array.Fill(garbage, (byte)0xAB);
        File.WriteAllBytes(dbPath, garbage);

        // 阶段 3：新实例首次操作不抛异常，得到空库，且重建后可正常读写
        using var second = new LibraryIndexService(rootPath, temp.Path);
        var all = await second.QueryByTagsAsync(null);
        Assert.Empty(all);

        var item = MakeItem(MakePath(temp.Path, "b.jpg"), "b", ".jpg", new[] { "人像" });
        await second.UpsertChunkAsync(new[] { item });

        var after = await second.QueryByTagsAsync(new[] { "人像" });
        var row = Assert.Single(after);
        Assert.Equal("b.jpg", row.DisplayName);

        var counts = await second.TagCountsAsync();
        Assert.Equal(1, counts["人像"]);
    }

    [Fact]
    public async Task T_IX_07_ClearAllItems_EmptiesTableForFullRebuild()
    {
        // 走查修复回归：重开图库清表重建，孤儿行不得残留污染候选集与计数。
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);
        await service.UpsertChunkAsync(new[]
        {
            MakeItem(MakePath(temp.Path, "a[旧名].jpg"), "a", ".jpg", new[] { "旧名" }),
            MakeItem(MakePath(temp.Path, "b.jpg"), "b", ".jpg", Array.Empty<string>()),
        });

        await service.ClearAllItemsAsync();

        Assert.Empty(await service.QueryByTagsAsync(null));
        Assert.Empty(await service.TagCountsAsync());
    }

    [Fact]
    public async Task T_IX_08_QueryUntagged_ReturnsOnlyRowsWithoutTags()
    {
        // untagged-filter-entry：无标签筛选——tags 为空的行命中，有标签的行排除。
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);
        await service.UpsertChunkAsync(new[]
        {
            MakeItem(MakePath(temp.Path, "plain1.png"), "plain1", ".png", Array.Empty<string>()),
            MakeItem(MakePath(temp.Path, "img[风景].png"), "img", ".png", new[] { "风景" }),
            MakeItem(MakePath(temp.Path, "plain10.png"), "plain10", ".png", Array.Empty<string>()),
            MakeItem(MakePath(temp.Path, "img[风景 已修].png"), "img", ".png", new[] { "风景", "已修" }),
        });

        var untagged = await service.QueryUntaggedAsync();

        // 仅无标签行命中，且按自然序返回（plain1 < plain10）
        Assert.Equal(new[] { "plain1.png", "plain10.png" }, untagged.Select(i => i.DisplayName).ToArray());
        Assert.All(untagged, i => Assert.Empty(i.Tags));
    }

    [Fact]
    public async Task T_IX_09_QueryUntagged_EmptyLibraryOrAllTaggedReturnsEmpty()
    {
        // 空库 → 空结果；全部行有标签 → 空结果（与 QueryByTags 的空集=全量语义互不影响）。
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);

        Assert.Empty(await service.QueryUntaggedAsync());

        await service.UpsertChunkAsync(new[]
        {
            MakeItem(MakePath(temp.Path, "a[风景].jpg"), "a", ".jpg", new[] { "风景" }),
        });

        Assert.Empty(await service.QueryUntaggedAsync());
    }

    [Fact]
    public async Task T_IX_10_QueryUntagged_UpdateTagsTogglesRowMembership()
    {
        // UpdateTagsAsync 清空标签后变命中；重新打标后不再命中（整列替换语义）。
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);
        var path = MakePath(temp.Path, "a[风景].jpg");
        await service.UpsertChunkAsync(new[] { MakeItem(path, "a", ".jpg", new[] { "风景" }) });

        Assert.Empty(await service.QueryUntaggedAsync()); // 有标签行不命中

        await service.UpdateTagsAsync(path, Array.Empty<string>()); // 清空标签

        var afterClear = await service.QueryUntaggedAsync();
        var row = Assert.Single(afterClear);
        Assert.Equal(path, row.Path);
        Assert.Empty(row.Tags);

        await service.UpdateTagsAsync(path, new[] { "人像" }); // 重新打标

        Assert.Empty(await service.QueryUntaggedAsync());
    }

    /// <summary>cr/P2-14：Dispose 后调用任一公共方法均抛 ObjectDisposedException（公共入口 ThrowIfDisposed 与 RunCommand lock 内复查双保险）。</summary>
    [Fact]
    public async Task T_IX_11_AfterDispose_AllPublicMethodsThrowObjectDisposedException()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);

        // 先做一次正常操作保证连接已建立，再 Dispose（二次 Dispose 幂等无害）。
        var path = MakePath(temp.Path, "a.jpg");
        await service.UpsertChunkAsync(new[] { MakeItem(path, "a", ".jpg", Array.Empty<string>()) });
        service.Dispose();

        var item = MakeItem(MakePath(temp.Path, "b.jpg"), "b", ".jpg", Array.Empty<string>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.UpsertChunkAsync(new[] { item }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RemovePathAsync(path));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ClearAllItemsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.UpdateTagsAsync(path, Array.Empty<string>()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ReplacePathAsync(path, item));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.QueryByTagsAsync(null));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.QueryUntaggedAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TagCountsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RebuildAsync(temp.Path));
    }

    /// <summary>以默认扫描服务构造被测实例（索引目录注入临时目录，rootPath 参与库文件名哈希）。</summary>
    private static LibraryIndexService CreateService(string indexDirectory, string? rootPath = null)
        => new(rootPath ?? System.IO.Path.Combine(indexDirectory, "root"), indexDirectory, new LibraryScanService());

    private static string MakePath(string directory, string fileName)
        => System.IO.Path.Combine(directory, fileName);

    /// <summary>构造索引行用的图库项（SortKey 与真实管线一致：对显示名一次性预分词）。</summary>
    private static GalleryItem MakeItem(
        string path, string baseName, string extension, string[] tags, int width = 0, int height = 0)
    {
        var displayName = baseName + extension;
        return new GalleryItem
        {
            Path = path,
            DirectoryName = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            BaseName = baseName,
            Extension = extension,
            Tags = tags,
            Width = width,
            Height = height,
            FileSizeBytes = displayName.Length,
            SortKey = GalleryItemNaturalComparer.Tokenize(displayName),
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sv-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
