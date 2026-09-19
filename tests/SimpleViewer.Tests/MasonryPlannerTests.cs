using SimpleViewer.Helpers;

namespace SimpleViewer.Tests;

/// <summary>
/// 瀑布流布局规划器单测（cr/P2-10，T-MS1~4）：锁定「尾部追加不重排（D15）/ 列数变化全量重算 /
/// Count 回退重算 / max(2,⌊w/240⌋) 列数口径」四个口径——MasonryLayout 对外行为不变，
/// 仅布局核心实现位置移动（Views → Core），实机瀑布流布局依赖这些语义不回归。
/// </summary>
public class MasonryPlannerTests
{
    /// <summary>构造 2 列布局（视口 500：列宽 = ⌊(500-14)/2⌋ = 243）并追加三个基准项。</summary>
    private static MasonryPlanner BuildBaselinePlanner(out MasonrySlot[] baselineSlots)
    {
        var planner = new MasonryPlanner();
        planner.Reset(columns: 2, appliedWidth: 500, cardWidth: 243);
        planner.AppendItem(1.0); // 列 0：(0, 0, 243, 291)
        planner.AppendItem(1.0); // 列 1：(257, 0, 243, 291)
        planner.AppendItem(2.0); // 并列取左 → 列 0：(0, 305, 243, 169)
        baselineSlots = [planner[0], planner[1], planner[2]];
        return planner;
    }

    [Fact]
    public void T_MS_1_AppendItem_DoesNotRearrangeExistingSlots()
    {
        // D15：渐进呈现（扫描追加块）只续算新槽位，既有项位置逐字节不变。
        var planner = BuildBaselinePlanner(out var baseline);

        // 复用条件成立：列数相同、宽度未越阈、数据只增不减。
        Assert.True(planner.CanReuse(columns: 2, viewportWidth: 500, itemCount: 4, widthSnapTolerance: 40));

        var appended = planner.AppendItem(1.0);

        // 既有三个槽位不动（渐进追加不重排已布局项）。
        Assert.Equal(baseline[0], planner[0]);
        Assert.Equal(baseline[1], planner[1]);
        Assert.Equal(baseline[2], planner[2]);

        // 新项落在当前最矮列（列 1：高 305 < 列 0：高 488），X 与列 1 对齐。
        Assert.Equal(4, planner.Count);
        Assert.Equal(257, appended.X);
        Assert.Equal(305, appended.Y);

        // 槽位几何口径：卡片高 = ⌊卡宽/宽高比⌋ + 文字区 48。
        Assert.Equal(243, appended.Width);
        Assert.Equal(Math.Floor(243 / 1.0) + MasonryPlanner.TextAreaHeight, appended.Height);

        // 内容总高 = 最高列高（列 1：305 + 291 + 14 = 610）减去末卡多算的一个间距 14。
        Assert.Equal(305 + 291, planner.ExtentHeight);
    }

    [Fact]
    public void T_MS_2_ColumnCountChange_ForcesFullRecompute()
    {
        // 视口 500（2 列）→ 1000（4 列）：CanReuse 判 false，调用方 Reset 后按新列参数全量重排。
        var planner = BuildBaselinePlanner(out _);
        Assert.False(planner.CanReuse(
            columns: MasonryPlanner.ComputeColumns(1000),
            viewportWidth: 1000,
            itemCount: 3,
            widthSnapTolerance: 40));

        // 全量重排：4 列（列宽 = ⌊(1000-3×14)/4⌋ = 239），同样三项的落位与新列宽一致。
        planner.Reset(columns: 4, appliedWidth: 1000, cardWidth: 239);
        Assert.Equal(0, planner.Count);
        Assert.True(planner.HasLayout);

        planner.AppendItem(1.0);
        planner.AppendItem(1.0);
        planner.AppendItem(2.0);

        Assert.Equal(0, planner[0].X);          // 列 0
        Assert.Equal(239 + 14, planner[1].X);   // 列 1（新列宽 239 + 间距 14，不再是 2 列时的 257）

        // 4 列下第三个空列先于次行：第三项落在列 2（X = 2×(239+14)，Y = 0）——
        // 与 2 列布局的落位（列 0 次行 X=0 Y=305）不同，证明确按新列参数全量重排。
        Assert.Equal(2 * (239 + 14), planner[2].X);
        Assert.Equal(0, planner[2].Y);

        // 内容横向总宽 = 4 列 × 239 + 3 × 14。
        Assert.Equal(4 * 239 + 3 * 14, planner.LayoutWidth);
    }

    [Fact]
    public void T_MS_3_CountShrink_ForcesFullRecompute()
    {
        // 数据源缩减（ResetFrom 筛选整体替换到更小命中集）：Count 回退（3 > 2）判 false，
        // 旧槽位不可续用，须 Reset 后从零重排。
        var planner = BuildBaselinePlanner(out _);
        Assert.Equal(3, planner.Count);
        Assert.False(planner.CanReuse(columns: 2, viewportWidth: 500, itemCount: 2, widthSnapTolerance: 40));

        // 同参数数据只增不减时判 true（对照）。
        Assert.True(planner.CanReuse(columns: 2, viewportWidth: 500, itemCount: 3, widthSnapTolerance: 40));

        // Reset 后重新追加 2 项：计数与槽位从新命中集起算。
        planner.Reset(columns: 2, appliedWidth: 500, cardWidth: 243);
        planner.AppendItem(1.0);
        planner.AppendItem(3.0);
        Assert.Equal(2, planner.Count);
        Assert.Equal(0, planner[0].Y);
        Assert.Equal(0, planner[1].Y);
    }

    [Fact]
    public void T_MS_4_ComputeColumns_MinTwoAndFloorQuotient()
    {
        // 列数口径：max(2, ⌊视口宽/240⌋)——窄视口至少 2 列，商向下取整。
        Assert.Equal(2, MasonryPlanner.ComputeColumns(100));   // ⌊100/240⌋=0 → 兜底 2
        Assert.Equal(2, MasonryPlanner.ComputeColumns(240));   // ⌊240/240⌋=1 → 2
        Assert.Equal(2, MasonryPlanner.ComputeColumns(479));   // ⌊479/240⌋=1 → 2
        Assert.Equal(2, MasonryPlanner.ComputeColumns(480));   // ⌊480/240⌋=2
        Assert.Equal(3, MasonryPlanner.ComputeColumns(720));   // =3
        Assert.Equal(5, MasonryPlanner.ComputeColumns(1200));  // =5

        // 列宽口径：均分视口宽（去列间距）后向下取整，至少 1。
        Assert.Equal(243, MasonryPlanner.ComputeCardWidth(500, 2));   // ⌊(500-14)/2⌋
        Assert.Equal(239, MasonryPlanner.ComputeCardWidth(1000, 4));  // ⌊(1000-42)/4⌋

        // 非法宽高比回退 1:1（与 GalleryItem.AspectRatio 口径一致）。
        var planner = new MasonryPlanner();
        planner.Reset(columns: 2, appliedWidth: 500, cardWidth: 243);
        var normal = planner.AppendItem(1.0);
        planner.Reset(columns: 2, appliedWidth: 500, cardWidth: 243);
        var invalid = planner.AppendItem(0);
        var nan = planner.AppendItem(double.NaN);
        Assert.Equal(normal.Height, invalid.Height);
        Assert.Equal(normal.Height, nan.Height);
    }
}
