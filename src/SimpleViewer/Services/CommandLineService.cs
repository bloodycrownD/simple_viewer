// Responsibility: Parse viewer.exe CLI into LaunchOptions (file, directory, index, help).
// Invariants: Help flag short-circuits; directory+index are 1-based for launch resolution downstream.
// Call chain: App.OnLaunched → Parse → LaunchOptions → MainViewModel.InitializeAsync (phase 5).

using System.CommandLine;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// Parses command-line arguments for application launch.
/// </summary>
public sealed class CommandLineService
{
    private readonly RootCommand _rootCommand;
    private readonly Argument<string?> _fileArgument;
    private readonly Option<string?> _directoryOption;
    private readonly Option<int?> _indexOption;
    private readonly Option<bool> _helpOption;

    public CommandLineService()
    {
        _fileArgument = new Argument<string?>("file")
        {
            Description = "输入有效文件路径",
        };

        _directoryOption = new Option<string?>(new[] { "-d", "--directory" })
        {
            Description = "打开指定图片目录",
        };

        _indexOption = new Option<int?>(new[] { "-i", "--index" })
        {
            Description = "指定目录中文件的位置（1-based）",
        };

        _helpOption = new Option<bool>(new[] { "-h", "--help" })
        {
            Description = "显示本帮助信息",
        };

        _rootCommand = new RootCommand("Simple Image Viewer")
        {
            _fileArgument,
            _directoryOption,
            _indexOption,
            _helpOption,
        };
    }

    /// <summary>
    /// Root command definition (System.CommandLine) for help and binding extensions.
    /// </summary>
    public RootCommand RootCommand => _rootCommand;

    /// <summary>
    /// Parses argv into <see cref="LaunchOptions"/> via System.CommandLine.
    /// </summary>
    public LaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new LaunchOptions();
        }

        var parseResult = _rootCommand.Parse(args.ToArray());

        // beta4 API: GetValueForOption/Argument (renamed to GetValue in later 2.0 builds).
        if (parseResult.GetValueForOption(_helpOption))
        {
            return new LaunchOptions { ShowHelp = true };
        }

        return new LaunchOptions
        {
            FilePath = parseResult.GetValueForArgument(_fileArgument),
            DirectoryPath = parseResult.GetValueForOption(_directoryOption),
            Index = parseResult.GetValueForOption(_indexOption),
            ShowHelp = false,
        };
    }

    /// <summary>
    /// Help text aligned with legacy ArgsParser.show_help semantics.
    /// </summary>
    public static string GetHelpText()
    {
        return """
            图片查看器 v2.0 使用说明

            基本用法:
              viewer [文件路径]

            选项:
              -d, --directory DIR   打开指定图片目录
              -i, --index     INDEX 指定目录中文件的位置
              -h, --help            显示本帮助信息

            示例:
              viewer abc.png
              viewer -d /path/pics -i 10 打开pics目录中第十个图片
              viewer -h
            """;
    }
}
