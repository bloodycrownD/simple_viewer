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

    public CommandLineService()
    {
        var fileArgument = new Argument<string?>("file")
        {
            Description = "输入有效文件路径",
        };

        var directoryOption = new Option<string?>(new[] { "-d", "--directory" })
        {
            Description = "打开指定图片目录",
        };

        var indexOption = new Option<int?>(new[] { "-i", "--index" })
        {
            Description = "指定目录中文件的位置（1-based）",
        };

        var helpOption = new Option<bool>(new[] { "-h", "--help" })
        {
            Description = "显示本帮助信息",
        };

        _rootCommand = new RootCommand("Simple Image Viewer")
        {
            fileArgument,
            directoryOption,
            indexOption,
            helpOption,
        };
    }

    /// <summary>
    /// Root command definition (System.CommandLine) for help and future binding extensions.
    /// </summary>
    public RootCommand RootCommand => _rootCommand;

    /// <summary>
    /// Parses argv into <see cref="LaunchOptions"/>.
    /// </summary>
    public LaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new LaunchOptions();
        }

        string? filePath = null;
        string? directoryPath = null;
        int? index = null;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "-h":
                case "--help":
                    return new LaunchOptions { ShowHelp = true };
                case "-d":
                case "--directory":
                    directoryPath = ReadNextValue(args, ref i);
                    break;
                case "-i":
                case "--index":
                    var indexText = ReadNextValue(args, ref i);
                    if (int.TryParse(indexText, out var parsedIndex))
                    {
                        index = parsedIndex;
                    }

                    break;
                default:
                    if (!token.StartsWith('-') && filePath is null)
                    {
                        filePath = token;
                    }

                    break;
            }
        }

        return new LaunchOptions
        {
            FilePath = filePath,
            DirectoryPath = directoryPath,
            Index = index,
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

    private static string? ReadNextValue(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count)
        {
            return null;
        }

        index++;
        return args[index];
    }
}
