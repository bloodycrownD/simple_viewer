namespace SimpleViewer.Services;

/// <summary>
/// File delete (recycle bin) and move operations.
/// </summary>
public interface IFileOperationService
{
    void DeleteToRecycleBin(string path);

    void MoveToFolder(string sourcePath, string destinationDirectory);
}
