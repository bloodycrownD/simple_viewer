// 职责：瀑布流缩略图管线——WIC 降采样解码（GIF 取首帧静态图）、内存 LRU（字节预算）、磁盘 JPEG q80 缓存。
// 不变量：查找顺序 内存 LRU → 磁盘缓存 → WIC 解码；解码并发上限 4；同 key 并发请求去重共享同一 Task；取消的请求不落盘。
// 调用链：WaterfallViewModel → GetThumbnailAsync → ThumbnailResult（UI 层负责字节桥接为 ImageSource）。

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IThumbnailService" />
public sealed class ThumbnailService : IThumbnailService
{
    /// <summary>内存 LRU 默认字节预算（约 300MB）。</summary>
    public const long DefaultMemoryBudgetBytes = 300L * 1024 * 1024;

    /// <summary>WIC 解码并发上限。</summary>
    public const int MaxConcurrentDecodes = 4;

    /// <summary>磁盘缓存 JPEG 编码质量（q80）。</summary>
    public const double JpegQuality = 0.8;

    /// <summary>默认磁盘缓存目录（%LocalAppData%\SimpleViewer\thumbcache\）。</summary>
    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleViewer",
        "thumbcache");

    private readonly long _memoryBudgetBytes;
    private readonly string _cacheDirectory;
    private readonly Func<string, int, CancellationToken, Task<byte[]>>? _decodeOverride;
    private readonly SemaphoreSlim _decodeGate = new(MaxConcurrentDecodes, MaxConcurrentDecodes);
    private readonly ConcurrentDictionary<CacheKey, TaskCompletionSource<ThumbnailResult>> _inFlight = new();

    private readonly object _memoryLock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<MemoryEntry>> _memoryMap = new();
    private readonly LinkedList<MemoryEntry> _memoryOrder = new();
    private long _memoryBytes;

    /// <summary>生产默认配置实例（约 300MB 内存预算 + 默认磁盘缓存目录）。</summary>
    public ThumbnailService()
        : this(DefaultMemoryBudgetBytes, DefaultCacheDirectory)
    {
    }

    /// <summary>注入内存字节预算与磁盘缓存目录（测试/定制用）。</summary>
    public ThumbnailService(long memoryBudgetBytes, string cacheDirectory)
        : this(memoryBudgetBytes, cacheDirectory, decodeOverride: null)
    {
    }

    /// <summary>额外注入解码委托，替代内置 WIC 管线（测试用：解码计数/延迟模拟）。</summary>
    public ThumbnailService(
        long memoryBudgetBytes,
        string cacheDirectory,
        Func<string, int, CancellationToken, Task<byte[]>>? decodeOverride)
    {
        _memoryBudgetBytes = memoryBudgetBytes > 0 ? memoryBudgetBytes : DefaultMemoryBudgetBytes;
        _cacheDirectory = cacheDirectory;
        _decodeOverride = decodeOverride;
    }

    /// <inheritdoc />
    public async Task<ThumbnailResult> GetThumbnailAsync(
        string path,
        int bucket,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图片路径不能为空。", nameof(path));
        }

        if (bucket <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "分桶宽度必须为正数。");
        }

        var cacheKey = new CacheKey(NormalizePath(path), bucket);
        var diskCachePath = GetDiskCachePath(cacheKey);

        // 查找顺序：内存 LRU → 磁盘缓存 → 解码（带去重）。
        if (TryGetMemory(cacheKey, out var memoryHit))
        {
            return memoryHit;
        }

        var diskHit = await TryReadDiskCacheAsync(diskCachePath, cancellationToken).ConfigureAwait(false);
        if (diskHit is not null)
        {
            var fromDisk = CreateResult(path, bucket, diskHit);
            AddMemory(cacheKey, fromDisk);
            return fromDisk;
        }

        // 同 key 并发请求去重：owner 负责生产，follower 等待共享的同一 Task；
        // owner 中途取消时 follower 回到循环头重试（重试前先复查内存缓存）。
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetMemory(cacheKey, out var cached))
            {
                return cached;
            }

            var completion = new TaskCompletionSource<ThumbnailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = _inFlight.GetOrAdd(cacheKey, completion);
            if (!ReferenceEquals(existing, completion))
            {
                // follower：搭车 owner 的结果（自身取消不传染 owner 与其他 follower）。
                try
                {
                    return await existing.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    continue; // owner 被取消，回到循环头重试。
                }
            }

            // owner：执行生产管线（解码 → 落盘 → 回填内存）。
            try
            {
                var produced = await ProduceAsync(path, bucket, cacheKey, diskCachePath, cancellationToken).ConfigureAwait(false);
                completion.TrySetResult(produced);
                return produced;
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
                throw;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<CacheKey, TaskCompletionSource<ThumbnailResult>>(cacheKey, completion));
            }
        }
    }

    /// <summary>生产管线：源检查 → 限流 → 双检缓存 → 解码 → 取消检查 → 落盘 → 回填内存。</summary>
    private async Task<ThumbnailResult> ProduceAsync(
        string path,
        int bucket,
        CacheKey cacheKey,
        string diskCachePath,
        CancellationToken cancellationToken)
    {
        // 源文件检查放在缓存未命中之后：磁盘缓存命中时源文件已删除也能取回缩略图。
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Image file not found.", path);
        }

        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DiagnosticTrace.Mark($"thumb:decode {Path.GetFileName(path)}");
            // 双检：排队等待期间缓存可能已被填充（如另一实例并发落盘）。
            if (TryGetMemory(cacheKey, out var memoryAgain))
            {
                return memoryAgain;
            }

            var diskAgain = await TryReadDiskCacheAsync(diskCachePath, cancellationToken).ConfigureAwait(false);
            if (diskAgain is not null)
            {
                var fromDisk = CreateResult(path, bucket, diskAgain);
                AddMemory(cacheKey, fromDisk);
                return fromDisk;
            }

            var jpegBytes = _decodeOverride is not null
                ? await _decodeOverride(path, bucket, cancellationToken).ConfigureAwait(false)
                : await DecodeWithWicAsync(path, bucket, cancellationToken).ConfigureAwait(false);

            // 取消的请求不落盘（避免滚动取消污染磁盘缓存）。
            cancellationToken.ThrowIfCancellationRequested();

            await TryWriteDiskCacheAsync(diskCachePath, jpegBytes, cancellationToken).ConfigureAwait(false);

            var result = CreateResult(path, bucket, jpegBytes);
            AddMemory(cacheKey, result);
            return result;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>WIC 降采样解码 + JPEG q80 编码；GIF 取首帧静态图（GetFrameAsync(0)）。</summary>
    private static async Task<byte[]> DecodeWithWicAsync(string path, int bucket, CancellationToken cancellationToken)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();

        // GIF 一律取首帧静态图（瀑布流不做动画）；其余格式 GetFrameAsync(0) 等价于容器首帧。
        var frame = await decoder.GetFrameAsync(0);
        cancellationToken.ThrowIfCancellationRequested();

        var transform = CreateTransform(frame.PixelWidth, frame.PixelHeight, bucket);
        var pixelData = await frame.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = pixelData.DetachPixelData();
        var width = transform.ScaledWidth > 0 ? (int)transform.ScaledWidth : (int)frame.PixelWidth;
        var height = transform.ScaledHeight > 0 ? (int)transform.ScaledHeight : (int)frame.PixelHeight;

        using var encoded = new InMemoryRandomAccessStream();
        var encoderProperties = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(JpegQuality, PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded, encoderProperties);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();

        return await ReadStreamBytesAsync(encoded);
    }

    /// <summary>按目标宽度分桶缩放；源图不放大（宽不超桶宽时保持原尺寸）。</summary>
    private static BitmapTransform CreateTransform(uint sourceWidth, uint sourceHeight, int bucket)
    {
        var transform = new BitmapTransform();
        if (sourceWidth <= (uint)bucket)
        {
            return transform;
        }

        transform.ScaledWidth = (uint)bucket;
        transform.ScaledHeight = (uint)Math.Max(1, Math.Round((double)sourceHeight * bucket / sourceWidth));
        return transform;
    }

    private static ThumbnailResult CreateResult(string path, int bucket, byte[] imageBytes) => new()
    {
        Path = path,
        Bucket = bucket,
        ImageBytes = imageBytes,
    };

    private static string NormalizePath(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>磁盘缓存键 = SHA-1(全路径小写规范化) + "-" + bucket + ".jpg"。</summary>
    private string GetDiskCachePath(CacheKey cacheKey)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(cacheKey.Path)));
        return Path.Combine(_cacheDirectory, hash + "-" + cacheKey.Bucket + ".jpg");
    }

    /// <summary>读取磁盘缓存；竞态删除/占用等 IO 异常视为未命中。</summary>
    private static async Task<byte[]?> TryReadDiskCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>写磁盘缓存：临时文件 + 原子替换，避免并发读者读到半截 JPEG；失败不阻断返回。</summary>
    private async Task TryWriteDiskCacheAsync(string cachePath, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var tempPath = cachePath + ".tmp" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不阻断缩略图返回（内存缓存仍有效）。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理残留失败可忽略。
        }
    }

    private bool TryGetMemory(CacheKey key, out ThumbnailResult result)
    {
        lock (_memoryLock)
        {
            if (_memoryMap.TryGetValue(key, out var node))
            {
                _memoryOrder.Remove(node);
                _memoryOrder.AddFirst(node); // 命中即提升为最近使用。
                result = node.Value.Result;
                return true;
            }
        }

        result = null!;
        return false;
    }

    /// <summary>字节预算制 LRU：新条目入头部，总字节数超预算时从尾部（最久未用）逐出。</summary>
    private void AddMemory(CacheKey key, ThumbnailResult result)
    {
        var size = result.ImageBytes.Length;
        if (size <= 0 || size > _memoryBudgetBytes)
        {
            return; // 单条超出预算时不入缓存。
        }

        lock (_memoryLock)
        {
            if (_memoryMap.TryGetValue(key, out var existing))
            {
                _memoryOrder.Remove(existing);
                _memoryMap.Remove(key);
                _memoryBytes -= existing.Value.Size;
            }

            var node = _memoryOrder.AddFirst(new MemoryEntry(key, result, size));
            _memoryMap[key] = node;
            _memoryBytes += size;

            while (_memoryBytes > _memoryBudgetBytes && _memoryOrder.Count > 0)
            {
                var last = _memoryOrder.Last!;
                _memoryOrder.RemoveLast();
                _memoryMap.Remove(last.Value.Key);
                _memoryBytes -= last.Value.Size;
            }
        }
    }

    private static async Task<byte[]> ReadStreamBytesAsync(InMemoryRandomAccessStream stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[checked((int)stream.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private readonly record struct CacheKey(string Path, int Bucket);

    private sealed class MemoryEntry(CacheKey key, ThumbnailResult result, int size)
    {
        public CacheKey Key { get; } = key;

        public ThumbnailResult Result { get; } = result;

        public int Size { get; } = size;
    }
}
