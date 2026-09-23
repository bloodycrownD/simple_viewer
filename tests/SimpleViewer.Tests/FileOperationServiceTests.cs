using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class FileOperationServiceTests
{
    // 被测服务构造无依赖，直接 new。
    // 批量删除（SendToRecycleBin）的文件按预期进入系统回收站，测试只清理自己的临时目录
    // （TempDirectory.Dispose，using 即结构化 try/finally）——绝不调用任何清空回收站的 API。

    [Fact]
    public async Task T_FO_01_BatchDeleteToRecycleBin_AllSucceed()
    {
        using var temp = new TempDirectory();
        var service = new FileOperationService();

        // 临时目录内建 3 个真实文件
        var paths = Enumerable.Range(1, 3)
            .Select(i => System.IO.Path.Combine(temp.Path, $"sv-test-fo-{i}.png"))
            .ToArray();
        foreach (var path in paths)
        {
            File.WriteAllText(path, "x");
        }

        var result = await service.DeleteToRecycleBin(paths);

        Assert.Equal(3, result.SucceededCount);                    // 全部成功
        Assert.False(result.HasFailures);                          // 无失败明细
        Assert.All(paths, p => Assert.False(File.Exists(p)));      // 原路径文件不存在（已入回收站）
    }

    [Fact]
    public async Task T_FO_02_BatchDeleteToRecycleBin_LockedFileAggregatedOthersSucceed()
    {
        using var temp = new TempDirectory();
        var service = new FileOperationService();

        var lockedPath = System.IO.Path.Combine(temp.Path, "sv-test-fo-locked.png");
        var normalPaths = new[]
        {
            System.IO.Path.Combine(temp.Path, "sv-test-fo-ok-1.png"),
            System.IO.Path.Combine(temp.Path, "sv-test-fo-ok-2.png"),
        };
        File.WriteAllText(lockedPath, "x");
        foreach (var path in normalPaths)
        {
            File.WriteAllText(path, "x");
        }

        // 锁定句柄（FileShare.None）在删除期间全程保持打开：SHFileOperationW 对被占用文件返回非零错误码（进失败明细而非弹框）
        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.DeleteToRecycleBin(normalPaths.Append(lockedPath).ToArray());

            Assert.Equal(2, result.SucceededCount);                    // 其余成功不回滚
            Assert.True(result.HasFailures);
            var failure = Assert.Single(result.Failures);              // 锁定项进失败明细
            Assert.Equal(lockedPath, failure.Path);                    // 真实文件路径（空串=整体拒绝约定，此处不适用）
            Assert.False(string.IsNullOrWhiteSpace(failure.Reason));   // 携带用户可读原因
            Assert.True(File.Exists(lockedPath));                      // 锁定文件原位保留
            Assert.All(normalPaths, p => Assert.False(File.Exists(p))); // 成功项确实删除
        }
    }

    [Fact]
    public async Task T_FO_03_BatchDeleteToRecycleBin_EmptyListIsNoOp()
    {
        var service = new FileOperationService();

        var result = await service.DeleteToRecycleBin(Array.Empty<string>());

        Assert.Equal(0, result.SucceededCount); // 空列表 → 全成功空结果（no-op）
        Assert.False(result.HasFailures);
        Assert.Empty(result.Failures);
    }

    /// <summary>唯一前缀（sv-test-fo-）的 Path.GetTempPath 临时子目录；Dispose 递归清理（即 try/finally 语义）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sv-test-fo-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
