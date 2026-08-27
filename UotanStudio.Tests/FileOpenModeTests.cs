using System.Text;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests;

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
    [InlineData("file.png", EditorOpenMode.Auto)]
    [InlineData("file", EditorOpenMode.Auto)]
    [InlineData("file.cs", EditorOpenMode.Text)]
    [InlineData("file.img", EditorOpenMode.Hex)]
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
        Assert.Equal(EditorOpenMode.Auto, FileOpenMode.ResolveMode("unknown", (byte[]?)null));
    }
}