using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class CommandLineServiceTests
{
    private readonly CommandLineService _service = new();

    [Fact]
    public void T_CLI_01_ParseFilePath()
    {
        var options = _service.Parse(["file.png"]);

        Assert.Equal("file.png", options.FilePath);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void T_CLI_02_ParseDirectoryAndIndex()
    {
        var options = _service.Parse(["-d", @"C:\pics", "-i", "2"]);

        Assert.Equal(@"C:\pics", options.DirectoryPath);
        Assert.Equal(2, options.Index);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void T_CLI_03_ParseHelpFlag()
    {
        var options = _service.Parse(["-h"]);

        Assert.True(options.ShowHelp);
        Assert.Null(options.FilePath);
        Assert.Null(options.DirectoryPath);
    }
}
