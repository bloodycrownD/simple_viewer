// 职责：瀑布流卡片项视图模型——显示名（剥离标签段）/标签角标/缩略图槽位/选中态与卡片交互；
//       缩略图加载成功后顺带预生成拖拽跟随小图（~120px 短边 SoftwareBitmap，失败静默降级）。
// 不变量：缩略图按需加载（ElementPrepared 触发、ElementClearing 取消并释放视觉资源——cr/P1-5，
//         禁止一次性为全部项加载）；
//         JPEG 字节经 MemoryStream → BitmapImage 在 UI 线程桥接（ThumbnailResult.ImageBytes 契约）；
//         拖拽小图生成置于 UiApplyGate 闸门段之后（不阻塞缩略图 UI 应用）；
//         解码/读盘失败保持浅色占位不抛出；打标重命名后就地 UpdateFrom 更新（不重建、不重排，D15）。
// 调用链：WaterfallViewModel（创建/更新）→ WaterfallView DataTemplate（x:Bind）→ BeginLoadThumbnail → ThumbnailService。

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Helpers;
using SimpleViewer.Models;
using SimpleViewer.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Graphics.Imaging;

namespace SimpleViewer.ViewModels;

/// <summary>
/// 瀑布流单个卡片项（spec Step 8）：包装 <see cref="GalleryItem"/>，提供卡片模板绑定属性。
/// </summary>
public partial class GalleryItemViewModel : ObservableObject
{
    /// <summary>
    /// 缩略图分桶宽度（D8）。默认 360；由 WaterfallView 在加载时按 DPI 放大
    /// （2026-09-17 走查修复模糊：200% 缩放下卡片物理宽 480px，360px 缩略图拉伸显示发糊）。
    /// </summary>
    public static int ThumbnailBucket { get; set; } = 360;

    /// <summary>卡片角标最多显示的标签数，其余折叠为 "+N"。</summary>
    private const int MaxVisibleTagBadges = 3;

    private readonly WaterfallViewModel _owner;
    private readonly IThumbnailService _thumbnailService;
    private CancellationTokenSource? _thumbnailCts;

    public GalleryItemViewModel(GalleryItem item, WaterfallViewModel owner, IThumbnailService thumbnailService)
    {
        Item = item;
        _owner = owner;
        _thumbnailService = thumbnailService;
    }

    /// <summary>底层数据项（打标重命名后就地替换，见 <see cref="UpdateFrom"/>）。</summary>
    public GalleryItem Item { get; private set; }

    /// <summary>卡片显示名（剥离标签段，D15：打标重命名不改变显示名与行序）。</summary>
    public string DisplayName => Item.DisplayName;

    /// <summary>完整文件名（含标签段；卡片 tooltip 用）。</summary>
    public string FullFileName => Path.GetFileName(Item.Path);

    /// <summary>宽高比（宽/高；未知回退 1:1，由 MasonryLayout 消费）。</summary>
    public double AspectRatio => Item.AspectRatio;

    /// <summary>缩略图（UI 线程创建的 BitmapImage；null = 浅色占位态）。</summary>
    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>选中态（选中集状态在 MainViewModel，本属性由其驱动）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>拖拽跟随小图的短边目标像素（2026-09-19 拖拽视觉：~120px 小缩略图随鼠标，替代整卡快照）。</summary>
    private const int DragVisualShortSide = 120;

    /// <summary>
    /// 拖拽跟随小图（短边约 120px）。2026-09-23 冻结修复：不再随缩略图加载预生成（原实现全链
    /// UI STA 续体，WIC 完成封送回 STA 与首帧布局互等成零 CPU 死锁——实机 8 次 ≥15s 冻结，
    /// 消融实验实锤），改为 <see cref="GetOrCreateDragVisualAsync"/> 拖拽发起时懒生成（后台线程）。
    /// null = 未生成/生成失败——WaterfallView.OnCardDragStarting 走回退链
    /// （懒生成 → Thumbnail BitmapImage → 系统默认整卡快照）。仅 UI 线程读写。
    /// </summary>
    private SoftwareBitmap? _dragVisual;

    /// <summary>拖拽跟随小图的只读访问（WaterfallView.OnCardDragStarting 消费；null = 走回退链）。</summary>
    internal SoftwareBitmap? DragVisual => _dragVisual;

    /// <summary>「+N」折叠角标的哨兵色相（转换器特判为黑色半透明底）。</summary>
    private const int MoreBadgeHue = -1;

    /// <summary>标签名 → 组色相索引（TagSidebarViewModel.Rebuild 在 UI 线程原子替换；纯展示数据）。</summary>
    private static IReadOnlyDictionary<string, int> _tagHues =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 以最新配置组重建「标签名 → 组色相」索引（UI 线程；由 TagSidebarViewModel.Rebuild 调用，
    /// 纯展示数据，与业务筛选/打标逻辑无关）。
    /// 返回索引内容是否变化（键值对全同视为未变化）——变化时宿主需对已呈现卡片补发
    /// <see cref="Badges"/> 重通知（见 <see cref="NotifyBadgeHuesChanged"/>）。
    /// </summary>
    internal static bool UpdateTagHues(IReadOnlyList<TagGroup> configGroups)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in configGroups)
        {
            var hue = TagGroupViewModel.HueOfName(group.Name);
            foreach (var tag in group.Tags)
            {
                map[tag.Name] = hue;
            }
        }

        // 内容比较（扫描期节流刷新/计数刷新会频繁 Rebuild：配置未变时不打扰已呈现卡片的角标绑定）。
        var changed = map.Count != _tagHues.Count;
        if (!changed)
        {
            foreach (var pair in map)
            {
                if (!_tagHues.TryGetValue(pair.Key, out var hue) || hue != pair.Value)
                {
                    changed = true;
                    break;
                }
            }
        }

        _tagHues = map;
        return changed;
    }

    /// <summary>
    /// 组色相索引更新后的角标重算通知（由 MainViewModel.RebuildTagSidebar 在
    /// <see cref="UpdateTagHues"/> 返回变化后对已呈现卡片补发）：Badges 是按需计算的派生属性，
    /// 静态索引替换不触发 INPC——打标时序为 UpdateFrom（先）→ Rebuild 更新索引（后），
    /// UpdateFrom 时通知的 Badges 用的是旧索引，已呈现卡片的角标集合/底色会滞后一轮
    ///（新标签尚未映射到组，按忽略口径被过滤掉，须待补发后才出角标）。
    /// </summary>
    internal void NotifyBadgeHuesChanged() => OnPropertyChanged(nameof(Badges));

    /// <summary>
    /// 缩略图左下角标签角标展示模型（前 3 个标签药丸 + 第 4 个起「+N」；每个角标取其所属组色相）。
    /// 2026-09-19 用户拍板：聚合类 UI 完全忽略无组标签——仅 _tagHues 命中（已映射到配置组）的标签
    /// 出角标（对齐 demo badgesHtml 只遍历配置组），「+N」溢出计数按过滤后的剩余数（过滤只减不加，
    /// 角标 hue 静态索引更新的既有联动不受影响）；无组标签的清理出口 = 单图右栏 chips ✕，
    /// 或在配置组内新建同名标签自然「收编」文件名中的同名脏标签（按名匹配配置）。
    /// </summary>
    public IReadOnlyList<TagBadgeViewModel> Badges
    {
        get
        {
            var list = new List<TagBadgeViewModel>(MaxVisibleTagBadges + 1);
            var matched = 0;
            foreach (var name in Item.Tags)
            {
                if (!_tagHues.TryGetValue(name, out var hue))
                {
                    continue; // 无组标签：不出角标（忽略口径，见上注释）。
                }

                if (matched < MaxVisibleTagBadges)
                {
                    list.Add(new TagBadgeViewModel(name, hue));
                }

                matched++;
            }

            if (matched > MaxVisibleTagBadges)
            {
                list.Add(new TagBadgeViewModel("+" + (matched - MaxVisibleTagBadges), MoreBadgeHue));
            }

            return list;
        }
    }

    /// <summary>
    /// 标签角标文本行：每图标签前 3 个 + "+N"（组色调简化为统一强调色，由模板前景色呈现）；
    /// 与 <see cref="Badges"/> 同步过滤无组标签（2026-09-19 忽略口径）。
    /// </summary>
    public string BadgeLine
    {
        get
        {
            // 仅统计映射到配置组的标签（与 Badges 同口径——demo tagsLine 同样只遍历组内）。
            var matched = Item.Tags.Where(t => _tagHues.ContainsKey(t)).ToList();
            if (matched.Count == 0)
            {
                return string.Empty;
            }

            return matched.Count <= MaxVisibleTagBadges
                ? string.Join(" · ", matched)
                : string.Join(" · ", matched.Take(MaxVisibleTagBadges)) + " +" + (matched.Count - MaxVisibleTagBadges);
        }
    }

    /// <summary>卡片双击 → 以单图模式打开（MainViewModel.OpenImageAsSingle）。</summary>
    public void OpenInViewer(object sender, DoubleTappedRoutedEventArgs e) => _owner.RaiseCardDoubleTapped(this);

    /// <summary>
    /// 开始按需加载缩略图（ItemsRepeater ElementPrepared 触发）。
    /// 幂等：已加载/加载中直接返回；失败保持占位（可由再次 Realize 重试）。
    /// </summary>
    public void BeginLoadThumbnail()
    {
        if (_thumbnailCts is not null || Thumbnail is not null)
        {
            return;
        }

        _thumbnailCts = new CancellationTokenSource();
        _ = LoadThumbnailAsync(_thumbnailCts.Token);
    }

    /// <summary>取消进行中的缩略图加载（ItemsRepeater ElementClearing 触发；已完成的为无操作）。</summary>
    public void CancelThumbnailLoad()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    /// <summary>
    /// 释放卡片持有的视觉资源（cr/P1-5，OnElementClearing 在 UI 线程调用）：
    /// 取消进行中的加载并退役已加载的 <see cref="Thumbnail"/>（bucket 360 位图约 300KB+）与
    /// <see cref="_dragVisual"/>（~120px SoftwareBitmap 约 70KB）——Items 全量常驻 VM（不随回收丢弃），
    /// 只取消不释放则滚动 N 张累积约 400KB×N，5 万张验收场景内存无界增长。
    /// Thumbnail 必须经 <see cref="ImageSourceRetirement"/> 退役（2026-09-24 崩溃根治）：直接置 null
    /// 会让旧 BitmapImage 落入 GC 终结器——终结器线程跨线程 Release XAML 对象（DependencyObject 族
    /// 须在创建线程析构），打标重排成批退休时 native 堆损坏闪退（fastfail/stowed dump 实锤）。
    /// 退役后卡片回退浅色占位；UI 线程串行性保证不会被回收后才续上的加载续体重新赋值
    /// （续体过闸门后仍有 ThrowIfCancellationRequested 检查）。卡片重新 Realize 时
    /// <see cref="BeginLoadThumbnail"/> 幂等条件（cts 与 Thumbnail 均 null）放行，走既有重载路径恢复
    /// （ThumbnailService 内存/磁盘缓存兜底，快速渐入）。
    /// </summary>
    public void ReleaseVisuals()
    {
        CancelThumbnailLoad();
        ImageSourceRetirement.Retire(Thumbnail);
        ImageSourceRetirement.Drain();
        Thumbnail = null;
        _dragVisual = null;
    }

    /// <summary>打标/重命名后就地更新数据项（路径/标签/显示名变化；选中态与滚动位置保持）。</summary>
    public void UpdateFrom(GalleryItem newItem)
    {
        Item = newItem;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(FullFileName));
        OnPropertyChanged(nameof(AspectRatio));
        OnPropertyChanged(nameof(BadgeLine));
        OnPropertyChanged(nameof(Badges));
    }

    /// <summary>同类加载失败的日志配额（每 VM 实例最多记 5 条进诊断日志）。</summary>
    private int _loadFailureLogCount;

    /// <summary>
    /// UI 侧缩略图应用闸门（2026-09-17 走查修复卡死）：BitmapImage.SetSourceAsync 与合成器交互，
    /// 首帧渲染期并发应用多张（清缓存后的全新解码风暴）曾致 UI 线程死锁（心跳 <1s 即停、消息泵假活）。
    /// 串行化应用段，一次只进一张；解码仍在后台并行，仅 UI 应用段排队。
    /// </summary>
    private static readonly SemaphoreSlim UiApplyGate = new(1, 1);

    private async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        // 捕获本次加载所属的 cts（2026-09-24 审计修复）：「回收→快速复用→再回收」两跳窗口里，
        // 旧加载的 finally 会误 dispose 掉已被替换的新 cts——取消保证失效，已回收卡片的续体
        // 仍可赋值 Thumbnail，产生绕过退役队列的悬挂 BitmapImage（裸交 GC）。
        var ownCts = _thumbnailCts;
        try
        {
            var result = await _thumbnailService.GetThumbnailAsync(Item.Path, ThumbnailBucket, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // JPEG 字节 → BitmapImage：UI 线程创建（ElementPrepared 与本 await 续体均在 UI 线程）；
            // 应用段经闸门串行（见 UiApplyGate 注释）。
            await UiApplyGate.WaitAsync(cancellationToken);
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(result.ImageBytes).AsRandomAccessStream())
                {
                    await bitmap.SetSourceAsync(stream);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Thumbnail = bitmap;
            }
            finally
            {
                UiApplyGate.Release();
            }

            // 拖拽小图不再在此预生成（2026-09-23 冻结修复）：原实现在每张缩略图应用后再跑一次
            // TryCreateDragVisualAsync（全链 UI STA 续体 + STA 创建的流），WIC 完成需封送回 STA——
            // 首帧布局期 UI 线程恰在原生工作中不泵消息时，与 WIC 线程互等成零 CPU 死锁（实机 8 次
            // ≥15s 冻结 + 本地复现，消融实验实锤：仅禁预生成即 6/6 轮无冻结）。改为 DragStarting
            // 时懒生成（GetOrCreateDragVisualAsync，带 GetDeferral），生成全程后台线程。
        }
        catch (OperationCanceledException)
        {
            // 视口外回收取消：占位保持，元素再次 Realize 时重新加载。
        }
        catch (Exception exception)
        {
            // 解码/读盘失败：保持浅色占位（瀑布流不中断；缩略图服务对缺失文件抛 FileNotFoundException）。
            // 2026-09-17 走查：占位块不消失问题定位——被吞的异常落诊断日志（每实例前 5 条，防刷屏）。
            if (Interlocked.Increment(ref _loadFailureLogCount) <= 5)
            {
                App.WriteDiagnosticLog($"[缩略图加载失败] bucket={ThumbnailBucket} path={Item.Path}", exception);
            }
        }
        finally
        {
            // 仅当字段仍指向本次加载的 cts 才清理（见方法头注释的竞态说明）。
            if (ReferenceEquals(_thumbnailCts, ownCts))
            {
                ownCts?.Dispose();
                _thumbnailCts = null;
            }
        }
    }

    /// <summary>
    /// 懒生成拖拽跟随小图（2026-09-23 冻结修复，替代原缩略图加载后的预生成）：DragStarting 经
    /// GetDeferral 等待本方法就绪再定跟随视觉；字节取缩略图缓存（首次拖拽时通常内存/磁盘命中，
    /// 小图解码数毫秒），生成全程后台线程（见 <see cref="ImageSourceHelper.TryCreateDragVisualAsync"/>
    /// 的线程化说明）。结果写入 <see cref="_dragVisual"/> 常驻，后续拖拽零成本。失败返回 null，
    /// 调用方走 Thumbnail → 系统快照回退链。
    /// </summary>
    internal async Task<SoftwareBitmap?> GetOrCreateDragVisualAsync()
    {
        if (_dragVisual is not null)
        {
            return _dragVisual;
        }

        try
        {
            var result = await _thumbnailService.GetThumbnailAsync(Item.Path, ThumbnailBucket, CancellationToken.None);
            _dragVisual = await ImageSourceHelper.TryCreateDragVisualAsync(
                result.ImageBytes, DragVisualShortSide, CancellationToken.None);
        }
        catch (Exception)
        {
            // 拖拽小图属锦上添花：任何失败静默降级（null → 回退链），不影响拖拽主链路。
        }

        return _dragVisual;
    }
}

/// <summary>标签角标展示模型（Text = 标签名或「+N」；Hue = 所属组色相，&lt;0 表示「+N」黑底样式）。</summary>
public sealed record TagBadgeViewModel(string Text, int Hue);
