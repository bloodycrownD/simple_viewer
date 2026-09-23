using Windows.Graphics.Imaging;

namespace SimpleViewer.Models;

/// <summary>
/// Result of an image load operation (metadata + decoded bitmap).
/// </summary>
public sealed class LoadedImage
{
    public string Path { get; init; } = string.Empty;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public long FileSizeBytes { get; init; }

    public bool IsGif { get; init; }

    /// <summary>BGRA8 托管像素缓冲（遗留路径；现行解码直出 <see cref="DecodedBitmap"/>，此字段恒 null，仅作桥接兜底）。GIF 恒 null。</summary>
    public byte[]? DecodedPixelData { get; init; }

    /// <summary>WIC 解码直出位图（2026-09-23 换源管线：解码+降采样+EXIF 方向全在 WIC 完成，零中间 byte[]；Bgra8/Premultiplied）。GIF 恒 null。</summary>
    public SoftwareBitmap? DecodedBitmap { get; init; }

    public int DecodedWidth { get; init; }

    public int DecodedHeight { get; init; }

    /// <summary>Reserved for UI-layer binding; not populated by Core.</summary>
    public object? ImageSource { get; init; }
}
