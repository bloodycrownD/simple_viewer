// 职责：轻量操作追踪（走查诊断）：关键路径打点 + 环形缓冲，供 UI 无响应看门狗转储。
// 不变量：全部方法线程安全且绝不抛出（诊断代码不许影响主流程）；环形上限有限（内存可控）；
//         Core 与 UI 工程共用（App 侧看门狗读取转储）。
// 调用链：MainViewModel（扫描/打标/筛选）、ThumbnailService（解码）、App（看门狗转储）。

namespace SimpleViewer.Services;

/// <summary>线程安全的最近操作环形追踪（默认保留 200 条）。</summary>
public static class DiagnosticTrace
{
    private readonly record struct Entry(long Timestamp, string Label);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = [];
    private static readonly int Capacity = 200;

    /// <summary>打一个操作点（标签建议短且含关键量，如 "scan:end 37"）。</summary>
    public static void Mark(string label)
    {
        try
        {
            lock (Gate)
            {
                Entries.Add(new Entry(Environment.TickCount64, label));
                if (Entries.Count > Capacity)
                {
                    Entries.RemoveRange(0, Entries.Count - Capacity);
                }
            }
        }
        catch
        {
            // 诊断代码静默
        }
    }

    /// <summary>转储最近操作（每行 "偏移毫秒 标签"，相对最早条目）；供无响应时写入诊断日志。</summary>
    public static string Dump()
    {
        try
        {
            lock (Gate)
            {
                if (Entries.Count == 0)
                {
                    return "<无追踪记录>";
                }

                var baseTime = Entries[0].Timestamp;
                return string.Join(Environment.NewLine, Entries
                    .Select(e => $"+{e.Timestamp - baseTime,8}ms {e.Label}"));
            }
        }
        catch
        {
            return "<转储失败>";
        }
    }
}
