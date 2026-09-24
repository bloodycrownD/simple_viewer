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

        // WinRT await 全链 ConfigureAwait(false)（2026-09-23 放大/翻页顿挫修复）：本方法无任何 UI
        // 依赖，续体（含全分辨率档 ~192MB 的 DetachPixelData）留在 UI 线程是换源顿挫主源之一；
        // 调用方不配 ConfigureAwait，其续体仍回 UI 执行 VM 状态更新。
        var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var pixelWidth = (int)decoder.PixelWidth;
        var pixelHeight = (int)decoder.PixelHeight;

        SoftwareBitmap? decodedBitmap = null;
        var decodedWidth = pixelWidth;
        var decodedHeight = pixelHeight;

        if (!isGif)
        {
            var transform = CreateTransform(decoder.PixelWidth, decoder.PixelHeight, decodeSize);
            // 解码直出 SoftwareBitmap（2026-09-23 换源管线）：解码+降采样+EXIF 方向全在 WIC（池线程）
            // 完成，零中间托管数组——原 GetPixelDataAsync→DetachPixelData 的 ~192MB（全分辨率档）
            // 托管分配/拷贝消失。全链 ConfigureAwait(false)，不回 UI 线程。
            decodedBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

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
            DecodedBitmap = decodedBitmap,
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

    /// <inheritdoc />
    public void MigrateCache(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        // 同图改名迁移（打标重命名，字节未变）：锁内完成，与 LoadAsync/TryGetCached/AddToCache 串行——
        // 防 prefetch 并发读旧键或插入新键的竞态。旧键条目移除（路径已失效，留着只会白占 LRU 容量）；
        // 迁移后的新条目复制出新 LoadedImage（Path 挂新路径，命中返回的元数据口径与请求路径一致；
        // 解码位图共享引用——LoadedImage 不可变，安全）。
        // GIF 不入缓存（LoadAsync 只对非 GIF AddToCache），此处天然无操作。
        // 迁移后仍在途的旧路径 prefetch 若完成落缓存，会重新插入旧键条目——LRU 自然逐出，无害。
        lock (_cacheLock)
        {
            var staleKeys = new List<CacheKey>();
            foreach (var key in _cacheMap.Keys)
            {
                if (string.Equals(key.Path, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    staleKeys.Add(key);
                }
            }

            foreach (var oldKey in staleKeys)
            {
                var node = _cacheMap[oldKey];
                var source = node.Value.Image;
                var newKey = new CacheKey(newPath, oldKey.DecodeSize, oldKey.RotationBucket);

                // 新键若已被并发填充则替换（磁盘已改名，旧内容必过期）。
                if (_cacheMap.TryGetValue(newKey, out var existingNode))
                {
                    _cacheOrder.Remove(existingNode);
                    _cacheMap.Remove(newKey);
                }

                _cacheOrder.Remove(node);
                _cacheMap.Remove(oldKey);

                var migrated = new LoadedImage
                {
                    Path = newPath,
                    FileSizeBytes = source.FileSizeBytes,
                    IsGif = source.IsGif,
                    PixelWidth = source.PixelWidth,
                    PixelHeight = source.PixelHeight,
                    DecodedBitmap = source.DecodedBitmap,
                    DecodedWidth = source.DecodedWidth,
                    DecodedHeight = source.DecodedHeight,
                    ImageSource = source.ImageSource,
                };

                var newNode = _cacheOrder.AddFirst(new CachedImage(newKey, migrated));
                _cacheMap[newKey] = newNode;
            }

            // 迁移为"先删后插"不增条目，但与并发插入合流后仍可能超容：统一收尾逐出。
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

        // 缩小插值用 Fant（2026-09-19 修复"线条毛刺"）：WIC 默认 Linear 双线性，
        // 大倍率缩小时高频细节欠采样产生锯齿/摩尔纹；Fant 专为高质量 minification 设计。
        transform.InterpolationMode = BitmapInterpolationMode.Fant;

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
                var candidate = node.Value.Image;

                // 命中体检（2026-09-24 RO_E_CLOSED 实锤闭环）：SoftwareBitmapSource 随退役队列
                // Dispose 时会连带关闭它呈现过的位图——「来回翻页」场景（LRU 条目未被淘汰、
                // 但旧源已过保留窗口被 Drain）命中缓存拿到已关位图，换源必炸（ObjectDisposed
                // 或 SetBitmapAsync 校验失败，dump 实锤 state=Unknown/Ignore 0x0）。已关位图
                // 的属性读数不抛而回哨兵值（宽高 0），据此视为陈旧：移除条目按未命中重新解码。
                // 体检成本为 2 次属性读，覆盖 LoadAsync/EnsureFullResolutionAsync 全部命中路径；
                // MigrateCache 复制共享位图引用的条目同被保护。
                if (candidate.DecodedBitmap is { } cachedBitmap)
                {
                    var healthy = true;
                    try
                    {
                        healthy = cachedBitmap.PixelWidth > 0 && cachedBitmap.PixelHeight > 0;
                    }
                    catch (Exception)
                    {
                        healthy = false;
                    }

                    if (!healthy)
                    {
                        _cacheOrder.Remove(node);
                        _cacheMap.Remove(key);
                        image = null!;
                        return false;
                    }
                }

                _cacheOrder.Remove(node);
                _cacheOrder.AddFirst(node);
                image = candidate;
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
