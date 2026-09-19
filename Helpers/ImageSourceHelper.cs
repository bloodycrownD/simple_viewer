using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Models;
using Windows.Graphics.Imaging;

namespace SimpleViewer.Helpers;

/// <summary>
/// Maps <see cref="LoadedImage"/> from Core into WinUI <see cref="ImageSource"/> instances.
/// 另含拖拽跟随小图的 WIC 生成工具（2026-09-19 拖拽视觉；本文件仅编入 UI 工程——
/// SoftwareBitmap/BitmapDecoder 属 WinRT 投影，Core 工程不引用 WinAppSDK，编不进去）。
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

    /// <summary>
    /// 从缩略图 JPEG 字节生成拖拽跟随小图（2026-09-19 拖拽视觉）：WIC 解码 + BitmapTransform
    /// 等比降采样到短边约 <paramref name="shortSide"/> 像素的 <see cref="SoftwareBitmap"/>，
/// 供 DragUI.SetContentFromSoftwareBitmap 消费（DragUI 渲染位图不缩放，必须预备小图）。
    /// 输入是 ThumbnailService 已烘焙 EXIF 方向的 JPEG（二次产物），故按 IgnoreExifOrientation
    /// 解码防止双重旋转。任一步失败（含取消）返回 null——调用方静默降级，DragStarting 走回退链。
    /// </summary>
    /// <param name="jpegBytes">缩略图 JPEG 字节（ThumbnailResult.ImageBytes）。</param>
    /// <param name="shortSide">目标短边像素（拖拽视觉约 120）。</param>
    /// <param name="cancellationToken">取消令牌（卡片元素被回收时中止）。</param>
    internal static async Task<SoftwareBitmap?> TryCreateDragVisualAsync(
        byte[] jpegBytes,
        int shortSide,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(jpegBytes).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();

            var frame = await decoder.GetFrameAsync(0);
            cancellationToken.ThrowIfCancellationRequested();

            var transform = CreateDownscaleTransform(frame.PixelWidth, frame.PixelHeight, shortSide);
            var pixelData = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            cancellationToken.ThrowIfCancellationRequested();

            var pixels = pixelData.DetachPixelData();
            var width = (int)(transform.ScaledWidth > 0 ? transform.ScaledWidth : frame.PixelWidth);
            var height = (int)(transform.ScaledHeight > 0 ? transform.ScaledHeight : frame.PixelHeight);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            return SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied);
        }
        catch (Exception)
        {
            // 静默降级：拖拽小图属锦上添花，任何失败（解码/取消/尺寸异常）都不影响缩略图主链路。
            return null;
        }
    }

    /// <summary>
    /// 短边钳制的等比降采样变换：目标短边 = <paramref name="shortSide"/>，源短边不超目标时保持原尺寸（不放大）。
    /// 缩小插值一律 Fant（项目铁律：默认 Linear 大倍率缩小会产生锯齿/摩尔纹）。
    /// </summary>
    private static BitmapTransform CreateDownscaleTransform(uint sourceWidth, uint sourceHeight, int shortSide)
    {
        var transform = new BitmapTransform();
        var sourceShortSide = Math.Min(sourceWidth, sourceHeight);
        if (sourceShortSide <= (uint)shortSide)
        {
            return transform;
        }

        var scale = (double)shortSide / sourceShortSide;
        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        transform.ScaledWidth = (uint)Math.Max(1, Math.Round(sourceWidth * scale));
        transform.ScaledHeight = (uint)Math.Max(1, Math.Round(sourceHeight * scale));
        return transform;
    }
}
