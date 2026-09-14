using UotanStudio.Firmware;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FileOpenModeTests
{
    /// <summary>
    /// Writes a temporary file and deletes it after the test finishes.
    /// <para>写入临时文件并在测试结束后删除。</para>
    /// </summary>
    private static string WriteTempFile(string fileName, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-tests-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ExtensionLessGzipRoutesToFirmwarePanel()
    {
        FirmwareFormatRegistry.RegisterAll();
        // GZIP 魔数（1F 8B 08）—— CompressFormatHandler 按内容命中。
        // GZIP magic (1F 8B 08) — matched by CompressFormatHandler on content.
        var gzip = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 };
        var path = WriteTempFile("sample", gzip);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Firmware, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtensionLessUnknownBinaryFallsBackToHex()
    {
        FirmwareFormatRegistry.RegisterAll();
        // 无任何格式命中且二进制特征明显（NUL 字节）时仍回退十六进制编辑器。
        // Files no handler claims with clear binary traits (NUL bytes) still fall back to the hex editor.
        var binary = new byte[256];
        for (var i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(i % 2);
        }
        var path = WriteTempFile("blob", binary);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Hex, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtensionLessPlainTextStaysAuto()
    {
        FirmwareFormatRegistry.RegisterAll();
        // 无扩展名纯文本不应被注册表嗅探或二进制启发式抢走。
        // Extension-less plain text must not be claimed by registry sniffing or binary heuristics.
        var text = "hello firmware world\nsecond line\n";
        var path = WriteTempFile("notes", System.Text.Encoding.UTF8.GetBytes(text));
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Auto, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtensionOwnedRoutesAreUnchanged()
    {
        FirmwareFormatRegistry.RegisterAll();
        // 有扩展名的文件路由不受注册表嗅探影响。
        // Extension-owned routes are unaffected by registry sniffing.
        Assert.Equal(EditorOpenMode.Text, FileOpenMode.ResolveMode("boot.txt"));
        Assert.Equal(EditorOpenMode.Firmware, FileOpenMode.ResolveMode("boot.img"));
        Assert.Equal(EditorOpenMode.Dtb, FileOpenMode.ResolveMode("boot.dtb"));
    }
}