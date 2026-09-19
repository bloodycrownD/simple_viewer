using SimpleViewer.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SimpleViewer.Tests;

public class ThumbnailServiceTests
{
    private static string TestImagePath =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.png");

    [Fact]
    public async Task T_TH_01_DiskCacheHit_ReturnsWithoutSourceAndSkipsDecode()
    {
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "sample.png");
        File.Copy(TestImagePath, imagePath);
        var cacheDir = Path.Combine(temp.Path, "thumbcache");

        var first = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir);
        var firstResult = await first.GetThumbnailAsync(imagePath, 360);

        // 输出为 JPEG（SOI 魔数 FF D8），且磁盘缓存生成 <sha1>-360.jpg。
        Assert.True(
            firstResult.ImageBytes.Length >= 2 && firstResult.ImageBytes[0] == 0xFF && firstResult.ImageBytes[1] == 0xD8,
            "缩略图应为 JPEG 字节（SOI 魔数）。");
        var cacheFile = Assert.Single(Directory.GetFiles(cacheDir));
        Assert.EndsWith("-360.jpg", Path.GetFileName(cacheFile), StringComparison.OrdinalIgnoreCase);

        // 删除源文件后：全新实例（空内存）仅凭磁盘缓存即可取回同一缩略图；
        // 若误入解码路径，将因源文件不存在抛 FileNotFoundException 导致用例失败。
        File.Delete(imagePath);
        var second = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir);
        var secondResult = await second.GetThumbnailAsync(imagePath, 360);

        Assert.Equal(imagePath, secondResult.Path);
        Assert.Equal(360, secondResult.Bucket);
        Assert.Equal(firstResult.ImageBytes, secondResult.ImageBytes);
    }

    [Fact]
    public async Task T_TH_02_MemoryLru_EvictsEntriesOverByteBudget()
    {
        using var temp = new TempDirectory();
        var pathA = Path.Combine(temp.Path, "a.png");
        var pathB = Path.Combine(temp.Path, "b.png");
        File.WriteAllText(pathA, "x");
        File.WriteAllText(pathB, "x");
        var cacheDir = Path.Combine(temp.Path, "thumbcache");

        var decodeCount = 0;
        Func<string, int, CancellationToken, Task<byte[]>> decode = (_, _, _) =>
        {
            Interlocked.Increment(ref decodeCount);
            return Task.FromResult(new byte[] { 1, 2, 3, 4 }); // 每条 4 字节
        };

        // 预算 6 字节：单条 4 字节可驻留，第二条触发对最旧条目的逐出。
        var service = new ThumbnailService(memoryBudgetBytes: 6, cacheDir, decode);

        await service.GetThumbnailAsync(pathA, 360);
        ClearCacheFiles(cacheDir);
        await service.GetThumbnailAsync(pathA, 360); // 内存命中（磁盘已剥离，未再解码）
        Assert.Equal(1, decodeCount);

        await service.GetThumbnailAsync(pathB, 360); // A 超预算被逐出
        Assert.Equal(2, decodeCount);

        // 剥离磁盘命中路径后，A 必须重新解码——证明 A 确已不在内存 LRU 中。
        ClearCacheFiles(cacheDir);
        await service.GetThumbnailAsync(pathA, 360);
        Assert.Equal(3, decodeCount);
    }

    [Fact]
    public async Task T_TH_03_ConcurrentSameKey_DecodesOnlyOnce()
    {
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "a.png");
        File.WriteAllText(imagePath, "x"); // 占位：解码由注入委托替代，仅要求源文件存在
        var cacheDir = Path.Combine(temp.Path, "thumbcache");

        var decodeCount = 0;
        Func<string, int, CancellationToken, Task<byte[]>> decode = async (_, _, _) =>
        {
            Interlocked.Increment(ref decodeCount);
            await Task.Delay(150); // 留出并发窗口
            return new byte[] { 1, 2, 3, 4 };
        };
        var service = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir, decode);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => service.GetThumbnailAsync(imagePath, 360))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, decodeCount); // 同 key 去重：仅解码一次
        Assert.All(results, r => Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.ImageBytes));
        Assert.All(results, r => Assert.Equal(imagePath, r.Path));
    }

    [Fact]
    public async Task T_TH_04_CancelledRequest_DoesNotWriteDiskCache()
    {
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "a.png");
        File.WriteAllText(imagePath, "x");
        var cacheDir = Directory.CreateDirectory(Path.Combine(temp.Path, "thumbcache")).FullName;

        Func<string, int, CancellationToken, Task<byte[]>> decode = async (_, _, _) =>
        {
            await Task.Delay(300); // 模拟不可中断的解码耗时，验证生产管线的取消点
            return new byte[] { 0xFF, 0xD8, 9 };
        };
        var service = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir, decode);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetThumbnailAsync(imagePath, 360, cts.Token));

        await Task.Delay(50); // 等待生产管线完全退出
        Assert.Empty(Directory.GetFiles(cacheDir)); // 未落任何缓存文件（含 .tmp 残留）
    }

    [Fact]
    public async Task T_TH_05_Gif_UsesFirstFrameAsStaticJpeg()
    {
        using var temp = new TempDirectory();
        var gifPath = Path.Combine(temp.Path, "anim.gif");
        await WriteTwoFrameGifAsync(gifPath); // 40x60，首帧红、次帧蓝
        var cacheDir = Path.Combine(temp.Path, "thumbcache");

        var service = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir);
        var result = await service.GetThumbnailAsync(gifPath, 16);

        // 输出为静态 JPEG（SOI 魔数 FF D8），而非 GIF 动画容器。
        Assert.True(
            result.ImageBytes.Length >= 2 && result.ImageBytes[0] == 0xFF && result.ImageBytes[1] == 0xD8,
            "GIF 缩略图应为静态 JPEG 字节。");

        await AssertJpegIsFirstFrameRedAsync(result.ImageBytes);
    }

    [Fact]
    public async Task T_TH_06_MigrateCache_NewPathHitsMemoryAndDiskWithoutRedecode()
    {
        // 同图改名（打标重命名）缓存迁移：内存条目复制到新键、磁盘缓存文件复制到新 SHA1 名——
        // 新路径取回不再解码（内存与磁盘两级均命中）。
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "a.png");
        File.WriteAllText(imagePath, "x");
        var renamedPath = Path.Combine(temp.Path, "a[tag].png");
        var cacheDir = Path.Combine(temp.Path, "thumbcache");

        var decodeCount = 0;
        Func<string, int, CancellationToken, Task<byte[]>> decode = (_, _, _) =>
        {
            Interlocked.Increment(ref decodeCount);
            return Task.FromResult(new byte[] { 1, 2, 3, 4 });
        };
        var service = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir, decode);

        await service.GetThumbnailAsync(imagePath, 360); // 建立内存 + 磁盘缓存
        service.MigrateCache(imagePath, renamedPath, 360);

        // 内存迁移命中：新路径取回不再解码，结果 Path 挂新路径。
        var memoryHit = await service.GetThumbnailAsync(renamedPath, 360);
        Assert.Equal(1, decodeCount);
        Assert.Equal(renamedPath, memoryHit.Path);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, memoryHit.ImageBytes);

        // 磁盘迁移命中：全新实例（空内存）仅凭迁移后的磁盘缓存取回，仍不触发解码。
        var second = new ThumbnailService(ThumbnailService.DefaultMemoryBudgetBytes, cacheDir, decode);
        var diskHit = await second.GetThumbnailAsync(renamedPath, 360);
        Assert.Equal(1, decodeCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, diskHit.ImageBytes);
    }

    /// <summary>解码 JPEG 输出：验证分桶缩放尺寸与首帧（红色）像素。</summary>
    private static async Task AssertJpegIsFirstFrameRedAsync(byte[] jpegBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(jpegBytes);
        await writer.StoreAsync();
        writer.DetachStream();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal(16u, decoder.PixelWidth); // 40 → 16 分桶缩放
        Assert.Equal(24u, decoder.PixelHeight); // 60 → 24 等比缩放

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var bgra = pixelData.DetachPixelData();

        // 中心像素取自首帧（红）：R 高、B 低（BGRA 排布）。
        var center = ((24 / 2) * 16 + 16 / 2) * 4;
        Assert.True(bgra[center + 2] > 200, $"首帧应为红色，实际 R={bgra[center + 2]}。");
        Assert.True(bgra[center] < 100, $"首帧应为红色，实际 B={bgra[center]}。");
    }

    /// <summary>用 WIC GIF 编码器生成两帧小图（首帧红、次帧蓝）。</summary>
    private static async Task WriteTwoFrameGifAsync(string path)
    {
        const uint width = 40;
        const uint height = 60;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96,
            FillBgraPixels(width, height, b: 0, g: 0, r: 255));
        await encoder.GoToNextFrameAsync(); // 提交首帧并切换到下一帧（FlushAsync 是最终提交，之后不可再加帧）
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96,
            FillBgraPixels(width, height, b: 255, g: 0, r: 0));
        await encoder.FlushAsync();

        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[checked((int)stream.Size)];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static byte[] FillBgraPixels(uint width, uint height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static void ClearCacheFiles(string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(cacheDir))
        {
            File.Delete(file);
        }
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
