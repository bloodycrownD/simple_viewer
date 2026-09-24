// Responsibility: Safe file delete and move for the current image.
// Invariants: Delete uses recycle bin; move creates destination directory when missing.
// Call chain: MainViewModel commands → DeleteToRecycleBin / MoveToFolder.

using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IFileOperationService" />
public sealed class FileOperationService : IFileOperationService
{
    // 无 UI 回收站删除只能走 SHFileOperationW P/Invoke（batch-tag-management D10 拍板）：
    // Microsoft.VisualBasic.FileIO.UIOption 只有 AllDialogs/OnlyErrorDialogs 两个成员（无 NoUI），
    // OnlyErrorDialogs 失败时会弹 Shell 错误框、批量场景连环卡死；FOF_NOERRORUI|FOF_SILENT 失败静默返回错误码供聚合。
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4; // 不显示进度对话框
    private const ushort FofNoConfirmation = 0x10; // 不弹确认
    private const ushort FofAllowUndo = 0x40; // 删除进回收站
    private const ushort FofNoErrorUi = 0x400; // 失败不弹错误框

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string? pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

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

        // 批量删除必须在专用 STA 线程执行（2026-09-24 崩溃根治）：SHFileOperationW 带
        // FOF_ALLOWUNDO（回收站）要求调用线程 STA——Shell 内部走 OLE/回收站对象，池线程（MTA）
        // 上调用属未定义行为，实机成批删除触发原生堆损坏闪退（fastfail 0xc0000409 @ucrtbase
        // 的 dump 实锤，用户「删除标签（选中集删除）后崩溃」场景）。原 Task.Run 池线程方案即祸根。
        // 专用 STA 线程兼顾「不阻塞调用线程」与 Shell 的单元要求（对齐 TagService.RenameAll 的
        // 后台口径，改单元形态不改并发语义）。
        var completion = new TaskCompletionSource<BatchOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.SetResult(DeleteAllOnSta(paths));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "sv-recycle-delete",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    /// <summary>STA 线程体：逐文件无 UI 回收站删除并聚合失败（原 Task.Run 循环逻辑原样迁移）。</summary>
    private static BatchOperationResult DeleteAllOnSta(IReadOnlyList<string> paths)
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

            if (!File.Exists(path))
            {
                failures.Add(new TagOperationFailure(path, "文件不存在（可能已被移动或删除）。"));
                continue;
            }

            try
            {
                // pFrom 要求双 NUL 结尾：封送器补一个终止 NUL，这里显式再追加一个。
                var operation = default(ShFileOpStruct);
                operation.hwnd = IntPtr.Zero;
                operation.wFunc = FoDelete;
                operation.pFrom = path + "\0";
                operation.pTo = null;
                operation.fFlags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi;
                operation.fAnyOperationsAborted = false;
                operation.hNameMappings = IntPtr.Zero;
                operation.lpszProgressTitle = null;

                var code = SHFileOperationW(ref operation);
                if (code == 0 && !operation.fAnyOperationsAborted)
                {
                    succeeded++;
                }
                else
                {
                    // 句柄锁定占用、权限不足等均以非零返回码体现；成功项不回滚、失败项可重试（既有批量口径）。
                    failures.Add(new TagOperationFailure(path, $"删除失败（SHFileOperation 错误码 0x{code:X}）。"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add(new TagOperationFailure(path, $"删除失败：{ex.Message}"));
            }
        }

        return new BatchOperationResult(succeeded, failures);
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
