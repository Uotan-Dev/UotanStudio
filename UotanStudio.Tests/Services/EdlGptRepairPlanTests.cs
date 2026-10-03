using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlGptRepairPlanTests
{
    private static GptTable CanonicalTable()
    {
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        using var stream = new MemoryStream(disk);
        return GptReader.Read(stream, 512, 128, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PlanGptWrite_ReportedGeometry_EmitsBothExtentsOnTheRequestedLun()
    {
        var table = CanonicalTable();

        var requests = EdlGptPartitionService.PlanGptWrite(table, 512, 128, 0);

        Assert.Equal(2, requests.Count);
        Assert.Equal(0L, requests[0].StartSector);
        Assert.Equal(34L, requests[0].SectorCount);
        Assert.Equal(95L, requests[1].StartSector);
        Assert.Equal(33L, requests[1].SectorCount);
        Assert.All(requests, request =>
        {
            Assert.Equal(0, request.Lun);
            Assert.Equal(request.SectorCount * 512, request.Bytes.Length);
            Assert.Equal($"gpt-{request.StartSector}.img", request.FileName);
        });
    }

    [Fact]
    public void PlanGptWrite_BackupExtentEndsExactlyAtTheLastReportedSector()
    {
        var table = CanonicalTable();

        var requests = EdlGptPartitionService.PlanGptWrite(table, 512, 4096, 1);

        var backup = requests[1];
        Assert.Equal(checked(backup.StartSector + backup.SectorCount), (long)table.BlockCount);
        Assert.Equal(1, backup.Lun);
    }

    [Fact]
    public void PlanGptWrite_TableBeyondTheReportedLun_IsRejected()
    {
        var table = CanonicalTable();

        var error = Assert.Throws<InvalidDataException>(
            () => EdlGptPartitionService.PlanGptWrite(table, 512, 64, 0));

        Assert.Contains("only holds 64 sectors", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanGptWrite_BlockSizeMismatchingReportedSectorSize_IsRejected()
    {
        var table = CanonicalTable();

        var error = Assert.Throws<InvalidDataException>(
            () => EdlGptPartitionService.PlanGptWrite(table, 4096, 128, 0));

        Assert.Contains("512-byte blocks", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 128)]
    [InlineData(512, 0)]
    [InlineData(-1, 128)]
    public void PlanGptWrite_NonPositiveGeometry_IsRejectedBeforeAnyExtentIsBuilt(long sectorSize,
        long sectorCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EdlGptPartitionService.PlanGptWrite(CanonicalTable(), sectorSize, sectorCount, 0));
    }

    [Fact]
    public void PlanGptWrite_NullTable_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => EdlGptPartitionService.PlanGptWrite(null!, 512, 128, 0));
    }
}