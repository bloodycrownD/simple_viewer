// Responsibility: Asynchronous image load, metadata, LRU cache, and adjacent prefetch.
// Invariants: Core returns no WinUI types; GIF skips pixel decode and LRU; cache cleared on directory change.
// Call chain: MainViewModel → LoadAsync / PrefetchAdjacent / ClearCache → LoadedImage.

using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// Asynchronous image loading and metadata extraction.
/// </summary>
public interface IImageLoaderService
{
    Task<LoadedImage> LoadAsync(
        string path,
        int? decodeSize = null,
        int rotationBucket = 0,
        CancellationToken cancellationToken = default);

    void PrefetchAdjacent(
        IReadOnlyList<string> paths,
        int currentIndex,
        int? decodeSize = null,
        int rotationBucket = 0);

    void ClearCache();
}
