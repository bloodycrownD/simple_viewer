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

    /// <summary>解码埋点稀疏化计数（每 25 张记一次，防环形缓冲洪泛）。</summary>
    private static long _decodeMarkCounter;

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
            var fromDisk = CreateResult(path, bucket, diskHit, diskCachePath);
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
            // 埋点稀疏化（2026-09-24）：千张冷启动风暴期每张解码都打点会 6s 洪泛 200 条环形缓冲，
            // 把 scan/tag/gc 上下文全部挤掉——无响应转储变得没有诊断价值。每 25 张记一次。
            if (Interlocked.Increment(ref _decodeMarkCounter) % 25 == 1)
            {
                DiagnosticTrace.Mark($"thumb:decode {Path.GetFileName(path)}");
            }

            // 双检：排队等待期间缓存可能已被填充（如另一实例并发落盘）。
            if (TryGetMemory(cacheKey, out var memoryAgain))
            {
                return memoryAgain;
            }

            var diskAgain = await TryReadDiskCacheAsync(diskCachePath, cancellationToken).ConfigureAwait(false);
            if (diskAgain is not null)
            {
                var fromDisk = CreateResult(path, bucket, diskAgain, diskCachePath);
                AddMemory(cacheKey, fromDisk);
                return fromDisk;
            }

            var jpegBytes = _decodeOverride is not null
                ? await _decodeOverride(path, bucket, cancellationToken).ConfigureAwait(false)
                : await DecodeWithWicAsync(path, bucket, cancellationToken).ConfigureAwait(false);

            // 取消的请求不落盘（避免滚动取消污染磁盘缓存）。
            cancellationToken.ThrowIfCancellationRequested();

            await TryWriteDiskCacheAsync(diskCachePath, jpegBytes, cancellationToken).ConfigureAwait(false);

            // CachePath=UriSource 优先路径的依据（2026-09-24 首帧卡死根治）；写盘失败时 UI 层
            // File.Exists 守卫自动回退 ImageBytes 流。
            var result = CreateResult(path, bucket, jpegBytes, diskCachePath);
            AddMemory(cacheKey, result);
            return result;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>
    /// WIC 降采样解码 + JPEG q80 编码；GIF 取首帧静态图（解码器级 API 即首帧）。
    /// 解码直出 SoftwareBitmap、编码器 SetSoftwareBitmapAsync 直读（2026-09-24 LOH churn 根治）：
    /// 原管线 GetPixelDataAsync→DetachPixelData 每张分配 ~0.5-2MB 原始像素 byte[]（360/720 桶全在
    /// 大对象堆），千张冷启动 = GB 级 LOH churn → Gen2 全停 GC——实机多次无响应的现场指纹正是
    /// 「池线程解码打点与 UI 心跳同时静默」（全进程托管线程齐停，startup.log 2026-09-24 晚 7 次）。
    /// 直出后原始像素全程留在 native，托管侧只剩 JPEG 字节（缓存与 UI 应用必需，量级小一个数量级）。
    /// 全链 WinRT await 补 AsTask().ConfigureAwait(false)（2026-09-24 审计修复，对齐 ImageLoaderService
    /// 与拖拽小图链的既有口径）：裸 await 在「磁盘缓存未命中 + 解码闸门空闲」的同步完成路径下，
    /// 整链从 UI 线程发起并捕获 UI 上下文——续体全部回投 UI STA，与 2026-09-23 已根治的 WIC-on-STA
    /// 互等死锁同源（当时只修了拖拽小图链，缩略图主链漏网）；行为还随缓存命中/并发度非确定漂移。
    /// </summary>
    private static async Task<byte[]> DecodeWithWicAsync(string path, int bucket, CancellationToken cancellationToken)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 注（2026-09-26 xaml-finalizer-residuals P1-7 实证修正）：BitmapDecoder/BitmapEncoder 在本投影
        //（Microsoft.Windows.SDK.NET.Ref 10.0.19041.56）不实现 IClosable/IDisposable——无 Close/Dispose
        // 成员、Windows.Foundation.IClosable 未投影（`using` 实测 CS1674 编译失败），无法显式释放；
        // 二者非 XAML DependencyObject，GC 终结安全，不属「终结器跨线程 Release」崩溃机制的残留。
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 解码器级 API 即容器首帧（GIF 取首帧静态图与原 GetFrameAsync(0) 等价）；EXIF 方向由
        // RespectExifOrientation 在 WIC 内烘焙，与单图管线（ImageLoaderService.LoadAsync）同口径。
        var transform = CreateTransform(decoder.PixelWidth, decoder.PixelHeight, bucket);
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var encoded = new InMemoryRandomAccessStream();
            var encoderProperties = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(JpegQuality, PropertyType.Single),
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded, encoderProperties).AsTask().ConfigureAwait(false);
            // 本投影（19041）SetSoftwareBitmap 为同步成员（设置帧数据引用，实际编码在 FlushAsync）；
            // 仅支持 Rgba8/Bgra8 输入——上方解码已固定 Bgra8/Premultiplied。
            // 注（P1-7 实证修正）：BitmapEncoder 同样无 Dispose/Close（见 DecodeWithWicAsync 头注），
            // 显式释放不可实施；encoded 流（IRandomAccessStream，IDisposable）由外层 using 正常关闭。
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return await ReadStreamBytesAsync(encoded).ConfigureAwait(false);
        }
        finally
        {
            // SoftwareBitmap 具敏捷性（GC 终结合法），但显式释放更优——解码风暴期少给终结器派活。
            bitmap.Dispose();
        }
    }

    /// <summary>按目标宽度分桶缩放；源图不放大（宽不超桶宽时保持原尺寸）。
    /// 缩小插值用 Fant（2026-09-19 修复"线条毛刺"）：默认 Linear 大倍率缩小会锯齿/摩尔纹。</summary>
    private static BitmapTransform CreateTransform(uint sourceWidth, uint sourceHeight, int bucket)
    {
        var transform = new BitmapTransform();
        if (sourceWidth <= (uint)bucket)
        {
            return transform;
        }

        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        transform.ScaledWidth = (uint)bucket;
        transform.ScaledHeight = (uint)Math.Max(1, Math.Round((double)sourceHeight * bucket / sourceWidth));
        return transform;
    }

    /// <inheritdoc />
    public void MigrateCache(string oldPath, string newPath, int bucket)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath) || bucket <= 0)
        {
            return;
        }

        var oldNormalized = NormalizePath(oldPath);
        var newNormalized = NormalizePath(newPath);

        // 内存迁移（锁内，与 TryGetMemory/AddMemory 串行防并发竞态）：旧路径全部分桶条目复制到新键
        // （结果 Path 挂新路径，JPEG 字节共享引用——ThumbnailResult 不可变，安全）；
        // 旧键条目移除（路径已失效，留着白占字节预算）。
        lock (_memoryLock)
        {
            var stale = new List<KeyValuePair<CacheKey, LinkedListNode<MemoryEntry>>>();
            foreach (var pair in _memoryMap)
            {
                if (string.Equals(pair.Key.Path, oldNormalized, StringComparison.Ordinal))
                {
                    stale.Add(pair);
                }
            }

            foreach (var pair in stale)
            {
                var source = pair.Value.Value;
                var targetKey = new CacheKey(newNormalized, pair.Key.Bucket);

                // 新键若已被并发填充则替换（磁盘已改名，旧内容必过期）。
                if (_memoryMap.TryGetValue(targetKey, out var existingNode))
                {
                    _memoryOrder.Remove(existingNode);
                    _memoryMap.Remove(targetKey);
                    _memoryBytes -= existingNode.Value.Size;
                }

                _memoryOrder.Remove(pair.Value);
                _memoryMap.Remove(pair.Key);

                var migrated = new ThumbnailResult
                {
                    Path = newPath,
                    Bucket = source.Result.Bucket,
                    ImageBytes = source.Result.ImageBytes,
                    ImageSource = source.Result.ImageSource,
                };

                var node = _memoryOrder.AddFirst(new MemoryEntry(targetKey, migrated, source.Size));
                _memoryMap[targetKey] = node;
            }
        }

        // 磁盘迁移：旧 SHA1 缓存文件复制到新 SHA1 名（复制而非改名——保留旧文件无害，
        // 下次清缓存自然回收；失败静默降级，不阻塞打标主链路）。
        try
        {
            var oldDiskPath = GetDiskCachePath(new CacheKey(oldNormalized, bucket));
            if (File.Exists(oldDiskPath))
            {
                Directory.CreateDirectory(_cacheDirectory);
                File.Copy(oldDiskPath, GetDiskCachePath(new CacheKey(newNormalized, bucket)), overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 磁盘迁移失败仅损失一次命中（下次未命中解码重建），静默降级。
        }
    }

    private static ThumbnailResult CreateResult(string path, int bucket, byte[] imageBytes, string? cachePath) => new()
    {
        Path = path,
        Bucket = bucket,
        ImageBytes = imageBytes,
        CachePath = cachePath,
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
        // 同 DecodeWithWicAsync：WIC/流异步链全程不回 UI STA（2026-09-24 审计修复）。
        await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);
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
