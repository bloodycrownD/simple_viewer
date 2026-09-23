using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// File delete (recycle bin) and move operations.
/// </summary>
public interface IFileOperationService
{
    void DeleteToRecycleBin(string path);

    /// <summary>
    /// 批量移入回收站（图库删除选中集，D10）：逐文件删除，可预期失败（占用/不存在/权限不足）
    /// 聚合进 <see cref="BatchOperationResult.Failures"/>，成功项不回滚；
    /// 空路径列表为全成功空结果（no-op）。
    /// </summary>
    Task<BatchOperationResult> DeleteToRecycleBin(IReadOnlyList<string> paths);

    void MoveToFolder(string sourcePath, string destinationDirectory);
}
