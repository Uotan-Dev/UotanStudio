using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
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
    public void ExtensionLessFatAndExFatVolumesRouteToFileSystemBrowser()
    {
        FirmwareFormatRegistry.RegisterAll();
        var boot = new byte[512];
        "EXFAT   "u8.CopyTo(boot.AsSpan(3));
        boot[0x6C] = 9;
        boot[0x6D] = 3;
        boot[0x6E] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x48), 16_384);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x50), 2_048);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x54), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x58), 4_096);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x5C), 1_536);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x60), 5);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        Assert.True(FileOpenMode.IsExFatMagic(boot));
        Assert.True(FileOpenMode.IsFatFamilyMagic(boot));
        Assert.True(FileOpenMode.IsFilesystemMagic(boot));
        Assert.False(FileOpenMode.IsFatMagic(boot));

        var volume = KernelExFatVolume();
        var exFatPath = WriteTempFile("exfat-volume", volume);
        var fat = new UotanStudio.Tests.Firmware.Fat.FatImageBuilder(12)
            .AddFile("NOTE.TXT", [1, 2, 3])
            .WriteTo(Path.Combine(Path.GetTempPath(), "uotan-fat-mode-" + Guid.NewGuid().ToString("N") + ".img"));
        try
        {
            Assert.Equal(EditorOpenMode.FileSystem,
                FileOpenMode.ResolveMode(exFatPath, FormatDetector.ReadHeader(exFatPath, FileOpenMode.ContentProbeLength)));
            Assert.True(FileOpenMode.IsFatMagic(FormatDetector.ReadHeader(fat, 512)));
            Assert.False(FileOpenMode.IsExFatMagic(FormatDetector.ReadHeader(fat, 512)));
        }
        finally
        {
            File.Delete(exFatPath);
            File.Delete(fat);
        }
    }

    private static byte[] KernelExFatVolume()
    {
        var asset = Path.Combine(AppContext.BaseDirectory, "TestData", "Fat", "exfat-kernel-written.deflate");
        using var compressed = File.OpenRead(asset);
        using var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void ExtensionLessUnknownBinaryFallsBackToHex()
    {
        FirmwareFormatRegistry.RegisterAll();
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
        Assert.Equal(EditorOpenMode.Text, FileOpenMode.ResolveMode("boot.txt"));
        Assert.Equal(EditorOpenMode.Firmware, FileOpenMode.ResolveMode("boot.img"));
        Assert.Equal(EditorOpenMode.Dtb, FileOpenMode.ResolveMode("boot.dtb"));
    }

    [Fact]
    public void RenamedDtbMagicRoutesToDtbEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
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
            Assert.False(new UotanStudio.Firmware.OtaDelta.Payload.PayloadFormatHandler().CanHandle(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fat12BootSectorRoutesToFileManager()
    {
        var image = new byte[512];
        image[0] = 0xEB;
        image[1] = 0x3C;
        image[2] = 0x90;
        System.Text.Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(image, 3);
        image[11] = 0x00;
        image[12] = 0x02;
        image[13] = 1;
        image[14] = 1;
        image[16] = 1;
        image[17] = 0x00;
        image[18] = 0x02;
        image[22] = 1;
        System.Text.Encoding.ASCII.GetBytes("FAT12   ").CopyTo(image, 54);
        image[510] = 0x55;
        image[511] = 0xAA;
        var path = WriteTempFile("renamed.bin", image);
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
    public void ExtensionLessZipMagicRoutesToFileManager()
    {
        FirmwareFormatRegistry.RegisterAll();
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
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        Assert.Equal(EditorOpenMode.Apk, FileOpenMode.ResolveMode("app.apk", zip));
        Assert.Equal(EditorOpenMode.Dex, FileOpenMode.ResolveMode("lib.jar", zip));
    }
}