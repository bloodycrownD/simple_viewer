using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Models;
using Windows.Graphics.Imaging;

namespace SimpleViewer.Helpers;

/// <summary>
/// 把 Core 层 <see cref="LoadedImage"/> 映射为 WinUI <see cref="ImageSource"/> 实例。
/// 另含拖拽跟随小图的 WIC 生成工具（2026-09-19 拖拽视觉；本文件仅编入 UI 工程——
/// SoftwareBitmap/BitmapDecoder 属 WinRT 投影，Core 工程不引用 WinAppSDK，编不进去）。
/// </summary>
internal static class ImageSourceHelper
{
    /// <summary>
    /// <see cref="LoadedImage"/> → <see cref="ImageSource"/>（2026-09-23 放大/翻页顿挫修复的异步版，
    /// 替代原同步 FromLoadedImage/WriteableBitmap 路径——那在 UI 线程做整块像素分配+memcpy：fit 档
    /// ~19MB、全分辨率档 ~192MB，放大跨 1.2× 换源时单次 ~400MB UI 线程内存流量即顿挫主源）。
    /// 主路径：解码直出的 <see cref="SoftwareBitmap"/> → UI 线程 O(1) 的
    /// <see cref="SoftwareBitmapSource.SetBitmapAsync"/>（像素上传由合成器异步完成）。
    /// GIF 分支不变（UriSource 异步打开，ImageOpened 由调用方等待后提交）。
    /// </summary>
    internal static async Task<ImageSource> FromLoadedImageAsync(LoadedImage loaded)
    {
        if (loaded.IsGif)
        {
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(loaded.Path, UriKind.Absolute);
            return bitmap;
        }

        // 解码器在池线程直出的位图可直接交 UI 消费；SoftwareBitmapSource 必须在 UI 线程创建并 Set
        //（本方法续体在 UI 线程）。
        if (loaded.DecodedBitmap is { } decoded)
        {
            // 2026-09-24 实机 SetBitmapAsync E_INVALIDARG 取证+自愈：用户实机会话报"only supports
            // SoftwareBitmap with positive width/height, bgra8 pixel format and pre-multiplied or no
            // alpha"，但对全图库 245 张按本管线参数实测全部输出 Bgra8/Premultiplied/正尺寸
            //（scripts\probe-decode-alpha.ps1）——源头不可能产出非法位图，异常只能来自运行期位图
            // 状态劣化。Set 前读实际状态：不合规则转换自愈（格式/alpha 异常可治；属性读不出
            // =位图已劣化，落日志后抛给上层按加载失败处理）；状态合规却仍被拒，用转换副本
            //（全新 native 对象）重试一次绕开劣化，成功与否均落完整现场（下次复现即有数据）。
            var bitmap = decoded;
            if (!IsSetBitmapCompatible(decoded))
            {
                var before = DescribeBitmap(decoded);
                try
                {
                    bitmap = SoftwareBitmap.Convert(
                        decoded, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
                catch (Exception convertEx)
                {
                    App.WriteDiagnosticLog($"[换源位图自愈失败] state={before} path={loaded.Path}", convertEx);
                    throw;
                }

                App.WriteDiagnosticLog(
                    $"[换源位图自愈] before={before} after={DescribeBitmap(bitmap)} path={loaded.Path}");
            }

            try
            {
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                return source;
            }
            catch (Exception ex) when (ReferenceEquals(bitmap, decoded))
            {
                // 属性读数正常但仍被拒：转换副本重试一次（全新 native 位图，绕开状态劣化）。
                try
                {
                    var copy = SoftwareBitmap.Convert(
                        decoded, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    var retrySource = new SoftwareBitmapSource();
                    await retrySource.SetBitmapAsync(copy);
                    App.WriteDiagnosticLog(
                        $"[SetBitmapAsync 副本重试成功] state={DescribeBitmap(decoded)} path={loaded.Path}", ex);
                    return retrySource;
                }
                catch (Exception retryEx)
                {
                    App.WriteDiagnosticLog(
                        $"[SetBitmapAsync 副本重试仍失败] state={DescribeBitmap(decoded)} path={loaded.Path}", retryEx);
                    throw;
                }
            }
            catch (Exception ex)
            {
                App.WriteDiagnosticLog(
                    $"[SetBitmapAsync 失败] state={DescribeBitmap(bitmap)} path={loaded.Path}", ex);
                throw;
            }
        }

        if (loaded.DecodedPixelData is null || loaded.DecodedWidth <= 0 || loaded.DecodedHeight <= 0)
        {
            throw new InvalidOperationException("Static image load did not produce decoded pixels.");
        }

        // 兜底（遗留托管像素路径）：UI 线程 CreateCopyFromBuffer——池线程用 CreateCopyFromBuffer
        // 包装托管数组产出的位图在 XAML Image 不渲染（2026-09-23 实验实锤的 WinUI 3 敏捷性坑，
        // 与解码器直出位图可渲染形成对照），必须回到 UI 线程创建。
        var fallback = SoftwareBitmap.CreateCopyFromBuffer(
            loaded.DecodedPixelData.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            loaded.DecodedWidth,
            loaded.DecodedHeight,
            BitmapAlphaMode.Premultiplied);
        var fallbackSource = new SoftwareBitmapSource();
        await fallbackSource.SetBitmapAsync(fallback);
        return fallbackSource;
    }

    /// <summary>
    /// 位图是否满足 <see cref="SoftwareBitmapSource.SetBitmapAsync"/> 的校验（正宽高 + Bgra8 +
    /// Premultiplied/Ignore）。属性读取本身抛异常（位图已劣化/dispose）按不合规处理。
    /// </summary>
    private static bool IsSetBitmapCompatible(SoftwareBitmap bitmap)
    {
        try
        {
            return bitmap.PixelWidth > 0
                && bitmap.PixelHeight > 0
                && bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                && (bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                    || bitmap.BitmapAlphaMode == BitmapAlphaMode.Ignore);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>安全读位图状态描述（属性可能抛——诊断代码不许再抛出去）。</summary>
    private static string DescribeBitmap(SoftwareBitmap? bitmap)
    {
        try
        {
            return bitmap is null
                ? "<null>"
                : $"{bitmap.BitmapPixelFormat}/{bitmap.BitmapAlphaMode} {bitmap.PixelWidth}x{bitmap.PixelHeight}";
        }
        catch (Exception e)
        {
            return $"<属性读取抛 {e.GetType().Name}>";
        }
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
            // 全程后台线程 + ConfigureAwait(false)（2026-09-23 冻结修复）：原实现各 WinRT await 续体
            // 回 UI STA，且 MemoryStream 流在 STA 创建——WIC 异步操作的完成需封送回流所属 STA，
            // 首帧布局期 UI 线程恰在原生布局中不泵消息时，WIC 线程等封送、UI 等解码完成，互等成
            // 零 CPU 死锁（实机 8 次 ≥15s 冻结 + 本地复现 6 次，消融实验定位）。整体包 Task.Run：
            // 流创建、解码、SoftwareBitmap 拷贝全在池线程，零 STA 依赖；SoftwareBitmap 具敏捷性，
            // 后台创建后交 UI 消费（DragUI.SetContentFromSoftwareBitmap）安全。
            return await Task.Run(async () =>
            {
                using var stream = new MemoryStream(jpegBytes).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var frame = await decoder.GetFrameAsync(0).AsTask().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var transform = CreateDownscaleTransform(frame.PixelWidth, frame.PixelHeight, shortSide);
                var pixelData = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
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
            }, cancellationToken).ConfigureAwait(false);
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
