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
    }

    [Fact]
    public void T_TF_05_BuildNewPath_TooLongPath_ReturnsExplicitFailure()
    {
        // 构造新路径必然超过 260 字符的源路径；无需真实文件（长度校验先于任何存在性检查），且不抛异常
        var oldPath = @"C:\sv-tests\" + new string('长', 300) + ".jpg";

        var result = _service.BuildNewPath(oldPath, new[] { "标签" });

        Assert.False(result.Success);
        Assert.Null(result.NewFullPath);
        Assert.NotNull(result.Error);
        Assert.Contains("260", result.Error);
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
