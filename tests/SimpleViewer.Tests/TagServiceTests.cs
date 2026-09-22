using SimpleViewer.Models;
using SimpleViewer.Services;

namespace SimpleViewer.Tests;

/// <summary>
/// TagService 标签操作测试（spec Step 3：T-TG1~12；T_ST_05 已删——拒绝点随删除连锁迁 MainViewModel，
/// 悬空绑定拒绝由 SettingsServiceTests T_ST_07~09 锁定）。
/// 全部使用 TempDirectory 建立真实文件，以落盘文件名为事实源断言；
/// TagSemantics 互斥语义另以纯函数级 Fact 直接覆盖。
/// batch-tag-management Step 2：T_TG_05 语义随删除纯化变更为锁定 RemoveTagAsync 按名移除
/// （未定义区连锁删除/右栏批量移除的服务层基础，spec D3）。
/// </summary>
public class TagServiceTests
{
    private readonly TagFilenameService _tagFilenameService = new();
    private readonly TagService _service;

    public TagServiceTests()
    {
        _service = new TagService(_tagFilenameService);
    }

    // ---------------------------------------------------------------- T-TG1：互斥组替换语义

    [Fact]
    public async Task T_TG_01_ExclusiveGroup_ReplacesSameGroupTagsOnDisk()
    {
        using var temp = new TempDirectory();
        var tagged = Path.Combine(temp.Path, "photo[A].jpg");
        var plain = Path.Combine(temp.Path, "plain.jpg");
        File.WriteAllText(tagged, "1");
        File.WriteAllText(plain, "2");
        var group = MakeGroup(exclusive: true, "A", "B");

        var result = await _service.ApplyTagAsync([tagged, plain], group.Tags[1], group);

        Assert.Equal(2, result.SucceededCount);
        Assert.False(result.HasFailures);
        // 已有同组标签 A 的文件被 B 替换；无标签文件直接追加 B
        Assert.Equal("photo[B].jpg", GetOnlyFileNames(temp.Path)[0]);
        Assert.Equal("plain[B].jpg", GetOnlyFileNames(temp.Path)[1]);
    }

    // ---------------------------------------------------------------- T-TG2：非互斥叠加

    [Fact]
    public async Task T_TG_02_NonExclusiveGroup_AppendsTag()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "photo[A].jpg");
        File.WriteAllText(path, "x");
        var group = MakeGroup(exclusive: false, "A", "B");

        var result = await _service.ApplyTagAsync([path], group.Tags[1], group);

        Assert.Equal(1, result.SucceededCount);
        Assert.False(result.HasFailures);
        // 非互斥组：已有 A 再打 B 为叠加而非替换
        Assert.Equal("photo[A B].jpg", GetOnlyFileNames(temp.Path).Single());
    }

    // ---------------------------------------------------------------- T-TG3：批量部分失败回执（预置同名冲突文件制造失败）

    [Fact]
    public async Task T_TG_03_BatchPartialFailure_AggregatesReceiptWithoutRollback()
    {
        using var temp = new TempDirectory();
        var a = Path.Combine(temp.Path, "a.jpg");
        var b = Path.Combine(temp.Path, "b.jpg");
        var c = Path.Combine(temp.Path, "c.jpg");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");
        File.WriteAllText(c, "3");
        // 预置 b 打标后的目标名，制造同名冲突（BuildNewPath 预检拦截）
        File.WriteAllText(Path.Combine(temp.Path, "b[风景].jpg"), "occupied");
        var group = MakeGroup(exclusive: false, "风景");

        var result = await _service.ApplyTagAsync([a, b, c], group.Tags[0], group);

        Assert.Equal(2, result.SucceededCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(b, failure.Path);
        Assert.Contains("已存在", failure.Reason);
        // 成功项不回滚：a/c 已重命名，失败项 b 保留原文件名
        Assert.Equal("a[风景].jpg", GetOnlyFileNames(temp.Path)[0]);
        Assert.True(File.Exists(b));
        Assert.Equal("c[风景].jpg", GetOnlyFileNames(temp.Path)[^1]);
    }

    // ---------------------------------------------------------------- T-TG4：RenameTag 全量更新（含多文件、位置保持、未含跳过）

    [Fact]
    public async Task T_TG_04_RenameTag_UpdatesAllMatchingFiles()
    {
        using var temp = new TempDirectory();
        var f1 = Path.Combine(temp.Path, "1[旧].jpg");
        var f2 = Path.Combine(temp.Path, "2[旧 位置].jpg");
        var f3 = Path.Combine(temp.Path, "3[别的].jpg");
        foreach (var f in new[] { f1, f2, f3 })
        {
            File.WriteAllText(f, "x");
        }

        var result = await _service.RenameTagAsync([f1, f2, f3], "旧", "新");

        // 仅含旧标签的 2 个文件被重命名；不含的 f3 跳过（不计成功也不计失败）
        Assert.Equal(2, result.SucceededCount);
        Assert.False(result.HasFailures);
        var names = GetOnlyFileNames(temp.Path);
        Assert.Equal("1[新].jpg", names[0]);
        // 替换保持标签原位置：旧 → 新，其余标签不动
        Assert.Equal("2[新 位置].jpg", names[1]);
        Assert.Equal("3[别的].jpg", names[2]);
    }

    // ---------------------------------------------------------------- T-TG5：RemoveTag 按名移除
    //（batch-tag-management Step 2：删除标签已纯化为配置操作、不再走文件管线；本用例语义随之变更为
    //  锁定 RemoveTagAsync 按名移除——未定义区连锁删除（D3）/右栏批量移除的服务层基础。）

    [Fact]
    public async Task T_TG_05_RemoveTag_RemovesTagByNameFromFiles()
    {
        using var temp = new TempDirectory();
        var f1 = Path.Combine(temp.Path, "1[A B].jpg");
        var f2 = Path.Combine(temp.Path, "2[a].jpg"); // 大小写变体同被移除
        var f3 = Path.Combine(temp.Path, "3[C].jpg"); // 不含该标签：幂等命中计成功
        foreach (var f in new[] { f1, f2, f3 })
        {
            File.WriteAllText(f, "x");
        }

        var result = await _service.RemoveTagAsync([f1, f2, f3], "A");

        Assert.Equal(3, result.SucceededCount);
        Assert.False(result.HasFailures);
        var names = GetOnlyFileNames(temp.Path);
        // 仅移除指定标签（大小写不敏感全移除），其余标签保留；标签清空的文件回到无标签名形态
        Assert.Equal("1[B].jpg", names[0]);
        Assert.Equal("2.jpg", names[1]);
        Assert.Equal("3[C].jpg", names[2]);
    }

    // ---------------------------------------------------------------- T-TG6：幂等打标（重复打同一标签文件名不变）

    [Fact]
    public async Task T_TG_06_ApplySameTag_IdempotentFileNameAndContentUnchanged()
    {
        using var temp = new TempDirectory();
        var content = new byte[] { 0x01, 0x02, 0xFF };
        var nonExclusive = Path.Combine(temp.Path, "p1[A].jpg");
        var exclusive = Path.Combine(temp.Path, "p2[A].jpg");
        File.WriteAllBytes(nonExclusive, content);
        File.WriteAllBytes(exclusive, content);
        var group = MakeGroup(exclusive: true, "A", "B");

        // 非互斥组重复打已有标签 A：叠加语义去重，文件名不变
        group.Exclusive = false;
        var r1 = await _service.ApplyTagAsync([nonExclusive], group.Tags[0], group);
        Assert.Equal(1, r1.SucceededCount);
        Assert.False(r1.HasFailures);
        Assert.Equal("p1[A].jpg", Path.GetFileName(nonExclusive));

        // 互斥组对仅含该标签的文件重复打 A：剔除后追加仍为 [A]，文件名不变
        group.Exclusive = true;
        var r2 = await _service.ApplyTagAsync([exclusive], group.Tags[0], group);
        Assert.Equal(1, r2.SucceededCount);
        Assert.False(r2.HasFailures);
        Assert.Equal("p2[A].jpg", Path.GetFileName(exclusive));

        // 无任何实际 IO：内容字节不变
        Assert.Equal(content, File.ReadAllBytes(nonExclusive));
        Assert.Equal(content, File.ReadAllBytes(exclusive));
    }

    // ---------------------------------------------------------------- T-TG7：长路径拒绝（超 260 聚合进 Failures）

    [Fact]
    public async Task T_TG_07_TooLongNewPath_FailureAggregated()
    {
        using var temp = new TempDirectory();
        // Windows 260 字符口径（2026-09-19 起与 Linux 255 字节口径双预检）：ASCII 文件名动态适配
        // 临时目录深度——源路径可真实创建（< 260），打短标签后全路径必超 260，组件名 UTF-8 字节
        // 控制在 255 以内（不先触发 Linux 口径，该口径见 T_TG_12）。
        var nameLength = Math.Max(10, 254 - temp.Path.Length);
        var path = Path.Combine(temp.Path, new string('a', nameLength) + ".jpg");
        File.WriteAllText(path, "x");
        var group = MakeGroup(exclusive: false, "t");

        var result = await _service.ApplyTagAsync([path], group.Tags[0], group);

        Assert.Equal(0, result.SucceededCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(path, failure.Path);
        Assert.Contains("260", failure.Reason);
        Assert.DoesNotContain("255", failure.Reason);
        // 原文件原样保留
        Assert.True(File.Exists(path));
        Assert.Single(GetOnlyFileNames(temp.Path));
    }

    // ---------------------------------------------------------------- T-TG12：组件名超 Linux 255 字节拒绝（2026-09-19 标签预算）

    [Fact]
    public async Task T_TG_12_TooLongFileNameBytes_FailureAggregated()
    {
        using var temp = new TempDirectory();
        // 组件名用汉字拼超 255 UTF-8 字节（图×90 = 270 字节 + .jpg = 274），全路径字符数
        // 控制在 260 以内（Windows 口径不触发）——与 T_TG_07 双口径独立断言。
        var path = Path.Combine(temp.Path, new string('图', 90) + ".jpg");
        File.WriteAllText(path, "x");
        var group = MakeGroup(exclusive: false, "标");

        var result = await _service.ApplyTagAsync([path], group.Tags[0], group);

        Assert.Equal(0, result.SucceededCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(path, failure.Path);
        Assert.Contains("255", failure.Reason);
        Assert.DoesNotContain("260", failure.Reason);
        // 原文件原样保留
        Assert.True(File.Exists(path));
        Assert.Single(GetOnlyFileNames(temp.Path));
    }

    // ---------------------------------------------------------------- T-TG8：GIF 打标仅改名不动内容

    [Fact]
    public async Task T_TG_08_GifTagging_RenamesOnlyContentUntouched()
    {
        using var temp = new TempDirectory();
        var bytes = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x02, 0x03, 0xFF, 0x00 };
        var path = Path.Combine(temp.Path, "anim.gif");
        File.WriteAllBytes(path, bytes);
        var group = MakeGroup(exclusive: false, "风景");

        var result = await _service.ApplyTagAsync([path], group.Tags[0], group);

        Assert.Equal(1, result.SucceededCount);
        Assert.False(result.HasFailures);
        var newPath = Path.Combine(temp.Path, "anim[风景].gif");
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(path));
        // 改名前后内容字节完全一致（仅重命名，未触碰文件内容）
        Assert.Equal(bytes, File.ReadAllBytes(newPath));
    }

    // ---------------------------------------------------------------- T-TG9：组外（未分组）标签在互斥替换后保留

    [Fact]
    public async Task T_TG_09_OutOfGroupTags_PreservedAfterExclusiveReplace()
    {
        using var temp = new TempDirectory();
        // 文件已带组内标签"风景"与未分组标签"精选"
        var path = Path.Combine(temp.Path, "photo[风景 精选].jpg");
        File.WriteAllText(path, "x");
        var group = MakeGroup(exclusive: true, "风景", "人像");

        var result = await _service.ApplyTagAsync([path], group.Tags[1], group);

        Assert.Equal(1, result.SucceededCount);
        Assert.False(result.HasFailures);
        // 同组"风景"被替换，组外"精选"保留，新标签追加尾部
        Assert.Equal("photo[精选 人像].jpg", GetOnlyFileNames(temp.Path).Single());
    }

    // ---------------------------------------------------------------- T-TG10：互斥⇄非互斥切换不改已落盘标签，操作按执行时组属性生效

    [Fact]
    public async Task T_TG_10_ExclusiveToggle_DiskTagsFollowSemanticsAtExecutionTime()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "photo.jpg");
        File.WriteAllText(path, "x");
        var group = MakeGroup(exclusive: true, "A", "B", "C");

        // 互斥期打 A → 仅 [A]
        var r1 = await _service.ApplyTagAsync([path], group.Tags[0], group);
        Assert.Equal(1, r1.SucceededCount);
        Assert.Equal("photo[A].jpg", GetOnlyFileNames(temp.Path).Single());

        // 切换为非互斥（切换本身不改文件），再打 B → 叠加，A 仍在
        group.Exclusive = false;
        path = Path.Combine(temp.Path, "photo[A].jpg");
        var r2 = await _service.ApplyTagAsync([path], group.Tags[1], group);
        Assert.Equal(1, r2.SucceededCount);
        Assert.Equal("photo[A B].jpg", GetOnlyFileNames(temp.Path).Single());

        // 切回互斥，再打 C → 同组 A、B 均被剔除，仅剩 [C]
        group.Exclusive = true;
        path = Path.Combine(temp.Path, "photo[A B].jpg");
        var r3 = await _service.ApplyTagAsync([path], group.Tags[2], group);
        Assert.Equal(1, r3.SucceededCount);
        Assert.Equal("photo[C].jpg", GetOnlyFileNames(temp.Path).Single());
    }

    // ---------------------------------------------------------------- T-ST5：已删（batch-tag-management Step 2）
    // 拒绝点迁 MainViewModel（VM 层不可单测，A2 由走查+代码审查覆盖）；
    // 悬空绑定拒绝由 SettingsServiceTests T_ST_07~09 锁定。

    // ---------------------------------------------------------------- T-TG11：仅大小写差异的重命名按幂等跳过（口径统一）

    [Fact]
    public async Task T_TG_11_CaseOnlyRename_TreatedAsIdempotentNoOp()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "photo[a].jpg");
        File.WriteAllText(path, "x");

        // 旧口径分裂：服务端按 Ordinal 判非幂等执行 File.Move（磁盘改为 [A]），
        // 同步侧按 OrdinalIgnoreCase 判幂等跳过——内存/索引停留旧路径。
        // 统一为 Windows 大小写不敏感口径（OrdinalIgnoreCase）后：双方一致视为无操作。
        var result = await _service.RenameTagAsync([path], "a", "A");

        Assert.Equal(0, result.SucceededCount);
        Assert.False(result.HasFailures);
        Assert.Equal("photo[a].jpg", GetOnlyFileNames(temp.Path).Single()); // 文件名保持原样
    }

    // ------------------------------------------------- TagSemantics 纯函数补充（独立可测，spec Step 3）

    [Fact]
    public void TagSemantics_Exclusive_RemovesOnlySameGroupTags()
    {
        var group = MakeGroup(exclusive: true, "风景", "人像");

        var result = TagSemantics.Apply(new[] { "风景", "精选", "置顶" }, group, "人像");

        // 仅同组"风景"被剔除，组外标签按原顺序保留，目标标签追加尾部
        Assert.Equal(new[] { "精选", "置顶", "人像" }, result);
    }

    [Fact]
    public void TagSemantics_NonExclusive_AppendsWithDedup()
    {
        var group = MakeGroup(exclusive: false, "风景", "人像");

        // 新标签追加
        Assert.Equal(new[] { "风景", "人像" }, TagSemantics.Apply(new[] { "风景" }, group, "人像"));
        // 已存在同名标签不重复追加（大小写不敏感，与标签重名拒绝口径一致）
        var latinGroup = MakeGroup(exclusive: false, "alpha", "beta");
        Assert.Equal(new[] { "alpha" }, TagSemantics.Apply(new[] { "alpha" }, latinGroup, "ALPHA"));
    }

    /// <summary>构造测试用标签组（Id 稳定：tag-<名称>）。</summary>
    private static TagGroup MakeGroup(bool exclusive, params string[] tagNames) => new()
    {
        Id = "group-1",
        Name = "测试组",
        Exclusive = exclusive,
        Tags = [.. tagNames.Select(n => new TagDefinition { Id = "tag-" + n, Name = n })],
    };

    /// <summary>取目录内全部文件名（字典序稳定排序）。</summary>
    private static IReadOnlyList<string> GetOnlyFileNames(string directory)
        => Directory.GetFiles(directory).Select(p => Path.GetFileName(p)!).Order().ToList();

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sv-tests-" + Guid.NewGuid().ToString("N"));

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
