namespace SimpleViewer.Services;

/// <summary>
/// Directory image enumeration and launch index resolution.
/// </summary>
public interface IFileBrowserService
{
    IReadOnlyList<string> GetImagesInDirectory(string directory);

    int ResolveLaunchIndex(IReadOnlyList<string> files, int oneBasedIndex);
}
