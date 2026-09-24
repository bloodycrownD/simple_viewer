// 职责：缩略图 UI 应用段的首帧闸门（2026-09-24 冷缓存首帧卡死根治）。
// 不变量：默认开通（单元测试/无瀑布流宿主时零影响）；由瀑布流宿主（WaterfallView）在进入树与
//         数据源 Reset 时武装新一代、在本代布局完成（LayoutUpdated）后 400ms 安定期开闸；
//         应用段等待带超时兜底，闸门异常绝不悬挂加载管线。
// 背景证据：startup.log 环形追踪 + dotnet-stack 现场实锤——冷缓存打开 245 张图库时，scan:end
//         后第一张缩略图应用（SetSourceAsync，实测 78-79ms=常规 5 倍，合成器争用堆积）紧跟
//         ≥15s UI 线程纯 native 阻塞（托管栈只剩 Application.Start 入口、GC 采样 4-5MB/ms 级
//         停顿排除 GC、池线程全部健康）。与 2026-09-17 UiApplyGate 记载的"首帧渲染期并发应用
//         死锁"同族：16ms 节流摊开了风暴，但大批量首呈现窗口内的第一次应用仍与合成器互等。
//         本闸门把应用段整体推到"该布局代完成并安定"之后，从竞态窗口中移出。首版用
//         CompositionTarget.Rendering 首拍开闸实机复测仍卡（首拍=帧开始渲染而非呈现完成），
//         改为 LayoutUpdated + 400ms 确定性时序。
// 调用链：WaterfallView（武装/开闸）→ GalleryItemViewModel.LoadThumbnailAsync（等待）。

namespace SimpleViewer.Services;

/// <summary>瀑布流"当前布局代首帧已呈现"闸门（静态、按代滚动）。</summary>
public static class FirstFrameGate
{
    private static TaskCompletionSource _current = CreateCompleted();

    /// <summary>当前布局代的首帧呈现任务（已开闸时立即完成）。</summary>
    public static Task FirstFrameCompleted => _current.Task;

    /// <summary>武装新一代（闭闸）：由瀑布流宿主在进入树/数据源 Reset 时调用。</summary>
    public static void Arm() => _current = CreatePending();

    /// <summary>开闸：首个合成帧到达时调用（TrySet 防重复开旧代）。</summary>
    public static void Open() => _current.TrySetResult();

    private static TaskCompletionSource CreateCompleted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private static TaskCompletionSource CreatePending()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
