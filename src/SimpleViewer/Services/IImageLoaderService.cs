using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// Asynchronous image loading and metadata extraction.
/// </summary>
public interface IImageLoaderService
{
    Task<LoadedImage> LoadAsync(string path, int? decodeSize = null, CancellationToken cancellationToken = default);
}
