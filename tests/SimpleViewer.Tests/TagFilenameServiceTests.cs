using SimpleViewer.Services;

namespace SimpleViewer.Tests;

public class TagFilenameServiceTests
{
    private readonly TagFilenameService _service = new();

    [Fact]
    public void T_TF_01_NoTags_ParsesEmptyTagSet()
    {
        var ok = _service.TryParse("photo.jpg", out var baseName, out var extension, out var tags);

        Assert.True(ok);
        Assert.Equal("photo", baseName);
        Assert.Equal(".jpg", extension);
        Assert.Empty(tags);
        // 空标签集合合成后应还原为无方括号文件名（往返一致）
        Assert.Equal("photo.jpg", _service.Compose(baseName, extension, tags));
    }

    [Fact]
    public void T_TF_02_RoundTrip_PreservesTagsAndNormalizesWhitespace()
    {
        // 解析：a[风景 已修].jpg → 基名 a / 扩展名 .jpg / 标签 [风景, 已修]
        var ok = _service.TryParse("a[风景 已修].jpg", out var baseName, out var extension, out var tags);
        Assert.True(ok);
        Assert.Equal("a", baseName);
        Assert.Equal(".jpg", extension);
        Assert.Equal(new[] { "风景", "已修" }, tags);

        // 往返一致：解析结果合成后与原文件名相同
        Assert.Equal("a[风景 已修].jpg", _service.Compose(baseName, extension, tags));

        // 多重半角空格归一
        ok = _service.TryParse("a[风景  已修].jpg", out _, out _, out var multiSpaceTags);
        Assert.True(ok);
        Assert.Equal(new[] { "风景", "已修" }, multiSpaceTags);

        // 全角空格 \u3000 与不换行空格 \u00A0 同样按空白归一为标签边界
        ok = _service.TryParse("a[风景\u3000已修\u00A0x].jpg", out _, out _, out var wideSpaceTags);
        Assert.True(ok);
        Assert.Equal(new[] { "风景", "已修", "x" }, wideSpaceTags);
    }

    [Fact]
    public void T_TF_03_NonTailBracket_NotTreatedAsTags()
    {
        // a[b]c.jpg 的方括号位于基名中部，不视为标签段
        var ok = _service.TryParse("a[b]c.jpg", out var baseName, out var extension, out var tags);
        Assert.True(ok);
        Assert.Equal("a[b]c", baseName);
        Assert.Equal(".jpg", extension);
        Assert.Empty(tags);

        // 往返合成：非尾部方括号原样保留在基名中
        Assert.Equal("a[b]c.jpg", _service.Compose(baseName, extension, tags));
    }

    [Fact]
    public void T_TF_04_ValidateTagName_RejectsEmptyWhitespaceAndBrackets()
    {
        Assert.True(_service.ValidateTagName("风景"));
        Assert.True(_service.ValidateTagName("已修-v2"));

        // 空与 null
        Assert.False(_service.ValidateTagName(null));
        Assert.False(_service.ValidateTagName(string.Empty));

        // 空白字符全集（char.IsWhiteSpace）：半角空格/内嵌空格/全角空格/nbsp/制表符/换行
        Assert.False(_service.ValidateTagName(" "));
        Assert.False(_service.ValidateTagName("a b"));
        Assert.False(_service.ValidateTagName("全角\u3000"));
        Assert.False(_service.ValidateTagName("nbsp\u00A0"));
        Assert.False(_service.ValidateTagName("tab\t"));
        Assert.False(_service.ValidateTagName("换行\n"));

        // 方括号（破坏尾部标签段结构）
        Assert.False(_service.ValidateTagName("a[b"));
        Assert.False(_service.ValidateTagName("b]c"));

        // 文件系统非法字符（cr/P2-16：Path.GetInvalidFileNameChars 全集——打标即改名，前置拒绝防 File.Move 整批失败；
        // 含 \ 还可能拼出跨目录路径分量）
        Assert.False(_service.ValidateTagName(@"a\b"));
        Assert.False(_service.ValidateTagName("a/b"));
        Assert.False(_service.ValidateTagName("a:b"));
        Assert.False(_service.ValidateTagName("a*b"));
    }

    [Fact]
    public void T_TF_05_BuildNewPath_TooLongPath_ReturnsExplicitFailure()
    {
        // Windows 260 字符口径单命中（2026-09-19 起与 Linux 255 字节口径双预检）：
        // 目录用 ASCII 拼超长、组件名字节数控制在 255 以内（不先触发 Linux 口径，该口径见 T_TF_10）。
        var oldPath = @"C:\sv-tests\" + new string('d', 250) + @"\photo.jpg";

        var result = _service.BuildNewPath(oldPath, new[] { "标签" });

        Assert.False(result.Success);
        Assert.Null(result.NewFullPath);
        Assert.NotNull(result.Error);
        Assert.Contains("260", result.Error);
        Assert.DoesNotContain("255", result.Error);

        // 恰好 260 被拒（cr/P2-13：>= 口径，PRD"将达到 260 即阻止"）——
        // 目录 246（C:\sv-tests\ + 234 个 d）+ 分隔符 1 + "photo[标签].jpg" 13 字符 = 恰好 260；
        // 少一个 d 即 259，边界另一侧放行（纯预检：目录无需真实存在，目标冲突不触发）。
        var at260 = _service.BuildNewPath(@"C:\sv-tests\" + new string('d', 234) + @"\photo.jpg", new[] { "标签" });
        Assert.False(at260.Success);
        Assert.NotNull(at260.Error);
        Assert.Contains("260", at260.Error);

        var at259 = _service.BuildNewPath(@"C:\sv-tests\" + new string('d', 233) + @"\photo.jpg", new[] { "标签" });
        Assert.True(at259.Success);
        Assert.Equal(259, at259.NewFullPath!.Length);
    }

    [Fact]
    public void T_TF_06_BuildNewPath_TargetExists_ReturnsConflictFailure()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "photo.jpg");
        File.WriteAllText(sourcePath, "x");
        // 预先创建与新标签集合对应的目标文件，制造同名冲突
        File.WriteAllText(Path.Combine(temp.Path, "photo[新标签].jpg"), "x");

        var result = _service.BuildNewPath(sourcePath, new[] { "新标签" });

        Assert.False(result.Success);
        Assert.Null(result.NewFullPath);
        Assert.NotNull(result.Error);
        Assert.Contains("已存在", result.Error);
    }

    [Fact]
    public void T_TF_07_BuildNewPath_NoConflict_ReturnsNewPath()
    {
        using var temp = new TempDirectory();
        // 源文件无需真实存在：BuildNewPath 是纯路径预检，存在性由调用方保证
        var sourcePath = Path.Combine(temp.Path, "photo[旧标签].jpg");

        var result = _service.BuildNewPath(sourcePath, new[] { "风景" });

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(temp.Path, "photo[风景].jpg"), result.NewFullPath);
    }

    // ------------------------------------------------- 文件名标签预算纯函数（Linux 255 UTF-8 字节口径，2026-09-19）

    [Fact]
    public void T_TF_08_TagFilenameBudget_ByteBudgetsForAsciiCjkAndMixed()
    {
        // 纯 ASCII：photo[ab].jpg = 13 字节，剩余预算 242
        Assert.Equal("photo[ab].jpg", TagFilenameBudget.ComposeFileName("photo", ".jpg", new[] { "ab" }));
        Assert.Equal(13, TagFilenameBudget.GetFileNameByteCount("photo", ".jpg", new[] { "ab" }));
        Assert.Equal(242, TagFilenameBudget.GetRemainingBytes("photo", ".jpg", new[] { "ab" }));

        // 中文标签（UTF-8 汉字 3 字节）：照片[风景].jpg = 6+1+6+1+4 = 18 字节，剩余 237
        Assert.Equal(18, TagFilenameBudget.GetFileNameByteCount("照片", ".jpg", new[] { "风景" }));
        Assert.Equal(237, TagFilenameBudget.GetRemainingBytes("照片", ".jpg", new[] { "风景" }));

        // 混合：无标签时组件 = base + ext（照片.jpg = 6+4 = 10 字节）
        Assert.Equal("照片.jpg", TagFilenameBudget.ComposeFileName("照片", ".jpg", Array.Empty<string>()));
        Assert.Equal(10, TagFilenameBudget.GetFileNameByteCount("照片", ".jpg", Array.Empty<string>()));

        // 多标签以单空格连接（与 Compose 同拼接规则，口径唯一来源）
        Assert.Equal("a[b c].jpg", TagFilenameBudget.ComposeFileName("a", ".jpg", new[] { "b", "c" }));
        Assert.Equal(10, TagFilenameBudget.GetFileNameByteCount("a", ".jpg", new[] { "b", "c" }));

        // 预算内预检通过（返回 null）
        Assert.Null(TagFilenameBudget.CheckFileNameBudget("photo", ".jpg", new[] { "ab" }));
    }

    [Fact]
    public void T_TF_09_TagFilenameBudget_BoundaryAtExactly255Bytes()
    {
        // 恰好达界：方括号 2 字节 + 单标签 253 个 ASCII = 255 字节 → 剩余 0，预检通过
        var exact = new string('a', 253);
        Assert.Equal(255, TagFilenameBudget.GetFileNameByteCount("", "", new[] { exact }));
        Assert.Equal(0, TagFilenameBudget.GetRemainingBytes("", "", new[] { exact }));
        Assert.Null(TagFilenameBudget.CheckFileNameBudget("", "", new[] { exact }));

        // 超界 1 字节：方括号 2 字节 + 254 个 ASCII = 256 字节 → 剩余 -1，预检拒绝并附超出字节数
        var over = new string('a', 254);
        Assert.Equal(-1, TagFilenameBudget.GetRemainingBytes("", "", new[] { over }));
        var error = TagFilenameBudget.CheckFileNameBudget("", "", new[] { over });
        Assert.NotNull(error);
        Assert.Contains("255", error);
        Assert.Contains("超出 1 字节", error);
    }

    [Fact]
    public void T_TF_10_BuildNewPath_FileNameOver255Bytes_ReturnsExplicitFailure()
    {
        // Linux 255 字节口径单命中：组件名用汉字拼超 255 UTF-8 字节（图×90 = 270 字节），
        // 全路径 UTF-16 字符数控制在 260 以内（Windows 口径不触发）——与 T_TF_05 双口径独立断言。
        using var temp = new TempDirectory();
        var oldPath = Path.Combine(temp.Path, new string('图', 90) + ".jpg");

        var result = _service.BuildNewPath(oldPath, new[] { "标" });

        Assert.False(result.Success);
        Assert.Null(result.NewFullPath);
        Assert.NotNull(result.Error);
        Assert.Contains("255", result.Error);
        Assert.DoesNotContain("260", result.Error);
    }

    [Fact]
    public void T_TF_11_Compose_AndBudgetComposeFileName_ShareSingleRule()
    {
        // 拼接规则唯一来源回归：服务实例 Compose 与预算纯函数 ComposeFileName 输出一致
        var tags = new[] { "风景", "已修" };
        Assert.Equal(
            _service.Compose("photo", ".jpg", tags),
            TagFilenameBudget.ComposeFileName("photo", ".jpg", tags));
    }

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
