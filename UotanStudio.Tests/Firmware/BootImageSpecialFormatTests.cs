using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Editing;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.Core;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootImageSpecialFormatTests
{
    [Fact]
    public void StartupResourceMagic_Logo_IsClassifiedWithoutClaimingPayloadParsing()
        => AssertStartupResource("LOGO!!!!", ChromeOsTool.BootResourceType.Logo);

    [Fact]
    public void StartupResourceMagic_Splash_IsClassifiedWithoutClaimingPayloadParsing()
        => AssertStartupResource("SPLASH!!", ChromeOsTool.BootResourceType.Splash);

    [Fact]
    public void StartupResourceMagic_RockchipResource_IsClassifiedWithoutClaimingPayloadParsing()
        => AssertStartupResource("RSCE", ChromeOsTool.BootResourceType.RockchipResource);

    private static void AssertStartupResource(string magic, ChromeOsTool.BootResourceType expected)
    {
        var data = new byte[64];
        Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);

        Assert.Equal(expected, ChromeOsTool.DetectResource(data));
    }

    [Fact]
    public void StartupResourceTruncatedMagic_IsNotRecognized()
    {
        Assert.Equal(ChromeOsTool.BootResourceType.Unknown, ChromeOsTool.DetectResource("RS"u8.ToArray()));
    }

    [Fact]
    public void RockchipResource_ParsesVersionedDirectoryAndBoundedEntry()
    {
        var data = new byte[1540];
        "RSCE"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        "ENTR"u8.CopyTo(data.AsSpan(512));
        "logo.bin"u8.CopyTo(data.AsSpan(516));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(772), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(776), 4);
        data.AsSpan(1536, 4).Fill(0xA5);

        var parsed = ChromeOsTool.ParseRockchipResource(data);

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal((ushort)1, parsed.Version);
        Assert.Equal((ushort)2, parsed.FileTableVersion);
        Assert.Equal("logo.bin", entry.Name);
        Assert.Equal(1536, entry.Offset);
        Assert.Equal(4, entry.Size);
    }

    [Fact]
    public void RockchipResource_TruncatedDirectory_IsRejected()
    {
        var data = new byte[512];
        "RSCE"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseRockchipResource(data));
    }

    [Fact]
    public void RockchipResource_EntryBeyondContainer_IsRejected()
    {
        var data = new byte[1024];
        "RSCE"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        "ENTR"u8.CopyTo(data.AsSpan(512));
        "logo.bin"u8.CopyTo(data.AsSpan(516));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(772), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(776), 1);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseRockchipResource(data));
    }

    [Fact]
    public async Task RockchipResource_ReadsMetadataAndStreamsSelectedPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-rsce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = new byte[2051];
            "RSCE"u8.CopyTo(data);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 2);
            WriteRockchipEntry(data, 512, "logo.bin", 3, 5);
            WriteRockchipEntry(data, 1024, "splash.bin", 4, 3);
            new byte[] { 1, 2, 3, 4, 5 }.CopyTo(data.AsSpan(1536, 5));
            new byte[] { 6, 7, 8 }.CopyTo(data.AsSpan(2048, 3));
            var source = Path.Combine(root, "resource.img");
            await File.WriteAllBytesAsync(source, data, TestContext.Current.CancellationToken);

            var metadata = ChromeOsTool.ReadRockchipResourceMetadata(source);
            Assert.Equal(["logo.bin", "splash.bin"], metadata.Entries.Select(entry => entry.Name));

            var output = Path.Combine(root, "out");
            var count = ChromeOsTool.ExtractRockchipResource(
                source, output, new HashSet<string>(["splash.bin"], StringComparer.OrdinalIgnoreCase),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, count);
            Assert.False(File.Exists(Path.Combine(output, "logo.bin")));
            Assert.Equal([6, 7, 8], File.ReadAllBytes(Path.Combine(output, "splash.bin")));

            var handlerOutput = Path.Combine(root, "handler-out");
            var handlerResult = await new BootImageFormatHandler().UnpackAsync(
                source, handlerOutput,
                new UnpackOptions { SelectedEntries = ["logo.bin"] },
                TestContext.Current.CancellationToken);
            Assert.True(handlerResult.Success, handlerResult.Summary);
            Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(handlerOutput, "logo.bin")));
            Assert.False(File.Exists(Path.Combine(handlerOutput, "splash.bin")));

            var repacked = Path.Combine(root, "handler-repacked.img");
            var repackResult = await new BootImageFormatHandler().PackAsync(
                handlerOutput, repacked, TestContext.Current.CancellationToken);
            Assert.True(repackResult.Success, repackResult.Summary);
            Assert.Single(ChromeOsTool.ReadRockchipResourceMetadata(repacked).Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RockchipResource_BuildsCanonicalContainerThatExtracts()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-rsce-build-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "logo.bin"), [1, 2, 3],
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(source, "splash.bin"), [4, 5, 6, 7],
                TestContext.Current.CancellationToken);
            var package = Path.Combine(root, "resource.img");

            Assert.Equal(2, ChromeOsTool.BuildRockchipResource(
                source, package, TestContext.Current.CancellationToken));
            var output = Path.Combine(root, "out");
            Assert.Equal(2, ChromeOsTool.ExtractRockchipResource(
                package, output, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(output, "logo.bin")));
            Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(Path.Combine(output, "splash.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChromeOsShortMagic_IsRejectedAsStructurallyIncomplete()
    {
        var data = new byte[64];
        "CHROMEOS"u8.CopyTo(data);

        var result = ChromeOsTool.Detect(data);

        Assert.False(result.IsChromeOs);
        Assert.Equal(ChromeOsTool.ChromeOsImageType.Unknown, result.ImageType);
        Assert.Contains("keyblock", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChromeOsKeyblock_ReadsValidatedHeaderAndOpaqueRanges()
    {
        var data = CreateChromeOsVblock()[..192];

        var parsed = ChromeOsTool.ParseKeyblock(data);

        Assert.Equal((uint)2, parsed.HeaderVersionMajor);
        Assert.Equal((uint)1, parsed.HeaderVersionMinor);
        Assert.Equal((uint)192, parsed.KeyblockSize);
        Assert.Equal((uint)120, parsed.KeyblockSignature.Offset);
        Assert.Equal((uint)16, parsed.KeyblockSignature.Size);
        Assert.Equal((uint)32, parsed.DataKey.Offset);
        Assert.Equal((uint)32, parsed.DataKey.Size);
        Assert.Equal((uint)7, parsed.DataKey.Version);
    }

    [Fact]
    public void ChromeOsVblock_ReadsKeyblockAndKernelPreambleWithoutVerifying()
    {
        var data = CreateChromeOsVblock();

        var parsed = ChromeOsTool.ParseVblock(data);
        var result = ChromeOsTool.Detect(data);

        Assert.Equal((uint)192, parsed.PreambleOffset);
        Assert.Equal((uint)128, parsed.PreambleSize);
        Assert.Equal((uint)2, parsed.PreambleVersionMajor);
        Assert.Equal((uint)2, parsed.PreambleVersionMinor);
        Assert.Equal((uint)7, parsed.KernelVersion);
        Assert.Equal((uint)0x120, parsed.Flags);
        Assert.Equal(ChromeOsTool.ChromeOsImageType.Vblock, result.ImageType);
        Assert.Equal(320, result.StructureSize);
        Assert.Throws<NotSupportedException>(() => ChromeOsTool.VerifySignature(data));
    }

    [Fact]
    public void ChromeOsKernelWithBody_ReportsBodyBoundaryWithoutExtraction()
    {
        var vblock = CreateChromeOsVblock();
        var data = new byte[vblock.Length + 4096];
        vblock.CopyTo(data, 0);

        var result = ChromeOsTool.Detect(data);

        Assert.True(result.IsChromeOs);
        Assert.Equal(ChromeOsTool.ChromeOsImageType.Kernel, result.ImageType);
        Assert.Equal(320, result.StructureSize);
        Assert.Throws<NotSupportedException>(() => ChromeOsTool.ExtractKernel(data));
    }

    [Fact]
    public void ChromeOsKeyblock_DeclaredSizeBeyondInput_IsRejected()
    {
        var data = CreateChromeOsVblock()[..192];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseKeyblock(data));
        Assert.False(ChromeOsTool.Detect(data).IsChromeOs);
    }

    [Fact]
    public void ChromeOsKeyblock_DataKeyRangeBeyondContainer_IsRejected()
    {
        var data = CreateChromeOsVblock()[..192];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseKeyblock(data));
    }

    [Fact]
    public void ChromeOsVblock_PreambleSignatureRangeBeyondPreamble_IsRejected()
    {
        var data = CreateChromeOsVblock();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(192 + 8), 127);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseVblock(data));
        Assert.False(ChromeOsTool.Detect(data).IsChromeOs);
    }

    [Fact]
    public void ChromeOsVblock_UnsupportedPreambleVersion_IsRejected()
    {
        var data = CreateChromeOsVblock();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(192 + 36), 3);

        Assert.Throws<InvalidDataException>(() => ChromeOsTool.ParseVblock(data));
    }

    [Fact]
    public async Task BootImageHandler_ExposesValidatedChromeOsMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateChromeOsVblock(), TestContext.Current.CancellationToken);

            var result = await new BootImageFormatHandler().ReadMetadataAsync(
                path, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal("Vblock", result.Metadata["Type"]);
            Assert.Equal("2.1", result.Metadata["KeyblockVersion"]);
            Assert.Equal("192", result.Metadata["KeyblockSize"]);
            Assert.Equal("2.2", result.Metadata["PreambleVersion"]);
            Assert.Equal("192", result.Metadata["PreambleOffset"]);
            Assert.Equal("128", result.Metadata["PreambleSize"]);
            Assert.Equal("4096", result.Metadata["BodySize"]);
            Assert.Equal("False", result.Metadata["BodyPresent"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TegraBlob_IsParsedWithCheckedComponentRanges()
    {
        var data = new byte[320];
        data[0] = 0x4E;
        data[1] = 0x56;
        data[2] = 0x44;
        data[3] = 0x41;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 260);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 320);
        data[256] = 1;
        data[257] = 2;
        data[258] = 3;
        data[259] = 4;
        data[260] = 5;

        var parsed = TegraBlobTool.Parse(data);

        Assert.True(parsed.IsValid);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed.Kernel);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, parsed.Ramdisk);
    }

    [Fact]
    public void TegraBlob_RejectsOverflowedComponentRange()
    {
        var data = new byte[256];
        data[0] = 0x4E;
        data[1] = 0x56;
        data[2] = 0x44;
        data[3] = 0x41;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 256);

        var parsed = TegraBlobTool.Parse(data);

        Assert.False(parsed.IsValid);
        Assert.Contains("range", parsed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZImage_UsesExistingCompressionDetection()
    {
        var data = new byte[80];
        data[0x24] = 0x18;
        data[0x25] = 0x28;
        data[0x26] = 0x6F;
        data[0x27] = 0x01;
        data[0x40] = 0x1F;
        data[0x41] = 0x8B;

        var parsed = ZImageTool.Parse(data);

        Assert.True(parsed.IsValid);
        Assert.Equal(CompressionType.Gzip, parsed.CompressionType);
        Assert.Equal(0x40, parsed.CompressedOffset);
        Assert.Equal(16, parsed.CompressedSize);
    }

    [Fact]
    public void CpioEditor_ReusesArchiveEntriesForRamdiskMutation()
    {
        var archive = new CpioArchive();
        using var source = new MemoryStream();
        archive.SaveAll(source,
        [new ArchiveEntry
        {
            Name = "./init.rc",
            Data = "old"u8.ToArray(),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, UnixPermissions = 0x81A4, LinkCount = 1 }
        }], ArchiveFormat.NewAscii);
        source.Position = 0;

        var editor = new CpioArchiveEditor();
        editor.Load(source);
        Assert.True(editor.Rename("init.rc", "init.magisk.rc"));
        editor.AddOrReplace("overlay.rc", "overlay"u8.ToArray());
        Assert.True(editor.Exists("init.magisk.rc"));
        Assert.True(editor.Remove("overlay.rc"));
        Assert.False(editor.Exists("overlay.rc"));
    }

    private static byte[] CreateChromeOsVblock()
    {
        const int keyblockSize = 192;
        const int preambleSize = 128;
        var data = new byte[keyblockSize + preambleSize];
        "CHROMEOS"u8.CopyTo(data);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), keyblockSize);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 120);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 120);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), 112);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 112);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(88), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(96), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104), 7);

        var preamble = data.AsSpan(keyblockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble, preambleSize);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[8..], 116);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[16..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[24..], 116);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[32..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[36..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[40..], 7);
        BinaryPrimitives.WriteUInt64LittleEndian(preamble[48..], 0x0010_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(preamble[56..], 0x0020_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[64..], 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[72..], 44);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[80..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[88..], 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(preamble[96..], 0x0030_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[104..], 64);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble[112..], 0x120);

        return data;
    }

    private static void WriteRockchipEntry(byte[] data, int offset, string name, uint blockOffset, uint size)
    {
        "ENTR"u8.CopyTo(data.AsSpan(offset));
        Encoding.UTF8.GetBytes(name).CopyTo(data.AsSpan(offset + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 260), blockOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 264), size);
    }
}