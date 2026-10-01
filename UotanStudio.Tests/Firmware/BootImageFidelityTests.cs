using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Boot image V3/V4 packing-fidelity tests: page-size inference for 16KB-aligned layouts, opaque reserved and
/// padding preservation, and byte-identical unpack → repack output for unmodified images.
/// <para>Boot 镜像 V3/V4 打包保真测试：16KB 对齐布局的页大小推断、不透明 reserved 与填充区域保留，
/// 以及未修改镜像解包 → 重打包的逐字节一致输出。</para>
/// </summary>
public class BootImageFidelityTests
{
    private static readonly byte[] TestKernel = BuildBytes(2048, factor: 7);
    private static readonly byte[] TestRamdisk = BuildBytes(1024, factor: 3);
    private static readonly byte[] TestSignature = BuildBytes(256, factor: 13);

    private static byte[] PackToArray(BootImagePacker packer)
    {
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    private static BootImageHeader V3Header(uint pageSize) => new()
    {
        HeaderVersion = BootImageVersion.V3,
        HeaderSize = 1580,
        PageSize = pageSize,
        OsVersion = 0x01030000,
        CommandLine = "androidboot.slot_suffix=_a",
    };

    private static BootImageHeader V4Header(uint pageSize) => new()
    {
        HeaderVersion = BootImageVersion.V4,
        HeaderSize = 1584,
        PageSize = pageSize,
        OsVersion = 0x01040000,
        CommandLine = "androidboot.verifiedbootstate=green",
        SignatureSize = (uint)TestSignature.Length,
    };

    private static void WriteReservedJunk(byte[] image)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 0xDEADBEEFu);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), 0xFEEDFACEu);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), 0x0BADC0DEu);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), 0xCAFEBABEu);
    }

    private static void FillOpaqueTail(byte[] image, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            image[offset + i] = (byte)(0xA0 + (i & 0x1F));
        }
    }

    [Theory]
    [InlineData(1580u, 2048u, 1024u, 0u, 12288L, 4096u)]
    [InlineData(1580u, 2048u, 1024u, 0u, 6144L, 2048u)]
    [InlineData(1580u, 8192u, 4096u, 0u, 24576L, 8192u)]
    [InlineData(1584u, 2048u, 1024u, 256u, 65536L, 16384u)]
    [InlineData(1584u, 16384u, 8192u, 0u, 49152L, 16384u)]
    public void DetectV3V4PageSize_LayoutEqualsImageLength_SelectsMatchingCandidate(
        uint headerSize, uint kernelSize, uint ramdiskSize, uint signatureSize, long imageLength, uint expected)
    {
        Assert.Equal(expected, BootImageParser.DetectV3V4PageSize(headerSize, kernelSize, ramdiskSize, signatureSize, imageLength));
    }

    [Theory]
    [InlineData(1580u, 2048u, 1024u, 0u, 12289L)]
    [InlineData(1580u, 8192u, 4096u, 0u, 20481L)]
    [InlineData(20000u, 2048u, 1024u, 0u, 40960L)]
    public void DetectV3V4PageSize_NoAlignmentEvidence_DefaultsToFourKb(
        uint headerSize, uint kernelSize, uint ramdiskSize, uint signatureSize, long imageLength)
    {
        Assert.Equal(4096u, BootImageParser.DetectV3V4PageSize(headerSize, kernelSize, ramdiskSize, signatureSize, imageLength));
    }

    [Fact]
    public void V4_PaddedFullPartitionImage_Lz4LegacyRamdisk_PageSizeRefinedToFourKb()
    {
        var plaintext = "070701000000000000000000000000000000000000000000000000000000TRAILER!!!"u8.ToArray();
        var ramdisk = BootCompressor.Compress(plaintext, CompressionType.Lz4Legacy);
        Assert.True(ramdisk.Length >= 8);
        Assert.Equal(0x02, ramdisk[0]);
        Assert.Equal(0x21, ramdisk[1]);

        const int pageSize = 4096;
        const uint headerSize = 1584;
        const int imageLength = 8 * 1024 * 1024;
        var image = new byte[imageLength];
        "ANDROID!"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), (uint)ramdisk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), 0x01040000u);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20, 4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40, 4), 4u);
        ramdisk.CopyTo(image, pageSize);

        Assert.Equal(16384u, BootImageParser.DetectV3V4PageSize(headerSize, 0u, (uint)ramdisk.Length, 0u, imageLength));

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(4096u, parser.Header.PageSize);
        Assert.Equal(CompressionType.Lz4Legacy, parser.Header.RamdiskCompression);
        Assert.Equal(plaintext, BootCompressor.TryDecompress(parser.Ramdisk, parser.Header.RamdiskCompression));
    }

    [Fact]
    public void V3_OpaqueReservedAndPaddingBytes_UnpackRepackIsByteIdentical()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V3Header(4096),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        WriteReservedJunk(image);
        FillOpaqueTail(image, 1600, 64);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.NotNull(parser.Header.HeaderBlob);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(image, BootImagePacker.Repack(parser));
    }

    [Fact]
    public void V3_SixteenKbAlignment_PageSizeDetectedAndRepackIsByteIdentical()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V3Header(16384),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        Assert.Equal(16384 * 3, image.Length);
        WriteReservedJunk(image);
        FillOpaqueTail(image, 1600, 64);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(16384u, parser.Header.PageSize);
        Assert.Equal(16384, parser.Header.HeaderBlob!.Length);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(image, BootImagePacker.Repack(parser));
    }

    [Fact]
    public void V4_SixteenKbAlignment_OpaqueTailPreservedAndSignatureRoundTrips()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V4Header(16384),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            Signature = TestSignature,
        });
        WriteReservedJunk(image);
        FillOpaqueTail(image, 1584, 2048);

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(16384u, parser.Header.PageSize);
        Assert.Equal(TestSignature, parser.Signature);
        Assert.Equal(TestKernel, parser.Kernel);
        Assert.Equal(TestRamdisk, parser.Ramdisk);
        Assert.Equal(image, BootImagePacker.Repack(parser));
    }

    [Fact]
    public void V4_DeclaredHeaderSizeAboveCanonical_IsPreservedByteIdentical()
    {
        var header = V4Header(4096);
        header.HeaderSize = 2048;
        var image = PackToArray(new BootImagePacker
        {
            Header = header,
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
            Signature = TestSignature,
        });

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Equal(2048u, parser.Header.HeaderSize);
        Assert.Equal(4096u, parser.Header.PageSize);
        Assert.Equal(image, BootImagePacker.Repack(parser));
    }

    [Fact]
    public void V2_Parse_LeavesHeaderBlobNull()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V2,
                PageSize = 2048,
                KernelAddr = 0x00008000,
                CommandLine = "console=ttyS0",
            },
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });

        using var parser = new BootImageParser();
        parser.Parse(image);

        Assert.Null(parser.Header.HeaderBlob);
    }

    [Fact]
    public void Repack_WithReplacedKernel_KeepsOpaqueHeaderFieldsAndUpdatesSizes()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V3Header(4096),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        WriteReservedJunk(image);

        using var parser = new BootImageParser();
        parser.Parse(image);
        var replacement = BuildBytes(3000, factor: 41);
        var repacked = BootImagePacker.Repack(parser, kernel: replacement);

        using var parser2 = new BootImageParser();
        parser2.Parse(repacked);

        Assert.Equal(replacement, parser2.Kernel);
        Assert.Equal((uint)replacement.Length, parser2.Header.KernelSize);
        Assert.Equal(TestRamdisk, parser2.Ramdisk);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(repacked.AsSpan(24)));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32LittleEndian(repacked.AsSpan(36)));
    }

    [Fact]
    public void Packer_MismatchedHeaderBlobLength_FallsBackToSyntheticHeader()
    {
        var corruptedHeader = V3Header(4096);
        corruptedHeader.HeaderBlob = new byte[3000];
        var packedWithBadBlob = PackToArray(new BootImagePacker
        {
            Header = corruptedHeader,
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        var synthetic = PackToArray(new BootImagePacker
        {
            Header = V3Header(4096),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packedWithBadBlob.AsSpan(24)));
        Assert.Equal(synthetic, packedWithBadBlob);
    }

    [Fact]
    public async Task PackToStreamAsync_UnmodifiedV3Image_MatchesSynchronousBytes()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V3Header(16384),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        WriteReservedJunk(image);

        using var parser = new BootImageParser();
        parser.Parse(image);
        var packer = BootImagePacker.FromParser(parser);
        using var ms = new MemoryStream();
        await packer.PackToStreamAsync(ms, TestContext.Current.CancellationToken);

        Assert.Equal(image, ms.ToArray());
    }

    [Fact]
    public async Task ParseFile_SixteenKbV3Image_DetectsPageSizeAndRepacksByteIdentical()
    {
        var image = PackToArray(new BootImagePacker
        {
            Header = V3Header(16384),
            Kernel = TestKernel,
            Ramdisk = TestRamdisk,
        });
        WriteReservedJunk(image);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-boot-fidelity-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, image, TestContext.Current.CancellationToken);

            using var parser = new BootImageParser();
            parser.ParseFile(path);

            Assert.Equal(16384u, parser.Header.PageSize);
            Assert.Equal(16384, parser.Header.HeaderBlob!.Length);
            Assert.Equal(TestKernel, parser.Kernel);
            Assert.Equal(TestRamdisk, parser.Ramdisk);
            Assert.Equal(image, BootImagePacker.Repack(parser));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor) & 0xFF);
        }

        return data;
    }
}