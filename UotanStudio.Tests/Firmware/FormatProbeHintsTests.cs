using System.Buffers.Binary;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Magic;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class FormatProbeHintsTests
{
    public FormatProbeHintsTests()
    {
        FirmwareFormatRegistry.RegisterAll();
    }

    [Fact]
    public void TryGetProbeMatch_LpGeometryMagicHeader_SelectsLpHandler()
    {
        var header = BuildLpHeader(0x616c4467);

        Assert.True(FormatDetector.TryGetProbeMatch("super.img", header, out var handler));
        Assert.Equal("Android Logical Partition (super)", handler!.FormatName);
    }

    [Fact]
    public void MatchesProbeHints_LpCorruptedMagicHeader_ReturnsFalse()
    {
        var header = BuildLpHeader(0x616c4466);

        Assert.False(FormatDetector.MatchesProbeHints("super.img", header));
    }

    [Fact]
    public void MatchesProbeHints_LpZeroLengthHeader_ReturnsFalse()
    {
        Assert.False(FormatDetector.MatchesProbeHints("super.img", ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TryGetProbeMatch_GptSignatureHeader_SelectsGptHandler()
    {
        var header = BuildGptHeader("EFI PART"u8);

        Assert.True(FormatDetector.TryGetProbeMatch("disk.img", header, out var handler));
        Assert.Equal("GUID Partition Table (GPT)", handler!.FormatName);
    }

    [Fact]
    public void MatchesProbeHints_GptCorruptedSignatureHeader_ReturnsFalse()
    {
        var header = BuildGptHeader("EFI XRRT"u8);

        Assert.False(FormatDetector.MatchesProbeHints("disk.img", header));
    }

    [Fact]
    public void MatchesProbeHints_GptZeroLengthHeader_ReturnsFalse()
    {
        Assert.False(FormatDetector.MatchesProbeHints("disk.img", ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TryGetProbeMatch_UnrelatedHeader_ReturnsFalseWithNullHandler()
    {
        var header = new byte[4100];

        Assert.False(FormatDetector.TryGetProbeMatch("random.img", header, out var handler));
        Assert.Null(handler);
    }

    [Fact]
    public void MatchesProbeHints_Ext4SuperblockHeader_IsCoveredByCompiledMagicNotProbeHints()
    {
        var header = new byte[1082];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1080, 2), 0xEF53);

        Assert.False(FormatDetector.MatchesProbeHints("system.img", header));
        Assert.NotNull(FileMagic.Describe(header));
    }

    private static byte[] BuildLpHeader(uint geometryMagic)
    {
        var header = new byte[4100];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4096, 4), geometryMagic);
        return header;
    }

    private static byte[] BuildGptHeader(ReadOnlySpan<byte> signature)
    {
        var header = new byte[520];
        signature[..8].CopyTo(header.AsSpan(512, 8));
        return header;
    }
}