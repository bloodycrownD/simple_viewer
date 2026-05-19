// Responsibility: WIC decode via BitmapDecoder, LRU cache, and index±1 prefetch.
// Invariants: Metadata from decoder headers; downsample via BitmapTransform; GIF not cached.
// Call chain: MainViewModel → LoadAsync → LoadedImage; directory change → ClearCache.

using System.Collections.Concurrent;
using SimpleViewer.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IImageLoaderService" />
public sealed class ImageLoaderService : IImageLoaderService
{
    private const int CacheCapacity = 6;

    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CachedImage>> _cacheMap = new();
    private readonly LinkedList<CachedImage> _cacheOrder = new();
    private readonly ConcurrentDictionary<int, byte> _prefetchInFlight = new();

    /// <inheritdoc />
    public async Task<LoadedImage> LoadAsync(
        string path,
        int? decodeSize = null,
        int rotationBucket = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Image file not found.", path);
        }

        var cacheKey = new CacheKey(path, decodeSize, rotationBucket);
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var fileInfo = new FileInfo(path);
        var isGif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

        var storageFile = await StorageFile.GetFileFromPathAsync(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var pixelWidth = (int)decoder.PixelWidth;
        var pixelHeight = (int)decoder.PixelHeight;

        byte[]? decodedPixels = null;
        var decodedWidth = pixelWidth;
        var decodedHeight = pixelHeight;

        if (!isGif)
        {
            var transform = CreateTransform(decoder.PixelWidth, decoder.PixelHeight, decodeSize);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            cancellationToken.ThrowIfCancellationRequested();

            decodedPixels = pixelData.DetachPixelData();
            decodedWidth = transform.ScaledWidth > 0 ? (int)transform.ScaledWidth : pixelWidth;
            decodedHeight = transform.ScaledHeight > 0 ? (int)transform.ScaledHeight : pixelHeight;
        }

        var loaded = new LoadedImage
        {
            Path = path,
            FileSizeBytes = fileInfo.Length,
            IsGif = isGif,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            DecodedPixelData = decodedPixels,
            DecodedWidth = decodedWidth,
            DecodedHeight = decodedHeight,
            ImageSource = null,
        };

        if (!isGif)
        {
            AddToCache(cacheKey, loaded);
        }

        return loaded;
    }

    /// <inheritdoc />
    public void PrefetchAdjacent(
        IReadOnlyList<string> paths,
        int currentIndex,
        int? decodeSize = null,
        int rotationBucket = 0)
    {
        if (paths.Count == 0)
        {
            return;
        }

        foreach (var offset in new[] { -1, 1 })
        {
            var index = currentIndex + offset;
            if (index < 0 || index >= paths.Count)
            {
                continue;
            }

            var path = paths[index];
            var token = HashCode.Combine(path, decodeSize, rotationBucket);
            if (!_prefetchInFlight.TryAdd(token, 0))
            {
                continue;
            }

            _ = PrefetchOneAsync(path, decodeSize, rotationBucket, token);
        }
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cacheMap.Clear();
            _cacheOrder.Clear();
        }

        _prefetchInFlight.Clear();
    }

    private async Task PrefetchOneAsync(string path, int? decodeSize, int rotationBucket, int token)
    {
        try
        {
            await LoadAsync(path, decodeSize, rotationBucket).ConfigureAwait(false);
        }
        catch
        {
            // Prefetch failures are non-fatal.
        }
        finally
        {
            _prefetchInFlight.TryRemove(token, out _);
        }
    }

    private static BitmapTransform CreateTransform(uint sourceWidth, uint sourceHeight, int? decodeSize)
    {
        var transform = new BitmapTransform();
        if (!decodeSize.HasValue || decodeSize.Value <= 0)
        {
            return transform;
        }

        var limit = (uint)decodeSize.Value;
        var maxEdge = Math.Max(sourceWidth, sourceHeight);
        if (maxEdge <= limit)
        {
            return transform;
        }

        if (sourceWidth >= sourceHeight)
        {
            transform.ScaledWidth = limit;
            transform.ScaledHeight = (uint)Math.Max(1, (long)sourceHeight * limit / sourceWidth);
        }
        else
        {
            transform.ScaledHeight = limit;
            transform.ScaledWidth = (uint)Math.Max(1, (long)sourceWidth * limit / sourceHeight);
        }

        return transform;
    }

    private bool TryGetCached(CacheKey key, out LoadedImage image)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _cacheOrder.Remove(node);
                _cacheOrder.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    private void AddToCache(CacheKey key, LoadedImage image)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var existingNode))
            {
                _cacheOrder.Remove(existingNode);
                _cacheMap.Remove(key);
            }

            var entry = new CachedImage(key, image);
            var node = _cacheOrder.AddFirst(entry);
            _cacheMap[key] = node;

            while (_cacheOrder.Count > CacheCapacity)
            {
                var last = _cacheOrder.Last;
                if (last is null)
                {
                    break;
                }

                _cacheMap.Remove(last.Value.Key);
                _cacheOrder.RemoveLast();
            }
        }
    }

    private readonly record struct CacheKey(string Path, int? DecodeSize, int RotationBucket);

    private sealed class CachedImage(CacheKey key, LoadedImage image)
    {
        public CacheKey Key { get; } = key;

        public LoadedImage Image { get; } = image;
    }
}
