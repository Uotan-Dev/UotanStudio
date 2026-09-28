using System.IO.Compression;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ProjectCreationSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "creation-space-" + Guid.NewGuid().ToString("N"));
    public ProjectCreationSpaceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void CopyOnlyDoesNotChargeForUnselectedUnpacking()
    {
        var path = Path.Combine(_root, "input.img");
        File.WriteAllBytes(path, new byte[4096]);
        var estimate = ProjectCreationSpaceEstimator.Estimate([path, path], [], false, false);
        Assert.Equal(ProjectCreationSpaceEstimator.MetadataReserve + 4096, estimate.TargetBytes);
        Assert.Equal(0, estimate.TemporaryBytes);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void SelectedPartitionsCountExpandedBytesWithoutOriginalCopy(bool fully, int multiplier)
    {
        var estimate = ProjectCreationSpaceEstimator.Estimate([], [new("system", "payload", 1024 * 1024, true)], false, fully);
        Assert.Equal(ProjectCreationSpaceEstimator.MetadataReserve + multiplier * 1024 * 1024, estimate.TargetBytes);
    }

    [Fact]
    public void SparseUnpackingUsesLogicalSizeRatherThanTinyStoredHeader()
    {
        var path = Path.Combine(_root, "system.img");
        var header = new byte[28];
        SparseHeader.CreateDefault(4096, 4096).WriteTo(header);
        File.WriteAllBytes(path, header);
        var estimate = ProjectCreationSpaceEstimator.Estimate([path], [], true, false);
        Assert.Equal(ProjectCreationSpaceEstimator.MetadataReserve + 28 + 2L * 4096 * 4096, estimate.TargetBytes);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void ZipPayloadTemporaryDemandUsesUncompressedMemberLength()
    {
        var path = Path.Combine(_root, "ota.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("payload.bin").Open()) stream.Write(new byte[1024 * 1024]);
        var estimate = ProjectCreationSpaceEstimator.Estimate([], [], false, false, path);
        Assert.Equal(1024 * 1024, estimate.TemporaryBytes);
        Assert.True(new FileInfo(path).Length < estimate.TemporaryBytes);
    }

    [Fact]
    public void SharedVolumeCombinesTemporaryAndTargetDemand()
    {
        var shortages = ProjectCreationSpaceEstimator.FindShortages(_root, new(100, 80, false), _ => 150, _ => "same");
        Assert.Equal(180, Assert.Single(shortages).RequiredBytes);
    }

    [Fact]
    public void SeparateTemporaryVolumeIsCheckedIndependently()
    {
        var shortages = ProjectCreationSpaceEstimator.FindShortages(_root, new(100, 80, false),
            p => p == _root ? 200 : 50, p => p == _root ? "target" : "temporary");
        var shortage = Assert.Single(shortages);
        Assert.Equal(Path.GetTempPath(), shortage.Directory);
        Assert.Equal(80, shortage.RequiredBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(101)]
    public void UnknownOrSufficientFreeSpaceDoesNotReportAShortage(long available)
        => Assert.Empty(ProjectCreationSpaceEstimator.FindShortages(_root, new(100, 0, false), _ => available, _ => "volume"));

    [Fact]
    public void HugeMetadataSaturatesInsteadOfWrappingToNegative()
    {
        var estimate = ProjectCreationSpaceEstimator.Estimate([], [new("system", "super", long.MaxValue, false)], false, true);
        Assert.Equal(long.MaxValue, estimate.TargetBytes);
    }

    [Fact]
    public void MissingDestinationCanBeCheckedWithoutCreatingDirectories()
    {
        var path = Path.Combine(_root, "missing", "device");
        Assert.True(DiskSpaceEstimator.GetFreeBytes(path) >= 0);
        Assert.False(Directory.Exists(path));
    }
}