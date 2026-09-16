namespace SimpleViewer.Models;

/// <summary>
/// 批量标签操作的单条失败记录。
/// </summary>
/// <param name="Path">失败文件的全路径；空串表示整体操作被拒绝（非单文件原因，如标签被快捷键绑定引用）。</param>
/// <param name="Reason">用户可读失败原因（占用/冲突/超长/名称非法等）。</param>
public sealed record TagOperationFailure(string Path, string Reason);

/// <summary>
/// 批量标签（重命名落盘）操作的聚合回执：成功数 + 失败明细。
/// 单文件操作也复用同一结果形态。
/// </summary>
/// <param name="SucceededCount">成功文件数（含幂等命中：文件已处于目标状态、未执行实际 IO）。</param>
/// <param name="Failures">失败明细列表；成功项一律不回滚，失败项可重试（PRD 拍板）。</param>
public sealed record BatchOperationResult(int SucceededCount, List<TagOperationFailure> Failures)
{
    /// <summary>是否存在失败项。</summary>
    public bool HasFailures => Failures is { Count: > 0 };

    /// <summary>构造全成功回执（无失败项）。</summary>
    public static BatchOperationResult Ok(int succeededCount) => new(succeededCount, []);

    /// <summary>
    /// 构造整体拒绝回执：成功数为 0，仅一条 Path 为空串的失败（整体原因，不归属单个文件）。
    /// </summary>
    public static BatchOperationResult Reject(string reason) => new(0, [new TagOperationFailure(string.Empty, reason)]);
}
