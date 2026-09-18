// 职责：瀑布流卡片项视图模型——显示名（剥离标签段）/标签角标/缩略图槽位/选中态与卡片交互。
// 不变量：缩略图按需加载（ElementPrepared 触发、ElementClearing 取消；禁止一次性为全部项加载）；
//         JPEG 字节经 MemoryStream → BitmapImage 在 UI 线程桥接（ThumbnailResult.ImageBytes 契约）；
//         解码/读盘失败保持浅色占位不抛出；打标重命名后就地 UpdateFrom 更新（不重建、不重排，D15）。
// 调用链：WaterfallViewModel（创建/更新）→ WaterfallView DataTemplate（x:Bind）→ BeginLoadThumbnail → ThumbnailService。

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleViewer.Models;
using SimpleViewer.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;

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

    /// <summary>未映射到配置组的标签角标色相（灰蓝 200，视觉对齐 demo 未分组语义）。</summary>
    private const int UngroupedBadgeHue = 200;

    /// <summary>「+N」折叠角标的哨兵色相（转换器特判为黑色半透明底）。</summary>
    private const int MoreBadgeHue = -1;

    /// <summary>标签名 → 组色相索引（TagSidebarViewModel.Rebuild 在 UI 线程原子替换；纯展示数据）。</summary>
    private static IReadOnlyDictionary<string, int> _tagHues =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 以最新配置组重建「标签名 → 组色相」索引（UI 线程；由 TagSidebarViewModel.Rebuild 调用，
    /// 纯展示数据，与业务筛选/打标逻辑无关）。
    /// </summary>
    internal static void UpdateTagHues(IReadOnlyList<TagGroup> configGroups)
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

        _tagHues = map;
    }

    /// <summary>
    /// 缩略图左下角标签角标展示模型（前 3 个标签药丸 + 第 4 个起「+N」；
    /// 每个角标取其所属组色相，未分组标签灰蓝 200——对齐 demo .badge）。
    /// </summary>
    public IReadOnlyList<TagBadgeViewModel> Badges
    {
        get
        {
            var tags = Item.Tags;
            if (tags.Count == 0)
            {
                return [];
            }

            var shown = Math.Min(tags.Count, MaxVisibleTagBadges);
            var list = new List<TagBadgeViewModel>(shown + 1);
            for (var i = 0; i < shown; i++)
            {
                var name = tags[i];
                list.Add(new TagBadgeViewModel(
                    name,
                    _tagHues.TryGetValue(name, out var hue) ? hue : UngroupedBadgeHue));
            }

            if (tags.Count > shown)
            {
                list.Add(new TagBadgeViewModel("+" + (tags.Count - shown), MoreBadgeHue));
            }

            return list;
        }
    }

    /// <summary>标签角标文本行：每图标签前 3 个 + "+N"（组色调简化为统一强调色，由模板前景色呈现）。</summary>
    public string BadgeLine
    {
        get
        {
            var tags = Item.Tags;
            if (tags.Count == 0)
            {
                return string.Empty;
            }

            var shown = tags.Count <= MaxVisibleTagBadges
                ? string.Join(" · ", tags)
                : string.Join(" · ", tags.Take(MaxVisibleTagBadges)) + " +" + (tags.Count - MaxVisibleTagBadges);
            return shown;
        }
    }

    /// <summary>卡片单击 = 选中/取消选中（事件 x:Bind 入口；Ctrl/Shift 连选与 Ctrl+A 属 Step 10）。</summary>
    public void ToggleSelected(object sender, TappedRoutedEventArgs e) => _owner.RaiseCardTapped(this);

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
            _thumbnailCts?.Dispose();
            _thumbnailCts = null;
        }
    }
}

/// <summary>标签角标展示模型（Text = 标签名或「+N」；Hue = 所属组色相，&lt;0 表示「+N」黑底样式）。</summary>
public sealed record TagBadgeViewModel(string Text, int Hue);
