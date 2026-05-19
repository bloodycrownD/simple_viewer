// Responsibility: Safe file delete and move for the current image.
// Invariants: Delete uses recycle bin; move creates destination directory when missing.
// Call chain: MainViewModel commands → DeleteToRecycleBin / MoveToFolder.

using Microsoft.VisualBasic.FileIO;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IFileOperationService" />
public sealed class FileOperationService : IFileOperationService
{
    /// <inheritdoc />
    public void DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("File not found for recycle-bin delete.", path);
        }

        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <inheritdoc />
    public void MoveToFolder(string sourcePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(sourcePath, destinationPath);
    }
}
