using SimpleViewer.Services;

namespace SimpleViewer.Tests;

/// <summary>
/// 标签筛选状态机单测（cr/P2-3）：锁定「无修饰单选重置 / 唯一选中再点取消 / Ctrl 加减选 /
/// 无标签⇄标签互斥清位 / Matches 三分支」语义——侧栏、工具栏、快捷键三入口的筛选行为
/// 共用该状态机（MainViewModel 薄包装），回归即三入口同时回归。
/// </summary>
public class TagFilterStateTests
{
    [Fact]
    public void T_TF_S01_Toggle_NoModifier_ReplacesMultiSelectionWithSingle()
    {
        // 无修饰 + 多选集（或不同标签）：单选重置——整集被替换为仅该标签。
        var (tags, untagged) = TagFilterState.Toggle(["风景", "人物"], untagged: false, "美食", ctrl: false);

        Assert.Equal(["美食"], tags);
        Assert.False(untagged);
    }

    [Fact]
    public void T_TF_S02_Toggle_NoModifier_SoleSelectedTagAgain_ClearsFilter()
    {
        // 无修饰 + 当前唯一选中就是它：取消筛选回全量（保留"二次点击取消"习惯）。
        var (tags, untagged) = TagFilterState.Toggle(["风景"], untagged: false, "风景", ctrl: false);

        Assert.Empty(tags);
        Assert.False(untagged);

        // 大小写不敏感（与原 HashSet(OrdinalIgnoreCase) 口径一致）。
        var (cased, _) = TagFilterState.Toggle(["Scenery"], untagged: false, "scenery", ctrl: false);
        Assert.Empty(cased);
    }

    [Fact]
    public void T_TF_S03_Toggle_Ctrl_AddsTagToSelection()
    {
        // Ctrl + 不在集中：加入——多标签 OR 的逐标签开关。
        var (tags, untagged) = TagFilterState.Toggle(["风景"], untagged: false, "人物", ctrl: true);

        Assert.Equal(["风景", "人物"], tags);
        Assert.False(untagged);
    }

    [Fact]
    public void T_TF_S04_Toggle_Ctrl_RemovesTagFromSelection()
    {
        // Ctrl + 已在集中：移除（其余成员保留，可减到空集 = 回全量）。
        var (tags, untagged) = TagFilterState.Toggle(["风景", "人物"], untagged: false, "风景", ctrl: true);

        Assert.Equal(["人物"], tags);
        Assert.False(untagged);

        var (emptied, _) = TagFilterState.Toggle(["人物"], untagged: false, "人物", ctrl: true);
        Assert.Empty(emptied);

        // 大小写不敏感移除。
        var (cased, _) = TagFilterState.Toggle(["Scenery"], untagged: false, "scenery", ctrl: true);
        Assert.Empty(cased);
    }

    [Fact]
    public void T_TF_S05_Toggle_FromUntagged_ClearsUntaggedFlag()
    {
        // 无标签激活时点标签：退出无标签模式（互斥清位）并按普通语义切换（此时标签集恒空 → 单选重置）。
        var (tags, untagged) = TagFilterState.Toggle([], untagged: true, "风景", ctrl: false);

        Assert.Equal(["风景"], tags);
        Assert.False(untagged);

        // Ctrl 同样清位（进入标签筛选的任何分支都退出无标签模式）。
        var (ctrlTags, ctrlUntagged) = TagFilterState.Toggle([], untagged: true, "风景", ctrl: true);
        Assert.Equal(["风景"], ctrlTags);
        Assert.False(ctrlUntagged);
    }

    [Fact]
    public void T_TF_S06_ToggleUntagged_FromTagFilter_ActivatingClearsTagSet()
    {
        // 标签态激活切无标签：清空标签集（互斥清集）并置无标签位。
        var (tags, untagged) = TagFilterState.ToggleUntagged(["风景", "人物"], untagged: false);

        Assert.Empty(tags);
        Assert.True(untagged);

        // 无标签态再点：取消回全量（标签集原样保留——激活期间恒空，语义等价）。
        var (kept, off) = TagFilterState.ToggleUntagged(["残留"], untagged: true);
        Assert.Equal(["残留"], kept);
        Assert.False(off);
    }

    [Fact]
    public void T_TF_S07_Matches_ThreeBranches()
    {
        // 分支一：无标签态——项无任何标签才命中，有标签不命中。
        Assert.True(TagFilterState.Matches([], filterTags: ["风景"], untagged: true));
        Assert.True(TagFilterState.Matches(null, filterTags: ["风景"], untagged: true));
        Assert.False(TagFilterState.Matches(["风景"], filterTags: ["风景"], untagged: true));

        // 分支二：标签态——项的任一标签命中激活集（OR；大小写不敏感）。
        Assert.True(TagFilterState.Matches(["风景", "美食"], filterTags: ["美食", "人物"], untagged: false));
        Assert.True(TagFilterState.Matches(["Scenery"], filterTags: ["scenery"], untagged: false));

        // 分支三：标签态——无一命中则不命中；激活集为空恒不命中（无筛选态不走本谓词）。
        Assert.False(TagFilterState.Matches(["建筑"], filterTags: ["美食", "人物"], untagged: false));
        Assert.False(TagFilterState.Matches(["建筑"], filterTags: [], untagged: false));
        Assert.False(TagFilterState.Matches(["建筑"], filterTags: null, untagged: false));
    }
}
