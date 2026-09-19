// Responsibility: 图库根目录递归分块扫描——白名单过滤、标签段解析、宽高头部快读、自然 key 预分词、块内排序渐进产出。
// Invariants: 每块 ChunkSize 项（尾部块可小）；产出序 = 块间发现顺序 + 块内自然稳定序（D15）；宽高读取失败回退 1:1；取消即时生效；可预期 IO 异常不外抛。
// Call chain: MainViewModel（Step 7“打开图库”）→ ScanAsync → Step 5 LibraryIndexService.UpsertChunk / Step 8 WaterfallView 渐进填充。

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="ILibraryScanService" />
public sealed class LibraryScanService : ILibraryScanService
{
    /// <summary>分块产出的目标块大小（块为产出单元；D9：每块约 500 项）。</summary>
    public const int ChunkSize = 500;

    /// <summary>受支持的图片扩展名白名单（含前导点、小写；匹配大小写不敏感）。Step 5 索引复用同一口径。</summary>
    public static readonly string[] SupportedImageExtensions = { ".png", ".jpg", ".jpeg", ".gif" };

    /// <summary>宽高合法上限（像素）：文件头读出的异常值视为无效，按未知处理。</summary>
    private const uint MaxDimensionPixels = 100_000;

    /// <summary>JPEG 头部扫描的最大字节数：防止损坏文件导致段循环失控。</summary>
    private const long MaxJpegHeaderScanBytes = 256 * 1024;

    private static readonly HashSet<string> s_supportedExtensions = new(SupportedImageExtensions, StringComparer.OrdinalIgnoreCase);

    private readonly ITagFilenameService _tagFilenameService;

    /// <param name="tagFilenameService">文件名标签解析服务；为 null 时使用默认 <see cref="TagFilenameService"/>。</param>
    public LibraryScanService(ITagFilenameService? tagFilenameService = null)
    {
        _tagFilenameService = tagFilenameService ?? new TagFilenameService();
    }

    /// <inheritdoc />
#pragma warning disable CS1998 // 枚举为同步磁盘 IO + yield 产出，无 await 是刻意设计（消费方 MoveNextAsync 线程上执行）
    public async IAsyncEnumerable<GalleryItem> ScanAsync(
        string root,
        IProgress<int>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // root 无效（含不存在）产出空序列而非抛异常：调用方（打开图库/重建索引）自行决定提示策略。
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var discovered = 0;
        var buffer = new List<GalleryItem>(ChunkSize);

        // 块冲刷前置（局部函数）：上报进度 + 按 key 稳定排序。
        // OrderBy 为稳定排序：同 key 项保持发现相对顺序（D15 稳定归并语义）。
        List<GalleryItem> SortAndReport(List<GalleryItem> chunk)
        {
            progress?.Report(discovered);
            return chunk.OrderBy(static i => i, GalleryItemNaturalComparer.Instance).ToList();
        }

        // EnumerateFiles 的磁盘 IO 在消费方 MoveNextAsync 线程上同步执行（几十万项的小 IO，无需线程池化）；
        // IgnoreInaccessible：不可访问目录静默跳过；RecurseSubdirectories：递归全部子目录（D9）。
        foreach (var filePath in Directory.EnumerateFiles(root, "*", CreateEnumerationOptions()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupportedImageExtension(Path.GetExtension(filePath)))
            {
                continue;
            }

            // 枚举与打开之间的竞态：文件消失时返回 null，跳过该项。
            var item = TryCreateItem(filePath);
            if (item is null)
            {
                continue;
            }

            discovered++;
            buffer.Add(item);

            if (buffer.Count < ChunkSize)
            {
                continue;
            }

            foreach (var ordered in SortAndReport(buffer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ordered;
            }

            buffer.Clear();
        }

        if (buffer.Count > 0)
        {
            foreach (var ordered in SortAndReport(buffer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ordered;
            }
        }
    }
#pragma warning restore CS1998

    /// <summary>
    /// 构造枚举选项（D9）：忽略不可访问目录 + 递归子目录。
    /// 独立暴露以便测试与调用方校验选项口径。
    /// </summary>
    public static EnumerationOptions CreateEnumerationOptions() => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
    };

    /// <summary>判断扩展名（含前导点）是否属于图片白名单（大小写不敏感）。</summary>
    public static bool IsSupportedImageExtension(string? extension)
        => !string.IsNullOrEmpty(extension) && s_supportedExtensions.Contains(extension);

    /// <summary>
    /// 为单个文件构建 <see cref="GalleryItem"/>：一次文件打开同时取长度与头部宽高（不整文件解码）。
    /// 返回 null 表示文件在枚举后消失（竞态），应跳过。
    /// </summary>
    private GalleryItem? TryCreateItem(string filePath)
    {
        var fileSize = 0L;
        var width = 0;
        var height = 0;

        try
        {
            // bufferSize 取小值：只读头部少量字节（SequentialScan 提示顺序预读）。
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128, FileOptions.SequentialScan);
            fileSize = stream.Length;
            (width, height) = ReadImageDimensions(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被占用/无权限但仍存在的文件：保留项（宽高未知回退 1:1），大小尽力补查。
            if (!File.Exists(filePath))
            {
                return null;
            }
        }

        if (fileSize <= 0)
        {
            fileSize = TryQueryFileSize(filePath);
        }

        _tagFilenameService.TryParse(Path.GetFileName(filePath), out var baseName, out var extension, out var tags);
        var displayName = baseName + extension;

        return new GalleryItem
        {
            Path = filePath,
            DirectoryName = Path.GetDirectoryName(filePath) ?? string.Empty,
            BaseName = baseName,
            Extension = extension,
            Tags = tags,
            Width = width,
            Height = height,
            FileSizeBytes = fileSize,
            // 预分词 key 一次性分配缓存于项内，块内排序与后续消费不再重复 Tokenize（D9）。
            SortKey = GalleryItemNaturalComparer.Tokenize(displayName),
        };
    }

    private static long TryQueryFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 依据文件头魔数分派格式并读取宽高：PNG（IHDR）/ GIF（逻辑屏幕描述符）/ JPEG（首个 SOF 段）。
    /// 任何解析失败返回 (0, 0)（调用方回退 1:1），不抛异常。
    /// </summary>
    private static (int Width, int Height) ReadImageDimensions(FileStream stream)
    {
        try
        {
            // 24 字节覆盖 PNG 签名(8) + IHDR 头(4+4) + 宽高(8)；GIF/JPEG 只需前 10/2 字节，短文件按实际读取数判断。
            Span<byte> header = stackalloc byte[24];
            var read = Fill(stream, header);

            // PNG：89 50 4E 47 0D 0A 1A 0A + IHDR chunk，宽高为大端 uint @16/@20。
            if (read >= 24
                && header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G'
                && header[12] == (byte)'I' && header[13] == (byte)'H' && header[14] == (byte)'D' && header[15] == (byte)'R')
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
                return IsValidDimension(w, h) ? ((int)w, (int)h) : (0, 0);
            }

            // GIF：“GIF8xa” + 逻辑屏幕描述符，宽高为小端 ushort @6/@8。
            if (read >= 10
                && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
            {
                var w = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
                var h = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);
                return IsValidDimension(w, h) ? (w, h) : (0, 0);
            }

            // JPEG：FF D8 之后逐段扫描首个 SOF。
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            {
                stream.Seek(2, SeekOrigin.Begin);
                return ReadJpegDimensions(stream);
            }
        }
        catch (IOException)
        {
            // 头部读取失败按未知处理。
        }

        return (0, 0);
    }

    /// <summary>
    /// JPEG 段扫描：跳过 APPn 等段，定位首个 SOF（C0-CF 中除 C4/C8/CC）读取精度+高+宽；
    /// 途经首个 APP1(Exif) 段时解析 Orientation 标签（0x0112，cr/P1-7）——值 5-8 表示存储帧需
    /// 旋转 90°/270° 才是显示方向，返回前交换宽高，使 AspectRatio/瀑布流卡片槽与缩略图
    /// （ThumbnailService 已烘焙 EXIF 旋转）的实际像素比例一致；PNG/GIF 无此协议不处理。
    /// </summary>
    private static (int Width, int Height) ReadJpegDimensions(FileStream stream)
    {
        // 循环外一次性分配：[0..2) 兼作段长缓冲，[0..5) 兼作 SOF payload 缓冲（CA2014：循环内不 stackalloc）。
        Span<byte> scratch = stackalloc byte[5];
        var scanned = 0L;
        var orientation = 0; // 首个 APP1(Exif) 的 Orientation（0 = 无/未知/解析失败，不旋转）。
        while (scanned < MaxJpegHeaderScanBytes)
        {
            var lead = stream.ReadByte();
            scanned++;
            if (lead < 0)
            {
                return (0, 0);
            }

            if (lead != 0xFF)
            {
                // 段间出现非 0xFF 字节：结构损坏，放弃。
                return (0, 0);
            }

            // 允许多个 0xFF 填充；0x00 为熵数据转义（SOS 前不应出现），同样放弃。
            int marker;
            do
            {
                marker = stream.ReadByte();
                scanned++;
                if (marker < 0)
                {
                    return (0, 0);
                }
            }
            while (marker == 0xFF);

            if (marker == 0x00)
            {
                return (0, 0);
            }

            // 独立标记（无长度段）：TEM / RSTn / EOI，直接继续找下一标记。
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xD9)
            {
                continue;
            }

            // SOS（熵编码开始）：此前未见 SOF，宽高不可得。
            if (marker == 0xDA)
            {
                return (0, 0);
            }

            // SOF 集：C0-CF 中排除 C4(DHT)/C8(JPG)/CC(DAC)。
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            Span<byte> lengthBytes = scratch[..2];
            if (Fill(stream, lengthBytes) < 2)
            {
                return (0, 0);
            }

            scanned += 2;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2)
            {
                return (0, 0);
            }

            if (isStartOfFrame)
            {
                // SOF payload 前 5 字节：精度(1) + 高(2) + 宽(2)。
                Span<byte> sof = scratch[..5];
                if (Fill(stream, sof) < 5)
                {
                    return (0, 0);
                }

                var h = BinaryPrimitives.ReadUInt16BigEndian(sof[1..3]);
                var w = BinaryPrimitives.ReadUInt16BigEndian(sof[3..5]);
                if (!IsValidDimension(w, h))
                {
                    return (0, 0);
                }

                // EXIF 方向 5-8（cr/P1-7）：竖拍照片的存储宽高与显示宽高互为转置——交换后返回。
                return orientation is >= 5 and <= 8 ? (h, w) : (w, h);
            }

            // 首个 APP1(Exif)：解析 Orientation（方法内消费整段 payload，返回时流位于段尾）。
            if (marker == 0xE1 && orientation == 0)
            {
                orientation = TryReadExifOrientation(stream, segmentLength);
                scanned += segmentLength - 2;
                continue;
            }

            // 跳过非 SOF 段 payload（段长含自身 2 字节）。
            var skip = segmentLength - 2;
            stream.Seek(skip, SeekOrigin.Current);
            scanned += skip;
        }

        return (0, 0);
    }

    /// <summary>
    /// 解析 APP1(Exif) 段的 Orientation 标签（0x0112；cr/P1-7）。
    /// 无论解析到哪一步，返回时流位置已统一回推到段末尾（payload = 段长 - 2，由 finally 保证），
    /// 外层段循环的推进语义不变。段结构："Exif\0\0"(6) + TIFF 头（MM/II 字节序标记 + 0x002A +
    /// IFD0 偏移，8）+ IFD0 条目表（2 字节条目数 + 每条 12 字节 tag/type/count/value）；
    /// Orientation 为 SHORT(type=3) count=1，值内联在条目 value 前 2 字节。
    /// 结构不符/IO 失败返回 0（按未知处理，不旋转）。
    /// </summary>
    private static int TryReadExifOrientation(FileStream stream, int segmentLength)
    {
        var payloadStart = stream.Position;
        var payloadLength = segmentLength - 2;
        try
        {
            // 最短合法 Exif：头 6 + TIFF 头 8 + IFD0 计数 2 + 单条目 12 = 28 字节。
            if (payloadLength < 28)
            {
                return 0;
            }

            Span<byte> exifId = stackalloc byte[6];
            if (Fill(stream, exifId) < 6)
            {
                return 0;
            }

            // "Exif\0\0" 校验：非 Exif 的 APP1（如 XMP 直接载荷）不解析。
            if (exifId[0] != (byte)'E' || exifId[1] != (byte)'x' || exifId[2] != (byte)'i'
                || exifId[3] != (byte)'f' || exifId[4] != 0 || exifId[5] != 0)
            {
                return 0;
            }

            Span<byte> tiffHeader = stackalloc byte[8];
            if (Fill(stream, tiffHeader) < 8)
            {
                return 0;
            }

            var bigEndian = tiffHeader[0] == (byte)'M' && tiffHeader[1] == (byte)'M';
            if (!bigEndian && !(tiffHeader[0] == (byte)'I' && tiffHeader[1] == (byte)'I'))
            {
                return 0; // 字节序标记非法。
            }

            // IFD0 偏移相对 TIFF 头起点（TIFF 头 = "Exif\0\0" 6 字节之后）。
            var ifdOffset = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(tiffHeader[4..8])
                : BinaryPrimitives.ReadUInt32LittleEndian(tiffHeader[4..8]);
            if (ifdOffset < 8 || ifdOffset > int.MaxValue)
            {
                return 0;
            }

            // IFD0 越出段 payload 即结构损坏。
            var ifdStart = payloadStart + 6 + ifdOffset;
            if (ifdStart - payloadStart >= payloadLength)
            {
                return 0;
            }

            stream.Seek(ifdStart, SeekOrigin.Begin);

            Span<byte> countBytes = stackalloc byte[2];
            if (Fill(stream, countBytes) < 2)
            {
                return 0;
            }

            var entryCount = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(countBytes)
                : BinaryPrimitives.ReadUInt16LittleEndian(countBytes);

            // 逐条目扫描（12 字节/条）：命中 tag=0x0112 且 type=SHORT(3) 取内联值。
            Span<byte> entry = stackalloc byte[12];
            for (var i = 0; i < entryCount; i++)
            {
                if (stream.Position - payloadStart + 12 > payloadLength || Fill(stream, entry) < 12)
                {
                    return 0; // 条目表越段/读不满：结构损坏。
                }

                var tag = bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(entry[..2])
                    : BinaryPrimitives.ReadUInt16LittleEndian(entry[..2]);
                if (tag != 0x0112)
                {
                    continue;
                }

                var type = bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(entry[2..4])
                    : BinaryPrimitives.ReadUInt16LittleEndian(entry[2..4]);
                if (type != 3)
                {
                    return 0; // 非 SHORT：结构异常，放弃。
                }

                return bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(entry[8..10])
                    : BinaryPrimitives.ReadUInt16LittleEndian(entry[8..10]);
            }
        }
        catch (IOException)
        {
            return 0;
        }
        finally
        {
            // 无论解析进行到哪一步，统一回推到段末尾，保证外层段循环推进语义不变。
            stream.Seek(payloadStart + payloadLength, SeekOrigin.Begin);
        }

        return 0;
    }

    private static bool IsValidDimension(uint width, uint height)
        => width is >= 1 and <= MaxDimensionPixels && height is >= 1 and <= MaxDimensionPixels;

    /// <summary>循环读取直至填满缓冲或到达 EOF，返回实际读取字节数。</summary>
    private static int Fill(FileStream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
