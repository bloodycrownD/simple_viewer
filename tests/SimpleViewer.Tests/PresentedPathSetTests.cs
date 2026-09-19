using SimpleViewer.Helpers;

namespace SimpleViewer.Tests;

/// <summary>
/// 已呈现路径集去重单测（cr/P1-2）：锁定「ResetWith 重建 / TryAdd 查重 / OrdinalIgnoreCase」
/// 三个口径——筛选 ResetFrom 与扫描渐进追加重叠时不产生重复卡片的行为依赖于此。
/// </summary>
public class PresentedPathSetTests
{
    [Fact]
    public void T_PP_01_TryAdd_FirstPathAccepted_DuplicateRejected()
    {
        var set = new PresentedPathSet();

        // 首次登记成功（应追加卡片）
        Assert.True(set.TryAdd(@"C:\lib\a[tag].jpg"));

        // 同路径（含大小写不同）再登记被拒——同一图片不出现第二张卡片
        Assert.False(set.TryAdd(@"C:\lib\a[tag].jpg"));
        Assert.False(set.TryAdd(@"c:\LIB\A[TAG].JPG"));

        // 不同路径正常登记
        Assert.True(set.TryAdd(@"C:\lib\b.jpg"));
    }

    [Fact]
    public void T_PP_02_ResetWith_RebuildsSet_DuplicateAfterResetReaccepted()
    {
        var set = new PresentedPathSet();
        Assert.True(set.TryAdd(@"C:\lib\a.jpg"));
        Assert.False(set.TryAdd(@"C:\lib\a.jpg"));

        // 筛选切换整体重置：集合内容 = 新呈现集，旧路径不再占用
        set.ResetWith(new[] { @"C:\lib\b.jpg" });
        Assert.False(set.TryAdd(@"C:\lib\b.jpg")); // 新集合内已有
        Assert.True(set.TryAdd(@"C:\lib\a.jpg"));  // 不在新集合内，可重新呈现

        // 空重置（重开图库清空瀑布流）：所有路径可重新登记
        set.ResetWith([]);
        Assert.True(set.TryAdd(@"C:\lib\b.jpg"));
    }

    [Fact]
    public void T_PP_03_ResetWith_EnumeratesInputOnce_LiveListSafe()
    {
        // ResetFrom 的输入可能是扫描中的活集合（_galleryItems）：确认单次枚举快照语义，
        // 且 ResetWith 之后继续向源集合追加不影响已建集合。
        var source = new List<string> { @"C:\lib\one.png", @"C:\lib\two.png" };
        var set = new PresentedPathSet();
        set.ResetWith(source);

        source.Add(@"C:\lib\three.png");

        Assert.False(set.TryAdd(@"C:\lib\two.png"));
        Assert.True(set.TryAdd(@"C:\lib\three.png")); // ResetWith 之后加入源集合的项不在已呈现集
    }
}
