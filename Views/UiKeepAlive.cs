// 职责：UI 线程有界「保命环」——强引用持有刚从可视树摘下的 XAML 对象/树根，超出容量按 FIFO
//       释放引用（2026-09-26 xaml-finalizer-residuals 修复 P1-6）。
// 语义（务必如实理解，勿当释放设施）：
//   · 这是「不释放」而非「正确释放」——被摘下的 XAML 子树（DependencyObject 族）仍然会由 GC
//     终结器线程执行 native Release（RULE:26 的崩溃机制同族），本类只是把「整棵树同一瞬间
//     失去引用」摊成「每次只放走一棵」：环里有界强引用（默认 4 项）让最近几棵树的终结时刻错开，
//     并把瞬时终结压力限制在一个有界窗口内——属「有界内存换安全」的止血措施，不是根治。
//   · 真正的正确释放需要 UI 元素级 Dispose/从树中确定性拆解（面板整树重建属既有架构，
//     本轮明确不重构——见 xaml-finalizer-residuals spec「TagFilterPanelControl 整树重建」）。
// 用法：仅 UI 线程调用（Debug 断言兜底）；对象从可视树移除前/后交给 <see cref="Hold"/> 即可。
// 代价：最多同时多保 4 棵已摘下的子树（面板量级约 40~300 个对象/次点击，树内共享画刷已经
//       TagBrushCache 静态复用，额外常驻内存为树结构壳与文本对象，量级 KB~百 KB）。

using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace SimpleViewer.Views;

/// <summary>
/// 有界保命环（仅 UI 线程）：FIFO 强引用最近摘下的 XAML 树根，容量满时放走最老一项。
/// 容量刻意取小（<see cref="Capacity"/>）——环不是缓存，只是摊薄终结时刻的缓冲。
/// </summary>
internal static class UiKeepAlive
{
    /// <summary>环容量（对象数；面板整树重建一次交一棵，4 棵 ≈ 最近 4 次重建的树壳）。</summary>
    private const int Capacity = 4;

    private static readonly Queue<object> Held = new();

    /// <summary>当前强引用持有数（诊断/自检用；≤ <see cref="Capacity"/>）。</summary>
    internal static int Count => Held.Count;

    /// <summary>
    /// 持有一棵树根（可空安全）：入环后若超容量，放走最老一项（仅释放本类引用，
    /// 该对象随下一次 GC 进入终结流程）。
    /// </summary>
    internal static void Hold(object? root)
    {
        Debug.Assert(
            DispatcherQueue.GetForCurrentThread() is not null,
            "UiKeepAlive 必须在 UI 线程调用（XAML 对象线程亲和）");

        if (root is null)
        {
            return;
        }

        Held.Enqueue(root);
        while (Held.Count > Capacity)
        {
            Held.Dequeue();
        }
    }
}
