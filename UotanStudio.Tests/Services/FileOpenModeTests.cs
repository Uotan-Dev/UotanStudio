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

    [Fact]
    public void RenamedDtbMagicRoutesToDtbEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        // FDT 魔数（大端 0xD00DFEED）优先于扩展名：改名 .bin 的 DTB 仍走设备树编辑器。
        // The FDT magic (big-endian 0xD00DFEED) wins over the extension: a DTB renamed .bin
        // still opens in the device-tree editor.
        var dtb = new byte[] { 0xD0, 0x0D, 0xFE, 0xED, 0x00, 0x00, 0x00, 0x28 };
        var path = WriteTempFile("renamed.bin", dtb);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dtb, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RenamedDtboMagicRoutesToDtbEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        // DTBO 魔数（大端 0xD7B7AB1E）按内容命中设备树编辑器。
        // The DTBO magic (big-endian 0xD7B7AB1E) hits the device-tree editor by content.
        var dtbo = new byte[] { 0xD7, 0xB7, 0xAB, 0x1E, 0x00, 0x00, 0x00, 0x20 };
        var path = WriteTempFile("table", dtbo);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dtb, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtensionLessDexMagicRoutesToDexEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        // "dex\n035" 魔数：无扩展名的 DEX 按内容走 DEX 编辑器。
        // The "dex\n035" magic: an extension-less DEX opens in the DEX editor by content.
        var dex = new byte[] { (byte)'d', (byte)'e', (byte)'x', (byte)'\n', (byte)'0', (byte)'3', (byte)'5', 0, 0, 0 };
        var path = WriteTempFile("classes", dex);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dex, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("renamed.bin", "ANDROID!")]
    [InlineData("renamed.img", "VNDRBOOT")]
    [InlineData("renamed.bin", "CrAU")]
    [InlineData("renamed.img", "AVBf")]
    [InlineData("renamed.bin", "RKFW")]
    [InlineData("renamed.img", "RKAF")]
    public void StrongFirmwareMagicIgnoresExtension(string fileName, string magic)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(magic);
        var path = WriteTempFile(fileName, bytes);
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
    public void OrdinaryBinDoesNotBecomePayloadFromExtension()
    {
        FirmwareFormatRegistry.RegisterAll();
        var path = WriteTempFile("ordinary.bin", [0x01, 0x02, 0x03, 0x04]);
        try
        {
            Assert.False(new UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadFormatHandler().CanHandle(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtensionLessZipMagicRoutesToFileManager()
    {
        FirmwareFormatRegistry.RegisterAll();
        // PK 本地文件头魔数：无扩展名 ZIP 按内容走文件管理器浏览。
        // The PK local-file-header magic: an extension-less ZIP opens in the file manager by content.
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        var path = WriteTempFile("package", zip);
        try
        {
            var header = FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
            Assert.Equal(EditorOpenMode.FileSystem, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApkAndJarExtensionsKeepDedicatedEditorsOverZipMagic()
    {
        FirmwareFormatRegistry.RegisterAll();
        // ZIP 魔数无法区分 APK/JAR：两者扩展名路由仍优先于内容命中。
        // The ZIP magic cannot tell an APK/JAR: their extension routes still win over content.
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        Assert.Equal(EditorOpenMode.Apk, FileOpenMode.ResolveMode("app.apk", zip));
        Assert.Equal(EditorOpenMode.Dex, FileOpenMode.ResolveMode("lib.jar", zip));
    }
}
