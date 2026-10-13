using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkEfuseFieldTableCensusTests
{
    private static readonly int[] GroupCellCounts =
        [32, 37, 72, 38, 28, 22, 170, 43, 60, 43, 40, 53, 35, 39, 55, 41, 80, 48];

    private static readonly ulong[] GroupCellSums =
    [
        0x108002FECUL, 0x528004E70UL, 0x510012F75UL, 0x1080065D0UL, 0x30810181CUL, 0x2100017CCUL,
        0x88AD923670UL, 0x930008C69UL, 0xB1814DCE7UL, 0x828009201UL, 0x6180082B1UL, 0x51000D465UL,
        0x310004CA8UL, 0x510008205UL, 0x51000E031UL, 0x1080027C8UL, 0x52800E118UL, 0x108007DF4UL
    ];

    private static readonly ushort[] GroupFirstHardwareCodes =
    [
        0x6570, 0x0551, 0x1208, 0x6582, 0x6572, 0x0601, 0x0688, 0x0699, 0x0788, 0x0717, 0x0690, 0x0707,
        0x0279, 0x0562, 0x0989, 0x8163, 0x8167, 0x8176
    ];

    [Fact]
    public void TranscribedGroups_ReferenceBranchChainCensus_HoldsEighteenBranchesAndNineHundredThirtySixCells()
    {
        var groups = MtkEfuseFieldTable.TranscribedGroups;

        Assert.Equal(18, groups.Count);
        Assert.Equal(936, groups.Sum(group => group.Cells.Count));
        Assert.Equal(33, MtkEfuseFieldTable.SupportedHardwareCodes.Count);
        Assert.Equal(170, MtkEfuseFieldTable.MaximumCellCount);
    }

    [Fact]
    public void TranscribedGroups_PerBranchCellCountsAndSums_MatchTheReferenceTable()
    {
        var groups = MtkEfuseFieldTable.TranscribedGroups;

        Assert.Equal(GroupCellCounts.Length, groups.Count);
        for (var index = 0; index < groups.Count; index++)
        {
            Assert.Equal(GroupFirstHardwareCodes[index], groups[index].HardwareCodes[0]);
            Assert.Equal(GroupCellCounts[index], groups[index].Cells.Count);
            Assert.Equal(GroupCellSums[index], groups[index].Cells.Aggregate(0UL, (total, cell) => total + cell));
        }
    }

    [Fact]
    public void TranscribedGroups_BranchOrderAndAbsoluteMarkers_MatchTheReferenceTable()
    {
        var groups = MtkEfuseFieldTable.TranscribedGroups;

        Assert.Equal(171, groups.Sum(group => group.Cells.Count(MtkEfuseFieldTable.IsAbsolute)));
        Assert.Equal(765, groups.Sum(group => group.Cells.Count(cell => !MtkEfuseFieldTable.IsAbsolute(cell))));
        Assert.Equal(0x6570, MtkEfuseFieldTable.SupportedHardwareCodes[0]);
        Assert.Equal(0x8176, MtkEfuseFieldTable.SupportedHardwareCodes[^1]);
    }

    [Theory]
    [InlineData((ushort)0x6570, 32)]
    [InlineData((ushort)0x0335, 32)]
    [InlineData((ushort)0x1208, 72)]
    [InlineData((ushort)0x0688, 170)]
    [InlineData((ushort)0x8176, 48)]
    public void TryGetCells_KnownHardwareCode_CopiesThatBranchInFull(ushort hardwareCode, int expectedCells)
    {
        var found = MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells);

        Assert.True(found);
        Assert.Equal(expectedCells, cells.Length);
        Assert.True(MtkEfuseFieldTable.IsSupported(hardwareCode));
    }

    [Fact]
    public void TryGetCells_UnsupportedHardwareCode_ReportsAMissWithoutGuessing()
    {
        var found = MtkEfuseFieldTable.TryGetCells(0x8125, out var cells);

        Assert.False(found);
        Assert.Empty(cells);
        Assert.False(MtkEfuseFieldTable.IsSupported(0x8125));
    }
}