using UotanStudio.Services.Firmware;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// <para>验证 <see cref="ImageInputService"/> 迁出的格式探测与源路径解析行为：
/// 扩展名优先、魔数判定、头部读取失败回退、未知输入返回 unknown、小文件头部截断语义，
/// 以及源路径解析的存在性校验。不初始化 UI、不触碰 ProjectContextService。</para>
/// Validates the format-probing and source-path-resolution behavior migrated to
/// <see cref="ImageInputService"/>: extension-first rules, magic matching, header-read fallback,
/// unknown inputs, small-file header truncation semantics, and existence validation for source
/// path resolution. No UI initialization and no ProjectContextService usage.
/// </summary>
public sealed class ImageInputServiceTests
{
    private static (ImageInputService Service, List<string> Warnings) CreateService(Func<string, string?>? resolve = null)
    {
        var warnings = new List<string>();
        var service = new ImageInputService(
            resolve ?? (path => File.Exists(path) || Directory.Exists(path) ? System.IO.Path.GetFullPath(path) : null),
            (_, message) => warnings.Add(message));
        return (service, warnings);
    }

    // ---- 格式探测：扩展名优先。 Format probing: extensions first. ----

    [Theory]
    [InlineData(".cpio", "cpio")]
    [InlineData(".ofp", "oppo")]
    [InlineData(".ops", "oppo")]
    [InlineData(".ozip", "oppo")]
    [InlineData(".simg", "sparse")]
    [InlineData(".sparse", "sparse")]
    [InlineData(".dat", "dat")]
    [InlineData(".new.dat.br", "brotli")]
    [InlineData(".BR", "brotli")]
    [InlineData(".transfer.list", "dat")]
    public void DetectFormat_KnownExtension_ReturnsExtensionFormat(string extension, string expected)
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("file" + extension, [1, 2, 3]);

        Assert.Equal(expected, ImageInputService.DetectFormat(file));
    }

    // ---- 格式探测：魔数判定。 Format probing: magic-based detection. ----

    [Theory]
    [InlineData("system.new.dat", "dat")]
    [InlineData("system.new.dat.br", "brotli")]
    public void DetectFormat_BlockDataSuffixTakesPrecedenceOverFilesystemHeader(string name, string expected)
    {
        using var temp = new TempDirectory();
        var bytes = new byte[4096]; bytes[1080] = 0x53; bytes[1081] = 0xef;
        Assert.Equal(expected, ImageInputService.DetectFormat(temp.WriteBytes(name, bytes)));
    }

    [Fact]
    public void DetectFormat_AndroidSparseMagic_ReturnsSparse()
    {
        using var temp = new TempDirectory();
        // Android sparse 魔数 0xED26FF3A（小端字节序 3A FF 26 ED）。
        // Android sparse magic 0xED26FF3A (little-endian bytes 3A FF 26 ED).
        var file = temp.WriteBytes("sparse.img", [0x3A, 0xFF, 0x26, 0xED, 1, 0]);

        Assert.Equal("sparse", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_PayloadMagic_ReturnsPayload()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("payload.bin", "CrAU"u8.ToArray());

        Assert.Equal("payload", ImageInputService.DetectFormat(file));
    }

    [Theory]
    [InlineData("ANDROID!")]
    [InlineData("VNDRBOOT")]
    public void DetectFormat_BootMagic_ReturnsBoot(string magic)
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("boot.img", System.Text.Encoding.ASCII.GetBytes(magic));

        Assert.Equal("boot", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_Ext4MagicAt1080_ReturnsExt4()
    {
        using var temp = new TempDirectory();
        var header = new byte[1082];
        header[1080] = 0x53; // 0xEF53 little-endian → 53 EF
        header[1081] = 0xEF;
        var file = temp.WriteBytes("system.raw", header);

        Assert.Equal("ext4", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_ErofsMagicAt1024_ReturnsErofs()
    {
        using var temp = new TempDirectory();
        var header = new byte[1028];
        // 0xE0F5E1E2 little-endian → E2 E1 F5 E0
        header[1024] = 0xE2;
        header[1025] = 0xE1;
        header[1026] = 0xF5;
        header[1027] = 0xE0;
        var file = temp.WriteBytes("erofs.img", header);

        Assert.Equal("erofs", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_F2fsMagicAt1024_ReturnsF2fs()
    {
        using var temp = new TempDirectory();
        var header = new byte[1028];
        // 0xF2F52010 little-endian → 10 20 F5 F2
        header[1024] = 0x10;
        header[1025] = 0x20;
        header[1026] = 0xF5;
        header[1027] = 0xF2;
        var file = temp.WriteBytes("f2fs.img", header);

        Assert.Equal("f2fs", ImageInputService.DetectFormat(file));
    }

    // ---- 格式探测：未知输入与回退。 Format probing: unknown inputs and fallbacks. ----

    [Fact]
    public void DetectFormat_UnrecognizedHeaderWithoutKnownExtension_ReturnsUnknown()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("mystery.unknown", [0x01, 0x02, 0x03, 0x04, 0x05]);

        Assert.Equal("unknown", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_ImgExtensionWithoutMagic_FallsBackToRaw()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("data.img", [0x01, 0x02, 0x03, 0x04]);

        Assert.Equal("raw", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_PayloadNamedBinWithoutMagic_FallsBackToPayload()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("firmware_payload.bin", [0x01, 0x02, 0x03, 0x04]);

        Assert.Equal("payload", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_ShortFile_TruncatedHeaderStillDetectsMagic()
    {
        using var temp = new TempDirectory();
        // 只有 2 字节的小文件：不足 1082 字节头部，但也不命中任何魔数；.img 扩展名回退 raw。
        // A 2-byte small file: below the 1082-byte header, matching no magic; the .img
        // extension falls back to raw.
        var file = temp.WriteBytes("tiny.img", [0x3A, 0xFF]);

        Assert.Equal("raw", ImageInputService.DetectFormat(file));
    }

    [Fact]
    public void DetectFormat_HeaderReadFailure_FallsBackToExtension()
    {
        using var temp = new TempDirectory();
        // 指向目录的“文件”路径使 ReadHeader 抛出 IOException → 回退扩展名推断 raw。
        // A "file" path pointing at a directory makes ReadHeader throw → falls back to "raw".
        var dir = System.IO.Path.Combine(temp.Path, "sub.img");
        Directory.CreateDirectory(dir);

        Assert.Equal("raw", ImageInputService.DetectFormat(dir));
    }

    // ---- 布局探测。 Layout probing. ----

    [Fact]
    public void ProbeFilesystemHeader_FsMagics_MapToLayout()
    {
        using var temp = new TempDirectory();
        var ext4 = temp.WriteBytes("a.raw", MakeHeaderWithUint16(1080, 0xEF53, 1082));
        var erofs = temp.WriteBytes("b.raw", MakeHeaderWithUint32(1024, 0xE0F5E1E2, 1028));
        var f2fs = temp.WriteBytes("c.raw", MakeHeaderWithUint32(1024, 0xF2F52010, 1028));
        var none = temp.WriteBytes("d.raw", [0x00, 0x01, 0x02, 0x03]);

        Assert.Equal(RawImageLayout.Ext4, ImageInputService.ProbeFilesystemHeader(ext4));
        Assert.Equal(RawImageLayout.Erofs, ImageInputService.ProbeFilesystemHeader(erofs));
        Assert.Equal(RawImageLayout.F2fs, ImageInputService.ProbeFilesystemHeader(f2fs));
        Assert.Null(ImageInputService.ProbeFilesystemHeader(none));
    }

    [Fact]
    public void ProbeFilesystemHeader_ShortFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("tiny.raw", [0x01, 0x02, 0x03]);

        Assert.Null(ImageInputService.ProbeFilesystemHeader(file));
    }

    // ---- 源路径解析。 Source path resolution. ----

    [Fact]
    public void ResolveSourcePath_ExistingFile_ReturnsAbsolutePath()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteBytes("boot.img", [1, 2, 3]);
        var (service, warnings) = CreateService(resolve: path => path);

        Assert.Equal(file, service.ResolveSourcePath(file, isDirectory: false, operation: "extract"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void ResolveSourcePath_UnresolvableRelativePath_ReturnsNullAndWarns()
    {
        var (service, warnings) = CreateService(resolve: _ => null);

        var result = service.ResolveSourcePath("missing.img", isDirectory: false, operation: "extract");

        Assert.Null(result);
        // 警告文案与原 LogService.Warn 输出保持一致（含中英双语，中止提示使用「导入/提取中止」措辞）。
        // The warning text matches the previous LogService.Warn output (bilingual; the abort text
        // keeps the existing wording).
        var warning = Assert.Single(warnings);
        Assert.Contains("extract aborted: path not found: missing.img", warning);
        Assert.Contains("中止：找不到路径：missing.img", warning);
    }

    [Fact]
    public void ResolveSourcePath_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var missing = System.IO.Path.Combine(temp.Path, "gone.img");
        var (service, warnings) = CreateService(resolve: path => path);

        Assert.Null(service.ResolveSourcePath(missing, isDirectory: false, operation: "extract"));
        var warning = Assert.Single(warnings);
        Assert.Contains("extract aborted: not found: ", warning);
    }

    [Fact]
    public void ResolveSourcePath_DirectoryEntryChecksDirectoryExistence()
    {
        using var temp = new TempDirectory();
        var dir = System.IO.Path.Combine(temp.Path, "system");
        Directory.CreateDirectory(dir);
        var (service, warnings) = CreateService(resolve: path => path);

        Assert.Equal(dir, service.ResolveSourcePath(dir, isDirectory: true, operation: "img"));
        Assert.Empty(warnings);

        // 文件路径按目录条目校验 → 不存在 → null。
        // A file path validated as a directory entry → missing → null.
        var file = temp.WriteBytes("boot.img", [1]);
        Assert.Null(service.ResolveSourcePath(file, isDirectory: true, operation: "img"));
        Assert.Single(warnings);
    }

    private static byte[] MakeHeaderWithUint16(int offset, ushort value, int size)
    {
        var header = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset, 2), value);
        return header;
    }

    private static byte[] MakeHeaderWithUint32(int offset, uint value, int size)
    {
        var header = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset, 4), value);
        return header;
    }
}