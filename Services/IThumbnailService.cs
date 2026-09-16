// 职责：瀑布流缩略图管线契约——按分桶宽度获取静态缩略图（JPEG 字节）。
// 不变量：Core 不依赖 WinUI 控件，返回字节由 UI 层桥接为 ImageSource（参考 LoadedImage.ImageSource 预留槽位）。
// 调用链：WaterfallViewModel → GetThumbnailAsync → ThumbnailResult → UI 层字节桥接。

namespace SimpleViewer.Services;

/// <summary>
/// 缩略图获取结果：JPEG 字节 + 分桶信息，供 UI 层桥接为图像控件数据源。
/// </summary>
public sealed class ThumbnailResult
{
    /// <summary>缩略图对应的源图路径（调用方传入的原始路径）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>目标宽度分桶（像素，如 360）。</summary>
    public int Bucket { get; init; }

    /// <summary>JPEG 编码后的缩略图字节（静态图；GIF 已取首帧，瀑布流不做动画）。</summary>
    public byte[] ImageBytes { get; init; } = [];

    /// <summary>UI 层桥接槽位；Core 不填充（沿用 LoadedImage.ImageSource 模式）。</summary>
    public object? ImageSource { get; init; }
}

/// <summary>
/// 瀑布流缩略图服务：内存 LRU（字节预算）+ 磁盘 JPEG 缓存 + WIC 降采样解码。
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// 获取指定图片的缩略图。查找顺序：内存 LRU → 磁盘缓存 → WIC 解码（JPEG q80 落盘并回填内存）。
    /// </summary>
    /// <param name="path">源图绝对路径。</param>
    /// <param name="bucket">目标宽度分桶（像素，如 360；源图更窄时不放大）。</param>
    /// <param name="cancellationToken">取消令牌；取消的请求不落盘。</param>
    /// <exception cref="FileNotFoundException">内存与磁盘均未命中且源文件不存在。</exception>
    /// <exception cref="OperationCanceledException">请求已取消。</exception>
    Task<ThumbnailResult> GetThumbnailAsync(
        string path,
        int bucket,
        CancellationToken cancellationToken = default);
}
