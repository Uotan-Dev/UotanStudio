using System.Buffers.Binary;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.Core;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DocumentFormatPolicyTests
{
    private static string WriteTempFile(string fileName, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-tests-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ResolveMode_ExtensionlessGzip_RoutesToFirmwarePanel()
    {
        FirmwareFormatRegistry.RegisterAll();
        var gzip = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 };
        var path = WriteTempFile("sample", gzip);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Firmware, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionlessFatAndExFatVolumes_RouteToFileSystemBrowser()
    {
        FirmwareFormatRegistry.RegisterAll();
        var boot = new byte[512];
        "EXFAT   "u8.CopyTo(boot.AsSpan(3));
        boot[0x6C] = 9;
        boot[0x6D] = 3;
        boot[0x6E] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x48), 16_384);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x50), 2_048);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x54), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x58), 4_096);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x5C), 1_536);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(0x60), 5);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        Assert.True(DocumentFormatPolicy.IsExFatMagic(boot));
        Assert.True(DocumentFormatPolicy.IsFatFamilyMagic(boot));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(boot));
        Assert.False(DocumentFormatPolicy.IsFatMagic(boot));

        var volume = KernelExFatVolume();
        var exFatPath = WriteTempFile("exfat-volume", volume);
        var fat = new Firmware.Fat.FatImageBuilder(12)
            .AddFile("NOTE.TXT", [1, 2, 3])
            .WriteTo(Path.Combine(Path.GetTempPath(), "uotan-fat-mode-" + Guid.NewGuid().ToString("N") + ".img"));
        try
        {
            Assert.Equal(EditorOpenMode.FileSystem,
                DocumentFormatPolicy.ResolveMode(exFatPath, FormatDetector.ReadHeader(exFatPath, DocumentFormatPolicy.ContentProbeLength)));
            Assert.True(DocumentFormatPolicy.IsFatMagic(FormatDetector.ReadHeader(fat, 512)));
            Assert.False(DocumentFormatPolicy.IsExFatMagic(FormatDetector.ReadHeader(fat, 512)));
        }
        finally
        {
            File.Delete(exFatPath);
            File.Delete(fat);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionlessUnknownBinary_FallsBackToHex()
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
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Hex, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionlessPlainText_StaysAuto()
    {
        FirmwareFormatRegistry.RegisterAll();
        var text = "hello firmware world\nsecond line\n";
        var path = WriteTempFile("notes", System.Text.Encoding.UTF8.GetBytes(text));
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Auto, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionOwnedRoutes_AreUnchanged()
    {
        FirmwareFormatRegistry.RegisterAll();
        Assert.Equal(EditorOpenMode.Text, DocumentFormatPolicy.ResolveMode("boot.txt"));
        Assert.Equal(EditorOpenMode.Firmware, DocumentFormatPolicy.ResolveMode("boot.img"));
        Assert.Equal(EditorOpenMode.Dtb, DocumentFormatPolicy.ResolveMode("boot.dtb"));
    }

    [Fact]
    public void ResolveMode_RenamedDtbMagic_RoutesToDtbEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        var dtb = new byte[] { 0xD0, 0x0D, 0xFE, 0xED, 0x00, 0x00, 0x00, 0x28 };
        var path = WriteTempFile("renamed.bin", dtb);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dtb, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_RenamedDtboMagic_RoutesToDtbEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        var dtbo = new byte[] { 0xD7, 0xB7, 0xAB, 0x1E, 0x00, 0x00, 0x00, 0x20 };
        var path = WriteTempFile("table", dtbo);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dtb, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionlessDexMagic_RoutesToDexEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        var dex = new byte[] { (byte)'d', (byte)'e', (byte)'x', (byte)'\n', (byte)'0', (byte)'3', (byte)'5', 0, 0, 0 };
        var path = WriteTempFile("classes", dex);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.Dex, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("renamed.bin", "ANDROID!", EditorOpenMode.Firmware)]
    [InlineData("renamed.img", "VNDRBOOT", EditorOpenMode.Firmware)]
    [InlineData("renamed.bin", "CrAU", EditorOpenMode.Firmware)]
    [InlineData("renamed.img", "AVBf", EditorOpenMode.Firmware)]
    [InlineData("renamed.bin", "RKFW", EditorOpenMode.Firmware)]
    [InlineData("renamed.img", "RKAF", EditorOpenMode.Firmware)]
    public void ResolveMode_StrongFirmwareMagicIgnoringExtension_KeepsRecordedMode(
        string fileName, string magic, EditorOpenMode expected)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(magic);
        var path = WriteTempFile(fileName, bytes);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(expected, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_OrdinaryBin_DoesNotBecomePayloadFromExtension()
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
    public void ResolveMode_Fat12BootSector_RoutesToFileManager()
    {
        var image = Fat12BootSector();
        var path = WriteTempFile("renamed.bin", image);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.FileSystem, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ExtensionlessZipMagic_RoutesToFileManager()
    {
        FirmwareFormatRegistry.RegisterAll();
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        var path = WriteTempFile("package", zip);
        try
        {
            var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);
            Assert.Equal(EditorOpenMode.FileSystem, DocumentFormatPolicy.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveMode_ApkAndJarExtensions_KeepDedicatedEditorsOverZipMagic()
    {
        FirmwareFormatRegistry.RegisterAll();
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 };
        Assert.Equal(EditorOpenMode.Apk, DocumentFormatPolicy.ResolveMode("app.apk", zip));
        Assert.Equal(EditorOpenMode.Dex, DocumentFormatPolicy.ResolveMode("lib.jar", zip));
    }

    [Fact]
    public void ResolveMode_QcnExtension_RoutesToQcnEditor()
    {
        Assert.Equal(EditorOpenMode.Qcn, DocumentFormatPolicy.ResolveMode("backup.qcn"));
        Assert.Equal(EditorOpenMode.Qcn, DocumentFormatPolicy.ResolveMode("BACKUP.QCN"));
        Assert.True(DocumentFormatPolicy.IsQcnExtension(".qcn"));
        Assert.False(DocumentFormatPolicy.IsQcnExtension(".doc"));
    }

    [Fact]
    public void ResolveMode_Ole2MagicAlone_DoesNotRouteToQcnEditor()
    {
        var ole2 = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        Assert.NotEqual(EditorOpenMode.Qcn, DocumentFormatPolicy.ResolveMode("document.doc", ole2));
        Assert.NotEqual(EditorOpenMode.Qcn, DocumentFormatPolicy.ResolveMode("installer", ole2));
    }

    [Fact]
    public void IsFilesystemMagic_HandlerOwnedCorpus_MatchesEachFormat()
    {
        var ext4 = Ext4Superblock();
        var erofs = new byte[1028];
        BinaryPrimitives.WriteUInt32LittleEndian(erofs.AsSpan(1024, 4), 0xE0F5E1E2);
        var f2fs = new byte[1028];
        BinaryPrimitives.WriteUInt32LittleEndian(f2fs.AsSpan(1024, 4), 0xF2F52010);
        var fat = Fat12BootSector();
        var exFat = new byte[512];
        "EXFAT   "u8.CopyTo(exFat.AsSpan(3));
        exFat[510] = 0x55;
        exFat[511] = 0xAA;

        Assert.True(DocumentFormatPolicy.IsExt4Magic(ext4));
        Assert.True(DocumentFormatPolicy.IsErofsMagic(erofs));
        Assert.True(DocumentFormatPolicy.IsF2fsMagic(f2fs));
        Assert.True(DocumentFormatPolicy.IsFatMagic(fat));
        Assert.True(DocumentFormatPolicy.IsExFatMagic(exFat));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(ext4));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(erofs));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(f2fs));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(fat));
        Assert.True(DocumentFormatPolicy.IsFilesystemMagic(exFat));
        Assert.False(DocumentFormatPolicy.IsExt4Magic(erofs));
        Assert.False(DocumentFormatPolicy.IsF2fsMagic(erofs));
        Assert.False(DocumentFormatPolicy.IsFatMagic(exFat));
        Assert.False(DocumentFormatPolicy.IsExFatMagic(fat));
    }

    [Fact]
    public void ResolveMode_PartitionTableCorpus_StaysOnPartitionEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        var gpt = new byte[520];
        "EFI PART"u8.CopyTo(gpt.AsSpan(512));
        gpt[510] = 0x55;
        gpt[511] = 0xAA;
        var lp = new byte[4100];
        BinaryPrimitives.WriteUInt32LittleEndian(lp.AsSpan(4096, 4), 0x616C4467);

        Assert.True(DocumentFormatPolicy.IsGptMagic(gpt));
        Assert.True(DocumentFormatPolicy.IsLpMetadataMagic(lp));
        Assert.Equal(EditorOpenMode.Partition, DocumentFormatPolicy.ResolveMode("disk.img", gpt));
        Assert.Equal(EditorOpenMode.Partition, DocumentFormatPolicy.ResolveMode("super.img", lp));
    }

    [Fact]
    public void IsGptImage_TableOnlyImageWithHeaderPastSixtyFourKiB_RoutesToPartitionEditor()
    {
        FirmwareFormatRegistry.RegisterAll();
        var entries = new List<GptFixtureEntry>
        {
            new(GptLayout.DefaultTypeGuid, Guid.NewGuid(), 34, 259, "bl2_a"),
            new(GptLayout.DefaultTypeGuid, Guid.NewGuid(), 260, 835, "teeos_a")
        };
        var image = GptFixtureImage.BuildAtOffset(0x2_8800, 0x2_0000, Guid.NewGuid(), entries, 128, 0);
        var path = WriteTempFile("ptable.img", image);
        try
        {
            Assert.True(DocumentFormatPolicy.IsGptImage(path));
            Assert.Equal(EditorOpenMode.Partition,
                DocumentFormatPolicy.ResolveMode(path,
                    FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Fat12BootSector()
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
        return image;
    }

    private static byte[] Ext4Superblock()
    {
        var header = new byte[2048];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1024, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1028, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1048, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1080, 2), 0xEF53);
        return header;
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
}