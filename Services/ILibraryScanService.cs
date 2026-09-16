// Responsibility: 图库递归分块扫描服务契约——以 IAsyncEnumerable<GalleryItem> 渐进产出扫描结果。
// Invariants: 分块产出（每块约 ChunkSize 项）；取消即时生效；root 为空或不存在时产出空序列。
// Call chain: MainViewModel（Step 7“打开图库”）→ ScanAsync → Step 5 索引 UpsertChunk / Step 8 瀑布流渐进填充。

using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>图库递归分块扫描服务（D9：首屏不等全量，渐进上报）。</summary>
public interface ILibraryScanService
{
    /// <summary>
    /// 递归扫描 <paramref name="root"/> 下受支持的图片（png/jpg/jpeg/gif，大小写不敏感；不可访问目录跳过），
    /// 以 <see cref="IAsyncEnumerable{GalleryItem}"/> 分块渐进产出。
    /// 每块约 <see cref="LibraryScanService.ChunkSize"/> 项：块内按预分词自然排序 key 稳定排序，
    /// 块间按发现顺序产出（最终产出序 = 自然序稳定归并结果，D15 发现顺序）。
    /// 每项解析文件名尾部标签段，并从 JPEG/PNG/GIF 文件头快速读取宽高（失败回退 1:1）。
    /// </summary>
    /// <param name="root">扫描根目录（递归含全部子目录）。</param>
    /// <param name="progress">按块上报累计已发现数（含当前块；仅为图片项计数）。</param>
    /// <param name="cancellationToken">取消令牌，贯穿枚举全程；取消时抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>图库项流（root 为空或不存在时为空序列，不抛异常）。</returns>
    IAsyncEnumerable<GalleryItem> ScanAsync(
        string root,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
