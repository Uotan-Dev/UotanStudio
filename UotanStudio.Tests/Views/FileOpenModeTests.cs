using System.Text;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Views;

/// <summary>
/// File open-mode resolution tests driven by extension and optional header bytes.
/// <para>打开方式判定测试：依据扩展名与可选头部字节。</para>
/// </summary>
public class FileOpenModeTests
{
    [Theory]
    [InlineData("file.dts", EditorOpenMode.Text)]
    [InlineData("file.dtb", EditorOpenMode.Dtb)]
    [InlineData("file.dtbo", EditorOpenMode.Dtb)]
    [InlineData("file.dex", EditorOpenMode.Dex)]
    [InlineData("file.arsc", EditorOpenMode.Arsc)]
    [InlineData("file.apk", EditorOpenMode.Apk)]
    [InlineData("file.png", EditorOpenMode.Auto)]
    [InlineData("file", EditorOpenMode.Auto)]
    [InlineData("file.cs", EditorOpenMode.Text)]
    [InlineData("file.xml", EditorOpenMode.Text)]
    // 注册表驱动：固件格式处理器的典型扩展名 → 固件元数据面板。
    // Registry-driven: typical firmware-handler extensions open in the firmware metadata panel.
    [InlineData("file.img", EditorOpenMode.Firmware)]
    [InlineData("file.pac", EditorOpenMode.Firmware)]
    [InlineData("file.ops", EditorOpenMode.Firmware)]
    [InlineData("file.nb0", EditorOpenMode.Firmware)]
    [InlineData("file.ozip", EditorOpenMode.Firmware)]
    [InlineData("file.payload", EditorOpenMode.Firmware)]
    // 注册表未覆盖但仍属强二进制的残留扩展名 → 十六进制。
    // Residual strongly-binary extensions outside the registry keep the hex editor.
    [InlineData("file.dat", EditorOpenMode.Hex)]
    [InlineData("file.zip", EditorOpenMode.Hex)]
    [InlineData("file.elf", EditorOpenMode.Hex)]
    [InlineData("file.so", EditorOpenMode.Hex)]
    public void ResolveMode_ByExtension(string path, EditorOpenMode expected)
    {
        Assert.Equal(expected, FileOpenMode.ResolveMode(path));
    }

    [Fact]
    public void ResolveMode_WithElfHeader_ReturnsHexForUnknownExtension()
    {
        // 无扩展名 + ELF 头部 → magic 兜底判十六进制。
        // Unknown extension with an ELF header falls back to hex via the magic engine.
        var path = "kernel";
        var header = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00 };
        Assert.Equal(EditorOpenMode.Hex, FileOpenMode.ResolveMode(path, header));
    }

    [Fact]
    public void ResolveMode_WithPrintableHeader_StaysAuto()
    {
        // 未知扩展名但头部为可打印文本 → 仍是 Auto。
        // Unknown extension with a printable header stays Auto.
        var path = "readme";
        var header = Encoding.ASCII.GetBytes("plain text, not a magic signature at all");
        Assert.Equal(EditorOpenMode.Auto, FileOpenMode.ResolveMode(path, header));
    }

    [Fact]
    public void ResolveMode_NullHeader_IgnoresMagic()
    {
        // null 头部不触发 magic 兜底。
        // A null header skips the magic fallback.
        Assert.Equal(EditorOpenMode.Auto, FileOpenMode.ResolveMode("unknown", null));
    }

    // ---- 文件系统镜像（ext4/EROFS/F2FS）魔数路由 ----
    // Filesystem-image (ext4/EROFS/F2FS) magic routing.

    [Theory]
    [InlineData(1080, 0xEF53u, 2)]     // ext4：0xEF53（小端 ushort）@1080
    [InlineData(1024, 0xE0F5E1E2u, 4)] // erofs：0xE0F5E1E2（小端 uint）@1024
    [InlineData(1024, 0xF2F52010u, 4)] // f2fs：0xF2F52010（小端 uint）@1024
    public void ResolveMode_FilesystemMagicInImg_ReturnsFileSystem(int offset, uint magic, int width)
    {
        // 真实文件 + .img 扩展名 + 命中魔数 → 文件管理器编辑器（即使 .img 同时是固件扩展名）。
        // A real file with a .img extension and matching magic routes to the file manager editor
        // (even though .img is also a firmware extension).
        using var temp = new TempFile();
        File.WriteAllBytes(temp.Path, BuildHeader(offset, magic, width));

        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(temp.Path));
        Assert.True(FileOpenMode.IsFilesystemImageExtension(Path.GetExtension(temp.Path)));
    }

    [Fact]
    public void ResolveMode_ImgWithoutFilesystemMagic_FallsThroughToFirmware()
    {
        // .img 与固件格式重叠：内容未命中文件系统魔数时仍走固件面板。
        // The .img extension overlaps with firmware formats: without a filesystem magic the Firmware
        // panel still claims it.
        using var temp = new TempFile();
        File.WriteAllBytes(temp.Path, new byte[2048]); // 全零头部，无任何文件系统魔数

        Assert.Equal(EditorOpenMode.Firmware, FileOpenMode.ResolveMode(temp.Path));
    }

    [Fact]
    public void ResolveMode_HeaderWithExt4Magic_ReturnsFileSystem()
    {
        // 显式传入足够长的头部字节 → 魔数优先于扩展名路由。
        // A caller-supplied header long enough to hold the magic wins over extension routing.
        var path = "system.img";
        var header = BuildHeader(1080, 0xEF53, 2);
        Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(path, header));
    }

    [Fact]
    public void IsFilesystemMagic_ShortHeader_ReturnsFalse()
    {
        // 长度不足（小于 ext4 魔数所需 1082 字节）不误判。
        // A header too short for the ext4 magic (needs 1082 bytes) must not match.
        Assert.False(FileOpenMode.IsFilesystemMagic(new byte[64]));
        Assert.False(FileOpenMode.IsExt4Magic(new byte[1081]));
        Assert.False(FileOpenMode.IsErofsMagic(new byte[1027]));
        Assert.False(FileOpenMode.IsF2fsMagic(new byte[1027]));
    }

    /// <summary>
    /// <para>构造一个在指定偏移写入小端魔数的头部字节数组（长度 2048）。</para>
    /// Builds a header byte array (length 2048) with a little-endian magic written at the given offset.
    /// </summary>
    private static byte[] BuildHeader(int offset, uint magic, int width)
    {
        var buffer = new byte[2048];
        for (var i = 0; i < width; i++)
        {
            buffer[offset + i] = (byte)(magic >> (8 * i));
        }

        return buffer;
    }

    /// <summary>
    /// <para>测试用临时文件，释放时自动删除。</para>
    /// A temporary test file that deletes itself on disposal.
    /// </summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uotan-fom-" + Guid.NewGuid().ToString("N") + ".img");

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // 清理失败不影响测试结果。
                // Cleanup failures must not affect the test result.
            }
        }
    }
}