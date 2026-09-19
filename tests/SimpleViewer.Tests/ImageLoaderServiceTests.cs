using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class ImageLoaderServiceTests
{
    private static string TestImagePath =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.png");

    [Fact]
    public async Task LoadAsync_ReturnsDimensionsFromImage()
    {
        var service = new ImageLoaderService();
        var loaded = await service.LoadAsync(TestImagePath);

        Assert.Equal(1, loaded.PixelWidth);
        Assert.Equal(1, loaded.PixelHeight);
        Assert.False(loaded.IsGif);
        Assert.NotNull(loaded.DecodedPixelData);
        Assert.Equal(1, loaded.DecodedWidth);
        Assert.Equal(1, loaded.DecodedHeight);
    }

    [Fact]
    public async Task LoadAsync_SecondLoad_ReturnsCachedInstance()
    {
        var service = new ImageLoaderService();
        var first = await service.LoadAsync(TestImagePath, decodeSize: 64);
        var second = await service.LoadAsync(TestImagePath, decodeSize: 64);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ClearCache_ForcesReload()
    {
        var service = new ImageLoaderService();
        var first = await service.LoadAsync(TestImagePath);
        service.ClearCache();
        var second = await service.LoadAsync(TestImagePath);

        Assert.NotSame(first, second);
        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
    }

    [Fact]
    public async Task MigrateCache_NewPathHitsWithoutRedecode()
    {
        // 同图改名（打标重命名）缓存迁移：旧键条目迁到新键，新路径命中共享同一份解码像素，
        // 不重走磁盘与 WIC 解码；旧路径条目移除（容量不白占）。
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "sample.png");
        File.Copy(TestImagePath, sourcePath);

        var service = new ImageLoaderService();
        var first = await service.LoadAsync(sourcePath, decodeSize: 64);

        var renamedPath = Path.Combine(temp.Path, "sample[tag].png");
        File.Move(sourcePath, renamedPath); // 模拟打标改名落盘
        service.MigrateCache(sourcePath, renamedPath);

        var migrated = await service.LoadAsync(renamedPath, decodeSize: 64);

        // 命中迁移条目：解码像素数组共享引用（未重解码），元数据挂到新路径。
        Assert.Same(first.DecodedPixelData, migrated.DecodedPixelData);
        Assert.Equal(renamedPath, migrated.Path);
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
