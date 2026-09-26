// 职责：x:Bind 函数转换器画刷的进程级静态缓存（2026-09-26 tag-op-finalizer-crash 修复）——
//       键 = 计算完成的最终 ARGB，同一颜色全进程仅一份 SolidColorBrush 实例，重复重建零新建。
// 背景：侧栏/筛选条是「全量重建」模型（打标收尾、扫描节流、组展开折叠、主题切换、配置保存都会
//       Groups.Clear()/FilterChips.Clear() 后逐行 Add），TagSidebarConverters 此前 30 处 new
//       SolidColorBrush 属「每次求值都新建」——每个打标操作收尾都产生数十个 SolidColorBrush
//       （DependencyObject 族）裸交 GC，终结器线程跨线程 Release 是 2026-09-26 崩溃（fastfail
//       0xc0000409 @ ucrtbase+0xa527e，终结器栈停在 WinRT.IObjectReference.Finalize）的候选来源，
//       违反 RULE:26「XAML 对象永不可裸交 GC」。
// 设计约束（勿改）：
//   ① 键必须是**最终颜色**（含主题差异）：IsDarkTheme 双值取色后颜色不同即不同条目，天然无
//      「主题切换后拿到旧色」风险；禁止改成 (主题, 语义) 复合键，也不依赖重建清理缓存；
//   ② UI 线程专用：x:Bind 求值与面板构建（TagSidebar.Rebuild / TagFilterPanelControl 代码构建）
//      均在 UI 线程，Dictionary 无锁即可——Debug 断言兜底（Release 静默，与 ImageSourceRetirement 同约定）；
//   ③ 取值域有界：HSL 调色板 + 少量固定色，条目数上限 = 调色板色数 × 主题数，缓存可终身持有；
//   ④ 只缓存不可变使用的画刷：调用方一律只读（无 .Color = / Opacity 就地改写），共享实例安全。
// 调用链：TagSidebarConverters 全部取色函数 + WaterfallConverters.BadgeBackground（经 FromHsl）
//          → 本缓存；MainViewModel.RebuildTagSidebar 收尾读 Snapshot 记 churn 埋点。

using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace SimpleViewer.Views;

/// <summary>
/// 转换器画刷的静态缓存（键 = ARGB uint，值 = 该颜色的唯一 <see cref="SolidColorBrush"/>）。
/// 仅 UI 线程访问；条目终身持有（有界），不提供清理入口。
/// </summary>
internal static class TagBrushCache
{
    private static readonly Dictionary<uint, SolidColorBrush> Cache = [];

    /// <summary>实际新建画刷的累计数（缓存未命中次数；churn 埋点的唯一事实来源）。</summary>
    private static long _createCount;

    /// <summary>
    /// 累计取画刷调用数（命中 + 未命中）。每次调用对应修复前的一处 <c>new SolidColorBrush</c>——
    /// 故「单轮重建的 brushCalls」= 修复前该轮重建会新建的画刷数（churn 前后对照的实测口径，
    /// 无需靠静态估算）。
    /// </summary>
    private static long _callCount;

    /// <summary>累计新建画刷数（只增；诊断用）。</summary>
    internal static long CreateCount => _createCount;

    /// <summary>当前缓存条目数（= 已出现的不同最终颜色数）。</summary>
    internal static int EntryCount => Cache.Count;

    /// <summary>缓存统计快照（累计新建数, 累计调用数, 条目数）——churn 埋点用，避免多次属性读的竞态。</summary>
    internal static (long Created, long Calls, int Entries) Snapshot() => (_createCount, _callCount, Cache.Count);

    /// <summary>按分量取画刷（缓存命中即复用）。</summary>
    internal static SolidColorBrush Get(byte a, byte r, byte g, byte b)
        => Get(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    /// <summary>按 <see cref="Windows.UI.Color"/> 取画刷（强调色等运行期读出的颜色走此入口）。</summary>
    internal static SolidColorBrush Get(Windows.UI.Color color)
        => Get(color.A, color.R, color.G, color.B);

    /// <summary>
    /// 按 ARGB 取画刷：命中直接复用；未命中创建后入缓存并计数。
    /// 颜色语义完全由调用方计算（本类不参与取色），故缓存不可能改变像素取值。
    /// </summary>
    internal static SolidColorBrush Get(uint argb)
    {
        // UI 线程专用（与 ImageSourceRetirement 同约定）：XAML 对象跨线程共享会破坏线程亲和性。
        Debug.Assert(
            DispatcherQueue.GetForCurrentThread() is not null,
            "TagBrushCache 必须在 UI 线程访问（XAML 画刷线程亲和）");

        _callCount++;
        if (Cache.TryGetValue(argb, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb));
        Cache[argb] = brush;
        _createCount++;
        return brush;
    }
}
