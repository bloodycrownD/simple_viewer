using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class FileBrowserServiceTests
{
    private readonly FileBrowserService _service = new();

    [Fact]
    public void T_FB_01_NaturalSort_OrdersImg2BeforeImg10()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "img10.jpg"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "img2.jpg"), "x");

        var files = _service.GetImagesInDirectory(temp.Path);

        Assert.Equal(2, files.Count);
        Assert.EndsWith("img2.jpg", files[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("img10.jpg", files[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T_FB_02_FiltersNonImageExtensions()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "note.txt"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "photo.png"), "x");

        var files = _service.GetImagesInDirectory(temp.Path);

        Assert.Single(files);
        Assert.EndsWith("photo.png", files[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T_FB_03_ResolveLaunchIndex_ClampsToLastWhenOutOfRange()
    {
        var files = new[] { "a.png", "b.png", "c.png" };

        var index = _service.ResolveLaunchIndex(files, 999);

        Assert.Equal(2, index);
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
