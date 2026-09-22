// Responsibility: Safe file delete and move for the current image.
// Invariants: Delete uses recycle bin; move creates destination directory when missing.
// Call chain: MainViewModel commands → DeleteToRecycleBin / MoveToFolder.

using Microsoft.VisualBasic.FileIO;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IFileOperationService" />
public sealed class FileOperationService : IFileOperationService
{
    /// <inheritdoc />
    public void DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("File not found for recycle-bin delete.", path);
        }

        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <inheritdoc />
    public Task<BatchOperationResult> DeleteToRecycleBin(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // 空路径列表 → 全成功空结果（no-op），与索引侧 UpsertChunkAsync 的空块口径一致。
        if (paths.Count == 0)
        {
            return Task.FromResult(BatchOperationResult.Ok(0));
        }

        // 批量删除走线程池（对齐 TagService.RenameAll 的批量口径），避免大批量 Shell 调用阻塞调用线程。
        return Task.Run(() =>
        {
            var succeeded = 0;
            var failures = new List<TagOperationFailure>();

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    failures.Add(new TagOperationFailure(path ?? string.Empty, "文件路径不能为空。"));
                    continue;
                }

                try
                {
                    // 批量变体必须用 NoUI（FOF_NOERRORUI）：失败抛异常供逐文件聚合——
                    // 单条方法的 OnlyErrorDialogs 失败时会弹 Shell 错误框，批量场景会连环卡死（D10 拍板，严禁照抄）。
                    FileSystem.DeleteFile(path, UIOption.NoUI, RecycleOption.SendToRecycleBin);
                    succeeded++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // 覆盖句柄锁定占用、文件不存在（FileNotFoundException 属 IOException）、权限不足等；
                    // 成功项一律不回滚、失败项可重试（BatchOperationResult 既有口径，聚合写法对齐 TagService.RenameAll）。
                    failures.Add(new TagOperationFailure(path, $"删除失败：{ex.Message}"));
                }
            }

            return new BatchOperationResult(succeeded, failures);
        });
    }

    /// <inheritdoc />
    public void MoveToFolder(string sourcePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(sourcePath, destinationPath);
    }
}
