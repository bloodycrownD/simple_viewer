// Responsibility: Load image files for display (decode + metadata).
// Invariants: Caller owns UI thread updates; GIF flagged via extension; decode size optional.
// Call chain: MainViewModel → LoadAsync → LoadedImage (BitmapImage in later phases).

using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IImageLoaderService" />
public sealed class ImageLoaderService : IImageLoaderService
{
    /// <inheritdoc />
    public Task<LoadedImage> LoadAsync(string path, int? decodeSize = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Image file not found.", path);
        }

        var fileInfo = new FileInfo(path);
        var isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

        // Phase 1 stub: metadata only; full WIC decode arrives in phase 2/6.
        var loaded = new LoadedImage
        {
            Path = path,
            FileSizeBytes = fileInfo.Length,
            IsGif = isGif,
            PixelWidth = 0,
            PixelHeight = 0,
            ImageSource = null,
        };

        return Task.FromResult(loaded);
    }
}
