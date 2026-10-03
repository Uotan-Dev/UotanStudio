using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Program sector-count resolution tests using literal byte and sector values: the manifest's
/// declared count is a capacity ceiling, a shorter source is programmed without implicit
/// whole-range padding, and only the final sector may be zero-filled.
/// <para>编程扇区数解析测试，全部使用字面字节与扇区数值：清单声明值是容量上限，较短源文件不会被隐式补齐到整个范围，
/// 且只有最后一个扇区允许零填充。</para>
/// </summary>
public class FlashProgramLengthTests
{
    [Theory]
    [InlineData(512L, 0L, 512L, 1L)]
    [InlineData(513L, 0L, 512L, 2L)]
    [InlineData(768L, 262144L, 512L, 2L)]
    [InlineData(4096L, 262144L, 4096L, 1L)]
    [InlineData(1024L, 1L, 512L, 1L)]
    [InlineData(1536L, 3L, 512L, 3L)]
    [InlineData(0L, 4L, 512L, 0L)]
    public void ResolveSectors_DeclaredCapacityAndSourceLength_ProducesProgrammedSectorCount(
        long availableBytes, long declaredSectors, long sectorSize, long expected)
    {
        Assert.Equal(expected, FlashProgramLength.ResolveSectors(availableBytes, declaredSectors, sectorSize));
    }

    [Theory]
    [InlineData(1024L, 1L, 512L, true)]
    [InlineData(1025L, 2L, 512L, true)]
    [InlineData(768L, 262144L, 512L, false)]
    [InlineData(1536L, 3L, 512L, false)]
    [InlineData(1024L, 0L, 512L, false)]
    public void IsTruncatedByDeclaration_SourceLongerThanDeclaredRange_ReportsTailLoss(
        long availableBytes, long declaredSectors, long sectorSize, bool expected)
    {
        Assert.Equal(expected, FlashProgramLength.IsTruncatedByDeclaration(availableBytes, declaredSectors, sectorSize));
    }

    [Theory]
    [InlineData(768L, 2L, 512L, 256L)]
    [InlineData(1L, 1L, 512L, 511L)]
    [InlineData(1024L, 2L, 512L, 0L)]
    [InlineData(4096L, 1L, 512L, 0L)]
    public void ResolveTailPadding_PartiallyFilledLastSector_ReportsZeroFillCount(
        long availableBytes, long numSectors, long sectorSize, long expected)
    {
        Assert.Equal(expected, FlashProgramLength.ResolveTailPadding(availableBytes, numSectors, sectorSize));
    }

    [Fact]
    public void ResolveTailPadding_SourceShortByWholeSector_ThrowsInsteadOfWritingZeros()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => FlashProgramLength.ResolveTailPadding(512L, 2L, 512L));

        Assert.Equal("availableBytes", exception.ParamName);
    }

    [Theory]
    [InlineData(-1L, 1L, 512L)]
    [InlineData(512L, -1L, 512L)]
    public void ResolveSectors_NegativeOperands_ThrowsBeforeArithmetic(long availableBytes, long declaredSectors, long sectorSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlashProgramLength.ResolveSectors(availableBytes, declaredSectors, sectorSize));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(65537L)]
    public void ResolveSectors_SectorSizeOutsideSupportedRange_Throws(long sectorSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlashProgramLength.ResolveSectors(512L, 1L, sectorSize));
    }
}