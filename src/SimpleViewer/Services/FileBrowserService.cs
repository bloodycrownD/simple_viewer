// Responsibility: Enumerate supported images in a directory with natural sort.
// Invariants: Only .png/.jpg/.jpeg/.gif (case-insensitive); paths are full paths; order is stable.
// Call chain: CommandLineService / MainViewModel → GetImagesInDirectory → ResolveLaunchIndex.

namespace SimpleViewer.Services;

/// <inheritdoc cref="IFileBrowserService" />
public sealed class FileBrowserService : IFileBrowserService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif",
    };

    /// <inheritdoc />
    public IReadOnlyList<string> GetImagesInDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(IsSupportedImage)
            .OrderBy(static path => path, Helpers.NaturalStringComparer.Instance)
            .ToList();

        return files;
    }

    /// <inheritdoc />
    public int ResolveLaunchIndex(IReadOnlyList<string> files, int oneBasedIndex)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        // CLI index is 1-based; clamp to last file when out of range (legacy min(index-1, count-1)).
        var zeroBased = oneBasedIndex - 1;
        if (zeroBased < 0)
        {
            zeroBased = 0;
        }

        if (zeroBased >= files.Count)
        {
            zeroBased = files.Count - 1;
        }

        return zeroBased;
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension);
    }
}
