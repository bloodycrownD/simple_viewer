namespace SimpleViewer.Models;

/// <summary>
/// Result of an image load operation (metadata + optional decoded source).
/// </summary>
public sealed class LoadedImage
{
    public string Path { get; init; } = string.Empty;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public long FileSizeBytes { get; init; }

    public bool IsGif { get; init; }

    public object? ImageSource { get; init; }
}
