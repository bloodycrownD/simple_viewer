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

    /// <summary>
    /// 同图改名缓存迁移：图片字节未变（打标重命名），把旧路径下全部解码档位（fit/全分辨率/旋转桶）的
    /// LRU 条目迁移到新路径键下——翻页回来直接命中，不重走磁盘与 WIC 解码；旧键条目移除（防白占容量的孤儿）。
    /// GIF 不入缓存，天然无操作。须在锁内完成（防 prefetch 并发竞态），无磁盘 IO。
    /// </summary>
    /// <param name="oldPath">改名前路径。</param>
    /// <param name="newPath">改名后路径。</param>
    void MigrateCache(string oldPath, string newPath);

    void ClearCache();
}
