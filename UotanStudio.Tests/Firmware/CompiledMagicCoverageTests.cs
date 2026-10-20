using System.Buffers.Binary;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class CompiledMagicCoverageTests
{
    public CompiledMagicCoverageTests()
    {
        FirmwareFormatRegistry.RegisterAll();
    }

    [Fact]
    public void DetectDetailed_SquashfsSuperblock_RoutesByCompiledStrongMagic()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("rootfs.img", BuildSquashfsSuperblock());

        var result = FormatDetector.DetectDetailed(path);

        Assert.NotNull(result);
        Assert.Equal("SquashFS", result!.Handler.FormatName);
        Assert.Equal(DetectionConfidence.StrongMagic, result.Confidence);
        Assert.Contains("Squashfs", result.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectDetailed_RomfsSuperblock_RoutesByCompiledStrongMagic()
    {
        using var temp = new TempDirectory();
        var image = new byte[256];
        "-rom1fs-"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), 64);
        var path = temp.WriteBytes("romfs.img", image);

        var result = FormatDetector.DetectDetailed(path);

        Assert.NotNull(result);
        Assert.Equal("ROMFS", result!.Handler.FormatName);
        Assert.Equal(DetectionConfidence.StrongMagic, result.Confidence);
        Assert.Contains("romfs", result.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_GptHeaderAtLba1_RoutesToHandlerThroughStructureOnly()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("disk.img", GptReaderTests.CreateDisk(512));

        var handler = FormatDetector.Detect(path);
        var detailed = FormatDetector.DetectDetailed(path);

        Assert.Equal("GUID Partition Table (GPT)", handler?.FormatName);
        Assert.NotNull(detailed);
        Assert.NotEqual(DetectionConfidence.StrongMagic, detailed!.Confidence);
    }

    [Fact]
    public void Detect_MeizuMpkMagic_RoutesToHandlerThroughStructureOnly()
    {
        using var temp = new TempDirectory();
        var archive = new byte[64];
        "!MPK"u8.CopyTo(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 3);
        var path = temp.WriteBytes("theme.mpk", archive);

        var handler = FormatDetector.Detect(path);
        var detailed = FormatDetector.DetectDetailed(path);

        Assert.Equal("Meizu MPK Package", handler?.FormatName);
        Assert.NotNull(detailed);
        Assert.NotEqual(DetectionConfidence.StrongMagic, detailed!.Confidence);
    }

    [Fact]
    public void Detect_AllwinnerImageWtyMagic_RoutesToHandlerThroughStructureOnly()
    {
        using var temp = new TempDirectory();
        var image = new byte[64];
        "IMAGEWTY"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0x100);
        var path = temp.WriteBytes("allwinner.img", image);

        var handler = FormatDetector.Detect(path);
        var detailed = FormatDetector.DetectDetailed(path);

        Assert.Equal("Allwinner IMAGEWTY Image", handler?.FormatName);
        Assert.NotNull(detailed);
        Assert.NotEqual(DetectionConfidence.StrongMagic, detailed!.Confidence);
    }

    private static byte[] BuildSquashfsSuperblock()
    {
        var image = new byte[512];
        "hsqs"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(22), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(28), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(40), 96);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(64), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72), 72);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(48), 80);
        return image;
    }
}