namespace SimpleViewer.Models;

/// <summary>
/// Parsed command-line launch parameters for initial navigation.
/// </summary>
public sealed class LaunchOptions
{
    public string? FilePath { get; init; }

    public string? DirectoryPath { get; init; }

    /// <summary>1-based index into the directory image list when <see cref="DirectoryPath"/> is set.</summary>
    public int? Index { get; init; }

    public bool ShowHelp { get; init; }
}
