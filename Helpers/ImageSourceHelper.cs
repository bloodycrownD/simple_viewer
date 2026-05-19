using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Models;

namespace SimpleViewer.Helpers;

/// <summary>
/// Maps <see cref="LoadedImage"/> from Core into WinUI <see cref="ImageSource"/> instances.
/// </summary>
internal static class ImageSourceHelper
{
    public static ImageSource FromLoadedImage(LoadedImage loaded)
    {
        if (loaded.IsGif)
        {
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(loaded.Path, UriKind.Absolute);
            return bitmap;
        }

        if (loaded.DecodedPixelData is null || loaded.DecodedWidth <= 0 || loaded.DecodedHeight <= 0)
        {
            throw new InvalidOperationException("Static image load did not produce decoded pixels.");
        }

        var writeable = new WriteableBitmap(loaded.DecodedWidth, loaded.DecodedHeight);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            stream.Write(loaded.DecodedPixelData, 0, loaded.DecodedPixelData.Length);
        }

        writeable.Invalidate();
        return writeable;
    }
}
