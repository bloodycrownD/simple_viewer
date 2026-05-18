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
}
