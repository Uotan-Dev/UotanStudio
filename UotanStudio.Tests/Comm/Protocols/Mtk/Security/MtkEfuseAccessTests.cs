using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

public sealed class MtkEfuseFieldTableTests
{
    [Fact]
    public void TranscribedGroups_ReferenceCensus_MatchesGroupCodeAndCellTotals()
    {
        var groups = MtkEfuseFieldTable.TranscribedGroups;

        Assert.Equal(18, groups.Count);
        Assert.Equal(33, groups.Sum(group => group.HardwareCodes.Count));
        Assert.Equal(MtkEfuseFieldTable.SupportedHardwareCodes.Count,
            groups.Sum(group => group.HardwareCodes.Distinct().Count()));
        Assert.Equal(936, groups.Sum(group => group.Cells.Count));
        Assert.Equal(171, groups.Sum(group => group.Cells.Count(MtkEfuseFieldTable.IsAbsolute)));
        Assert.Equal(765, groups.Sum(group => group.Cells.Count(cell => !MtkEfuseFieldTable.IsAbsolute(cell))));
    }

    [Theory]
    [InlineData(0x6570, 32)]
    [InlineData(0x0551, 37)]
    [InlineData(0x1208, 72)]
    [InlineData(0x6582, 38)]
    [InlineData(0x6572, 28)]
    [InlineData(0x0601, 22)]
    [InlineData(0x0688, 170)]
    [InlineData(0x0699, 43)]
    [InlineData(0x0788, 60)]
    [InlineData(0x0717, 43)]
    [InlineData(0x0690, 40)]
    [InlineData(0x0707, 53)]
    [InlineData(0x0279, 35)]
    [InlineData(0x0562, 39)]
    [InlineData(0x0989, 55)]
    [InlineData(0x8163, 41)]
    [InlineData(0x8167, 80)]
    [InlineData(0x8176, 48)]
    public void TryGetCells_EveryTranscribedChip_ReturnsTheReferenceCellCount(ushort hardwareCode, int expectedCells)
    {
        Assert.True(MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells));

        Assert.Equal(expectedCells, cells.Length);
    }

    [Fact]
    public void TranscribedGroups_EveryHardwareCode_ResolvesToExactlyOneGroup()
    {
        var groups = MtkEfuseFieldTable.TranscribedGroups;

        Assert.Equal(groups.Sum(group => group.HardwareCodes.Count),
            groups.Sum(group => group.HardwareCodes.Distinct().Count()));
        foreach (var (hardwareCodes, cells) in groups)
        {
            foreach (var hardwareCode in hardwareCodes)
            {
                Assert.True(MtkEfuseFieldTable.TryGetCells(hardwareCode, out var resolved));
                Assert.Equal(cells, resolved);
            }
        }
    }

    [Theory]
    [InlineData(0x6571)]
    [InlineData(0x0552)]
    [InlineData(0x8166)]
    [InlineData(0x0817)]
    [InlineData(0x0000)]
    public void TryGetCells_CodeBesideATranscribedGroup_DoesNotBorrowThatTable(ushort hardwareCode)
    {
        Assert.False(MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells));

        Assert.Empty(cells);
        Assert.False(MtkEfuseFieldTable.IsSupported(hardwareCode));
    }

    [Fact]
    public void TranscribedGroups_EveryCellEncoding_IsRelativeOrCarriesTheAbsoluteMarker()
    {
        Assert.All(MtkEfuseFieldTable.TranscribedGroups.SelectMany(group => group.Cells), cell =>
            Assert.True(cell < MtkEfuseAccess.NonAddressThreshold || MtkEfuseFieldTable.IsAbsolute(cell)));
    }

    [Fact]
    public void TranscribedGroups_ReferenceLiteralEntriesBelowThreshold_StayReportedWithoutAnAddressRead()
    {
        var reported = MtkEfuseFieldTable.TranscribedGroups
            .SelectMany(group => group.Cells)
            .Count(cell => MtkEfuseFieldTable.IsAbsolute(cell)
                && (cell & 0xFFFF_FFFFUL) < MtkEfuseAccess.NonAddressThreshold);

        Assert.Equal(46, reported);
    }

    [Fact]
    public void TryGetCells_ChipWhoseReferenceCellsRepeat_KeepsEveryDuplicatePosition()
    {
        Assert.True(MtkEfuseFieldTable.TryGetCells(0x0551, out var cells));

        Assert.Equal(4, cells.Count(cell => cell == (0x1_0000_0000UL | 0x0800_0000UL)));
        Assert.Equal(0x1_0000_0000UL | 0x0800_0000UL, cells[19]);
        Assert.Equal(0x1_0000_0000UL | 0x0800_0000UL, cells[20]);
        Assert.Equal(0x1_0000_0000UL | 0x0800_0000UL, cells[32]);
        Assert.Equal(0x1_0000_0000UL | 0x0800_0000UL, cells[33]);
        Assert.Equal(0x1_0000_0000UL | 0x0800_0008UL, cells[34]);
        Assert.Equal(0x7C8UL, cells[36]);
    }

    [Fact]
    public void MaximumCellCount_LongestGroup_IsTheTableOfTheLargestTranscribedChip()
    {
        Assert.Equal(MtkEfuseFieldTable.TranscribedGroups.Max(group => group.Cells.Count),
            MtkEfuseFieldTable.MaximumCellCount);
        Assert.True(MtkEfuseFieldTable.TryGetCells(0x0688, out var cells));

        Assert.Equal(MtkEfuseFieldTable.MaximumCellCount, cells.Length);
    }

    [Fact]
    public void ValidateRange_FullTableOfEverySupportedCode_AcceptsTheWholeCellRange()
    {
        foreach (var hardwareCode in MtkEfuseFieldTable.SupportedHardwareCodes)
        {
            Assert.True(MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells));

            MtkEfuseAccess.ValidateRange(cells, 0, cells.Length);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MtkEfuseAccess.ValidateRange(cells, 0, cells.Length + 1));
        }
    }

    [Fact]
    public void TryGetCells_KnownChip_ReturnsReferenceCellOrderInTableSequence()
    {
        Assert.True(MtkEfuseFieldTable.TryGetCells(0x6570, out var cells));

        Assert.Equal(32, cells.Length);
        Assert.Equal([0x20UL, 0x30, 0x38, 0x40, 0x44, 0x48, 0x60, 0x180, 0x184, 0x188, 0x120, 0x130, 0x140, 0x144],
            cells[..14]);
        Assert.Equal(0x314UL, cells[^1]);
        Assert.False(MtkEfuseFieldTable.IsAbsolute(cells[0]));
        Assert.True(MtkEfuseFieldTable.IsAbsolute(cells[20]));
        Assert.Equal(0x08000000UL, cells[20] & 0xFFFF_FFFFUL);
    }

    [Fact]
    public void TryGetCells_ChipWithLiteralEntries_KeepsAbsoluteMarkerForEveryLiteral()
    {
        Assert.True(MtkEfuseFieldTable.TryGetCells(0x6572, out var cells));

        Assert.Equal(28, cells.Length);
        Assert.True(MtkEfuseFieldTable.IsAbsolute(cells[0]));
        Assert.Equal(0x40UL, cells[0] & 0xFFFF_FFFFUL);
        Assert.True(MtkEfuseFieldTable.IsAbsolute(cells[1]));
        Assert.Equal(0x00100000UL, cells[1] & 0xFFFF_FFFFUL);
        Assert.False(MtkEfuseFieldTable.IsAbsolute(cells[2]));
        Assert.Equal(0x20UL, cells[2]);
    }

    [Fact]
    public void TryGetCells_UnknownChip_ReturnsFalseAndEmptyCells()
    {
        Assert.False(MtkEfuseFieldTable.TryGetCells(0x0001, out var cells));
        Assert.Empty(cells);
        Assert.False(MtkEfuseFieldTable.IsSupported(0x0001));
    }

    [Fact]
    public void SupportedHardwareCodes_EveryTranscribedCode_LooksUpAndStaysDistinct()
    {
        var codes = MtkEfuseFieldTable.SupportedHardwareCodes;

        Assert.Equal(33, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.True(MtkEfuseFieldTable.IsSupported(code)));
        Assert.Contains((ushort)0x0688, codes);
        Assert.Contains((ushort)0x8167, codes);
    }

    [Fact]
    public void MaximumCellCount_LongestTranscribedTable_StaysInsideRequestCeiling()
    {
        Assert.Equal(170, MtkEfuseFieldTable.MaximumCellCount);
        Assert.True(MtkEfuseFieldTable.MaximumCellCount <= MtkEfuseAccess.MaximumRequestedCells);
    }

    [Fact]
    public void TryGetCells_CopiesEachCall_SoCallerMutationCannotPoisonTheTable()
    {
        Assert.True(MtkEfuseFieldTable.TryGetCells(0x0551, out var first));
        first[0] = 0x7FF;

        Assert.True(MtkEfuseFieldTable.TryGetCells(0x0551, out var second));
        Assert.Equal(0x20UL, second[0]);
    }
}

public sealed class MtkEfuseAccessTests
{
    [Fact]
    public void ResolveCell_BaseRelativeOffset_AddsDocumentedBase()
    {
        var cell = MtkEfuseAccess.ResolveCell(0x20, 0x00100000);

        Assert.Equal(0x00100020u, cell.Address);
        Assert.False(cell.IsReportedWithoutRead);
    }

    [Fact]
    public void ResolveCell_ResolvedValueBelowThreshold_IsReportedWithoutReading()
    {
        var cell = MtkEfuseAccess.ResolveCell(0x20, 0x100);

        Assert.Equal(0x120u, cell.Address);
        Assert.True(cell.IsReportedWithoutRead);
        Assert.Equal("20010000", Convert.ToHexString(MtkEfuseAccess.EncodeReportedValue(cell.Address)));
    }

    [Theory]
    [InlineData(0x1_0000_0000UL | 0x08000000UL, 0x08000000u, false)]
    [InlineData(0x1_0000_0000UL | 0x0000000AUL, 0x0000000Au, true)]
    [InlineData(0x1_FFFF_FFFFUL, 0xFFFF_FFFFu, false)]
    public void ResolveCell_AbsoluteMarker_BypassesTheBaseEntirely(ulong encoded, uint expected, bool reported)
    {
        var cell = MtkEfuseAccess.ResolveCell(encoded, 0x00100000);

        Assert.Equal(expected, cell.Address);
        Assert.Equal(reported, cell.IsReportedWithoutRead);
    }

    [Fact]
    public void ResolveCell_BasePlusOffsetBeyondAddressSpace_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => MtkEfuseAccess.ResolveCell(0x0000_07C8UL, uint.MaxValue));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    [InlineData(32, 1)]
    [InlineData(31, 2)]
    [InlineData(0, 33)]
    [InlineData(0, MtkEfuseAccess.MaximumRequestedCells + 1)]
    public void ValidateRange_OutsideTableOrCeiling_RejectsBeforeAnyRequest(int startIndex, int count)
    {
        var cells = new ulong[32];

        Assert.Throws<ArgumentOutOfRangeException>(() => MtkEfuseAccess.ValidateRange(cells, startIndex, count));
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(31, 1)]
    [InlineData(4, 28)]
    public void ValidateRange_InsideTableAndCeiling_Accepts(int startIndex, int count)
    {
        var cells = new ulong[32];

        MtkEfuseAccess.ValidateRange(cells, startIndex, count);
    }

    [Fact]
    public void ValidateRange_FullLongestTable_IsExactlyAtTheRequestCeiling()
    {
        var cells = new ulong[MtkEfuseFieldTable.MaximumCellCount];

        MtkEfuseAccess.ValidateRange(cells, 0, MtkEfuseFieldTable.MaximumCellCount);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MtkEfuseAccess.ValidateRange(cells, 0, MtkEfuseFieldTable.MaximumCellCount + 1));
    }

    [Theory]
    [InlineData(0x00100090u, "90001000")]
    [InlineData(0x00000000u, "00000000")]
    [InlineData(0xFFFF_FFFFu, "FFFFFFFF")]
    public void EncodeReportedValue_WritesFourLittleEndianBytes(uint address, string expectedHex)
    {
        var bytes = MtkEfuseAccess.EncodeReportedValue(address);

        Assert.Equal(4, bytes.Length);
        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        Assert.Equal(address, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }

    [Fact]
    public void CellReading_DecodesLittleEndianDwordAndRejectsMalformedHex()
    {
        var reading = new MtkEfuseCellReading(3, new MtkEfuseCell(0x100020, false), "20011000");

        Assert.Equal(3, reading.Index);
        Assert.Equal(0x00100120u, reading.Value);
        Assert.Throws<FormatException>(() => new MtkEfuseCellReading(3, new MtkEfuseCell(1, true), "20010").Value);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MtkEfuseCellReading(3, new MtkEfuseCell(1, true), "200100").Value);
    }

    [Fact]
    public void EfuseReading_CountsOnlyCellsTakenFromTheTarget()
    {
        var cells = new[]
        {
            new MtkEfuseCellReading(0, new MtkEfuseCell(0x100020, false), "20001000"),
            new MtkEfuseCellReading(1, new MtkEfuseCell(0x0000000A, true), "0A000000"),
            new MtkEfuseCellReading(2, new MtkEfuseCell(0x100030, false), "30001000")
        };
        var reading = new MtkEfuseReading(0x6570, 0x00100000, cells);

        Assert.Equal(2, reading.ReadCellCount);
        Assert.Equal(0x0000000Au, reading.Cells[1].Value);
        Assert.Equal(3, reading.Cells.Count);
    }

    [Fact]
    public void EfuseConstants_MatchTheDocumentedWindowAndCellWidth()
    {
        Assert.Equal(4, MtkEfuseAccess.CellLength);
        Assert.Equal(0x1000u, MtkEfuseAccess.NonAddressThreshold);
        Assert.Equal(0x90u, MtkEfuseAccess.PublicKeyOffset);
        Assert.Equal(0x20, MtkEfuseAccess.PublicKeyLength);
    }
}