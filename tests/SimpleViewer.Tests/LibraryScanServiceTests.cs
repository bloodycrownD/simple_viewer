using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class LibraryScanServiceTests
{
    private readonly LibraryScanService _service = new();

    [Fact]
    public async Task T_SC_01_RecursiveScan_IncludesNestedSubdirectories()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "root.png"), "x");
        Directory.CreateDirectory(Path.Combine(temp.Path, "sub1"));
        File.WriteAllText(Path.Combine(temp.Path, "sub1", "mid.png"), "x");
        Directory.CreateDirectory(Path.Combine(temp.Path, "sub1", "sub2"));
        File.WriteAllText(Path.Combine(temp.Path, "sub1", "sub2", "deep.png"), "x");

        var items = await ScanAllAsync(temp.Path);

        // 多层子目录（root/一级/二级）全部纳入
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Path.EndsWith("root.png", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.Path.EndsWith("mid.png", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.Path.EndsWith("deep.png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T_SC_02_InaccessibleDirectory_IgnoreInaccessibleOptionAndNoThrow()
    {
        // 真实 ACL 拒绝在无 System.IO.FileSystem.AccessControl 引用（测试 csproj 未引入）时无法稳定模拟，
        // 按任务预留口径改为验证选项传递：IgnoreInaccessible + RecurseSubdirectories 均开启（D9）。
        var options = LibraryScanService.CreateEnumerationOptions();
        Assert.True(options.IgnoreInaccessible);
        Assert.True(options.RecurseSubdirectories);

        // 配套健壮性：root 不存在时产出空序列且不抛异常（不可达目录由此吞掉而非炸掉扫描）。
        var missingRoot = Path.Combine(Path.GetTempPath(), "sv-tests-missing-" + Guid.NewGuid().ToString("N"));
        var items = await ScanAllAsync(missingRoot);
        Assert.Empty(items);
    }

    [Fact]
    public async Task T_SC_03_ExtensionWhitelist_FiltersNonImages()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "note.txt"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "doc.pdf"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "img.bmp"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "anim.webp"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "photo.png"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "upper.PNG"), "x");   // 大小写不敏感
        File.WriteAllText(Path.Combine(temp.Path, "pic.jpeg"), "x");    // .jpeg 别名

        var items = await ScanAllAsync(temp.Path);

        // 仅白名单四种扩展名出现（含大写变体）；非图片文件全部过滤
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.True(LibraryScanService.IsSupportedImageExtension(i.Extension)));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("note.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("img.bmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("anim.webp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task T_SC_04_ChunkedProgressiveYield_FirstChunkBeforeCompletion()
    {
        using var temp = new TempDirectory();
        const int total = 700; // > ChunkSize(500)：必然产生至少两块
        for (var i = 0; i < total; i++)
        {
            File.WriteAllText(Path.Combine(temp.Path, $"img{i}.png"), "x");
        }

        var progress = new ListProgress();
        var items = new List<GalleryItem>();
        var discoveredAtFirstItem = -1;

        await foreach (var item in _service.ScanAsync(temp.Path, progress))
        {
            items.Add(item);
            if (items.Count == 1)
            {
                // 渐进性证据：首项到达时，已发现数仍小于全量（枚举未结束，首块早于全量完成）
                discoveredAtFirstItem = progress.Values.Count > 0 ? progress.Values[^1] : 0;
            }
        }

        Assert.Equal(total, items.Count);

        // 进度按块上报：多次、严格递增、终值等于全量，且首报不超过一个块
        Assert.True(progress.Values.Count >= 2, "700 项应至少上报两次进度（多块产出）。");
        Assert.Equal(total, progress.Values[^1]);
        for (var i = 1; i < progress.Values.Count; i++)
        {
            Assert.True(progress.Values[i] > progress.Values[i - 1], "进度序列应严格递增。");
        }

        Assert.True(progress.Values[0] <= LibraryScanService.ChunkSize, "首报应为首个块（≤500）。");
        Assert.True(discoveredAtFirstItem < total, $"首项到达时已发现数应 < {total}，实际 {discoveredAtFirstItem}。");
    }

    [Fact]
    public async Task T_SC_05_CancellationToken_StopsEnumeration()
    {
        using var temp = new TempDirectory();
        const int total = 600;
        for (var i = 0; i < total; i++)
        {
            File.WriteAllText(Path.Combine(temp.Path, $"img{i}.jpg"), "x");
        }

        // 预取消令牌：首次枚举即抛 OperationCanceledException
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScanAllAsync(temp.Path, null, preCancelled.Token));

        // 中途取消：消费少量项后取消，枚举随即终止并抛 OperationCanceledException
        using var cts = new CancellationTokenSource();
        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _service.ScanAsync(temp.Path, null, cts.Token))
            {
                received++;
                if (received == 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(received < total, $"取消后枚举应终止（已收 {received} < {total}）。");
    }

    [Fact]
    public async Task T_SC_06_ExistingTaggedFilenames_TagsParsedAndDisplayNameStripped()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a[风景 已修].jpg"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "b[风景].png"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "plain.gif"), "x");

        var items = await ScanAllAsync(temp.Path);
        Assert.Equal(3, items.Count);

        var a = Assert.Single(items, i => i.BaseName == "a");
        Assert.Equal(new[] { "风景", "已修" }, a.Tags);
        Assert.Equal(".jpg", a.Extension);
        Assert.Equal("a.jpg", a.DisplayName); // 显示名剥离标签段

        var b = Assert.Single(items, i => i.BaseName == "b");
        Assert.Equal(new[] { "风景" }, b.Tags);
        Assert.Equal("b.png", b.DisplayName);

        var plain = Assert.Single(items, i => i.BaseName == "plain");
        Assert.Empty(plain.Tags);
        Assert.Equal("plain.gif", plain.DisplayName);
    }

    [Fact]
    public async Task T_SC_07_NaturalOrderWithinChunk_SortKeyPrecomputed()
    {
        using var temp = new TempDirectory();
        // 同一目录 4 项 < ChunkSize：单块内按自然序（img1 < img2 < img10 < img20）
        foreach (var name in new[] { "img10.png", "img2.png", "img20.png", "img1.png" })
        {
            File.WriteAllText(Path.Combine(temp.Path, name), "x");
        }

        var items = await ScanAllAsync(temp.Path);

        Assert.Equal(4, items.Count);
        Assert.Equal(
            new[] { "img1.png", "img2.png", "img10.png", "img20.png" },
            items.Select(i => i.DisplayName).ToArray());

        // 预分词 key 已缓存于项内（一次性分配，非空、可比较；按数字/非数字边界切词，与 NaturalStringComparer 口径一致）
        Assert.All(items, i => Assert.NotEmpty(i.SortKey));
        Assert.Equal(new[] { "img", "1", ".png" }, items[0].SortKey);
    }

    [Fact]
    public async Task T_SC_08_HeaderDimensionRead_PngJpegGifAndFallback()
    {
        using var temp = new TempDirectory();
        // 最小 PNG（IHDR，32x48，大端）
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,             // 签名
            0x00, 0x00, 0x00, 0x0D,                                       // IHDR 长度
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',                   // "IHDR"
            0x00, 0x00, 0x00, 0x20,                                       // 宽 = 32
            0x00, 0x00, 0x00, 0x30,                                       // 高 = 48
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "p.png"), png);

        // 最小 GIF（GIF87a 逻辑屏幕描述符，40x60，小端）
        var gif = new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a',
            40, 0x00,                                                     // 宽 = 40
            60, 0x00,                                                     // 高 = 60
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "g.gif"), gif);

        // 最小 JPEG（SOI + APP0 + SOF0，32x60，大端）
        var jpg = new byte[]
        {
            0xFF, 0xD8,                                                   // SOI
            0xFF, 0xE0, 0x00, 0x10,                                       // APP0，段长 16
            0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // "JFIF..." 14 字节 payload
            0xFF, 0xC0, 0x00, 0x0B,                                       // SOF0，段长 11
            0x08,                                                         // 精度
            0x00, 0x3C,                                                   // 高 = 60
            0x00, 0x20,                                                   // 宽 = 32
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "j.jpg"), jpg);

        // 损坏文件（无有效头）：宽高未知回退 1:1
        File.WriteAllText(Path.Combine(temp.Path, "bad.png"), "x");

        var items = await ScanAllAsync(temp.Path);
        Assert.Equal(4, items.Count);

        var p = Assert.Single(items, i => i.BaseName == "p");
        Assert.Equal(32, p.Width);
        Assert.Equal(48, p.Height);
        Assert.Equal(32.0 / 48.0, p.AspectRatio, precision: 10);
        Assert.Equal(png.Length, p.FileSizeBytes);

        var g = Assert.Single(items, i => i.BaseName == "g");
        Assert.Equal(40, g.Width);
        Assert.Equal(60, g.Height);
        Assert.Equal(gif.Length, g.FileSizeBytes);

        var j = Assert.Single(items, i => i.BaseName == "j");
        Assert.Equal(32, j.Width);
        Assert.Equal(60, j.Height);
        Assert.Equal(jpg.Length, j.FileSizeBytes);

        var bad = Assert.Single(items, i => i.BaseName == "bad");
        Assert.Equal(0, bad.Width);
        Assert.Equal(0, bad.Height);
        Assert.Equal(1.0, bad.AspectRatio, precision: 10); // 失败回退 1:1
        Assert.Equal(1, bad.FileSizeBytes);                // 文件大小仍被记录
    }

    [Fact]
    public async Task T_SC_09_JpegExifOrientation_SwapsDimensionsForRotatedValues()
    {
        using var temp = new TempDirectory();

        // APP1(Exif) 手工字节构造（cr/P1-7，T-SC8 同法扩展）：
        // SOI + APP1 + SOF0；APP1 段 = FFE1 + 段长(34 = payload 32 + 自身 2) + "Exif\0\0"(6)
        // + TIFF 头(MM/II 字节序 + 0x002A + IFD0 偏移 8)(8) + IFD0(条目数 1(2)
        // + Orientation 条目(tag 0x0112 / type SHORT / count 1 / 值内联)(12) + 下一 IFD 偏移 0(4))。
        // SOF0 存储宽高固定 32x60（横向）；Orientation 5-8（旋转 90°/270°）显示宽高应交换为 60x32，
        // 与缩略图烘焙 EXIF 旋转后的实际像素一致。
        var rotatedBig = new byte[]
        {
            0xFF, 0xD8,                                                   // SOI
            0xFF, 0xE1, 0x00, 0x22,                                       // APP1，段长 34
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,                           // "Exif\0\0"
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,               // TIFF 头：MM 大端，IFD0 @ 8
            0x00, 0x01,                                                   // IFD0 条目数 = 1
            0x01, 0x12,                                                   // tag = 0x0112（Orientation）
            0x00, 0x03,                                                   // type = SHORT(3)
            0x00, 0x00, 0x00, 0x01,                                       // count = 1
            0x00, 0x06,                                                   // value = 6（顺时针 90°）
            0x00, 0x00,                                                   // value 对齐填充
            0x00, 0x00, 0x00, 0x00,                                       // 下一 IFD 偏移 = 0
            0xFF, 0xC0, 0x00, 0x0B,                                       // SOF0，段长 11
            0x08,                                                         // 精度
            0x00, 0x3C,                                                   // 高 = 60（存储）
            0x00, 0x20,                                                   // 宽 = 32（存储）
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "rotated-big.jpg"), rotatedBig);

        // II 小端变体（Orientation=8，逆时针 90°）：tag/type/count/value 全小端字节序。
        var rotatedLittle = new byte[]
        {
            0xFF, 0xD8,                                                   // SOI
            0xFF, 0xE1, 0x00, 0x22,                                       // APP1，段长 34
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,                           // "Exif\0\0"
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,               // TIFF 头：II 小端，IFD0 @ 8
            0x00, 0x01,                                                   // IFD0 条目数 = 1
            0x12, 0x01,                                                   // tag = 0x0112（小端）
            0x03, 0x00,                                                   // type = SHORT(3)（小端）
            0x01, 0x00, 0x00, 0x00,                                       // count = 1（小端）
            0x08, 0x00,                                                   // value = 8（小端）
            0x00, 0x00,                                                   // value 对齐填充
            0x00, 0x00, 0x00, 0x00,                                       // 下一 IFD 偏移 = 0
            0xFF, 0xC0, 0x00, 0x0B,                                       // SOF0，段长 11
            0x08,                                                         // 精度
            0x00, 0x3C,                                                   // 高 = 60（存储）
            0x00, 0x20,                                                   // 宽 = 32（存储）
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "rotated-little.jpg"), rotatedLittle);

        // 对照组：Orientation=1（正常方向）不交换。
        var normal = new byte[]
        {
            0xFF, 0xD8,                                                   // SOI
            0xFF, 0xE1, 0x00, 0x22,                                       // APP1，段长 34
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,                           // "Exif\0\0"
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,               // TIFF 头：MM 大端，IFD0 @ 8
            0x00, 0x01,                                                   // IFD0 条目数 = 1
            0x01, 0x12,                                                   // tag = 0x0112（Orientation）
            0x00, 0x03,                                                   // type = SHORT(3)
            0x00, 0x00, 0x00, 0x01,                                       // count = 1
            0x00, 0x01,                                                   // value = 1（正常）
            0x00, 0x00,                                                   // value 对齐填充
            0x00, 0x00, 0x00, 0x00,                                       // 下一 IFD 偏移 = 0
            0xFF, 0xC0, 0x00, 0x0B,                                       // SOF0，段长 11
            0x08,                                                         // 精度
            0x00, 0x3C,                                                   // 高 = 60
            0x00, 0x20,                                                   // 宽 = 32
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "normal.jpg"), normal);

        var items = await ScanAllAsync(temp.Path);
        Assert.Equal(3, items.Count);

        var big = Assert.Single(items, i => i.BaseName == "rotated-big");
        Assert.Equal(60, big.Width);   // 交换：32x60 存储 → 60x32 显示
        Assert.Equal(32, big.Height);
        Assert.Equal(60.0 / 32.0, big.AspectRatio, precision: 10);

        var little = Assert.Single(items, i => i.BaseName == "rotated-little");
        Assert.Equal(60, little.Width);
        Assert.Equal(32, little.Height);
        Assert.Equal(60.0 / 32.0, little.AspectRatio, precision: 10);

        var normalItem = Assert.Single(items, i => i.BaseName == "normal");
        Assert.Equal(32, normalItem.Width); // Orientation=1 不交换
        Assert.Equal(60, normalItem.Height);
        Assert.Equal(32.0 / 60.0, normalItem.AspectRatio, precision: 10);
    }

    private async Task<List<GalleryItem>> ScanAllAsync(
        string root, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<GalleryItem>();
        await foreach (var item in _service.ScanAsync(root, progress, cancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>同步收集进度报告（不经 SynchronizationContext 投递，保证与枚举同序）。</summary>
    private sealed class ListProgress : IProgress<int>
    {
        public List<int> Values { get; } = new();

        public void Report(int value) => Values.Add(value);
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
