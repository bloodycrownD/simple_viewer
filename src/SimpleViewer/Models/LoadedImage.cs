namespace SimpleViewer.Models;

/// <summary>
/// Result of an image load operation (metadata + optional decoded pixels).
/// </summary>
public sealed class LoadedImage
{
    public string Path { get; init; } = string.Empty;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public long FileSizeBytes { get; init; }

    public bool IsGif { get; init; }

    /// <summary>BGRA8 buffer after optional downsample decode; null for GIF.</summary>
    public byte[]? DecodedPixelData { get; init; }

    public int DecodedWidth { get; init; }

    public int DecodedHeight { get; init; }

    /// <summary>Reserved for UI-layer binding; not populated by Core.</summary>
    public object? ImageSource { get; init; }
}
