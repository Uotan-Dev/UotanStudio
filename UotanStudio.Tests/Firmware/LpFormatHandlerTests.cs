using System.Text;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks complete LP handler workflows and failed-output preservation. <para>检查 LP 处理器完整流程及失败输出保护。</para></summary>
public class LpFormatHandlerTests
{
    private const long DataOffset = 300 * 1024;

    [Fact]
    public async Task PackAndUnpack_UsesAlignedActualSizeAndPreservesAllPayloads()
    {
        using var source = new TempDirectory();
        using var output = new TempDirectory();
        var system = Enumerable.Range(0, 4097).Select(value => (byte)value).ToArray();
        source.WriteBytes("system.IMG", system);
        source.WriteBytes("vendor.img", [0xA5, 0x6B, 0x12]);
        source.WriteBytes("empty.img", []);
        var image = Path.Combine(output.Path, "super.img");
        var handler = new LpFormatHandler();
        var packed = await handler.PackAsync(source.Path, image, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);
        Assert.Equal(274432 + 8192 + 4096, new FileInfo(image).Length);
        var metadata = MetadataReader.ReadFromImageFile(image);
        Assert.Equal(3, metadata.Partitions.Count);
        Assert.Equal(0u, metadata.Partitions.Single(partition => partition.NameString == "empty").NumExtents);
        Assert.All(metadata.Extents, extent => Assert.True(extent.TargetData >= 536));
        var extracted = Path.Combine(output.Path, "extracted");
        var unpacked = await handler.UnpackAsync(image, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        var actual = File.ReadAllBytes(Path.Combine(extracted, "system.img"));
        Assert.Equal(8192, actual.Length);
        Assert.Equal(system, actual.AsSpan(0, system.Length).ToArray());
        Assert.All(actual.AsSpan(system.Length).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(new byte[] { 0xA5, 0x6B, 0x12 }, File.ReadAllBytes(Path.Combine(extracted, "vendor.img")).AsSpan(0, 3).ToArray());
        Assert.Equal(0, new FileInfo(Path.Combine(extracted, "empty.img")).Length);
    }

    [Fact]
    public async Task Pack_ExcludesItsExistingOutputAndAllowsAllEmptyPartitions()
    {
        using var source = new TempDirectory();
        source.WriteBytes("empty.img", []);
        source.WriteBytes("super.img", [1, 2, 3]);
        var image = Path.Combine(source.Path, "super.img");
        var result = await new LpFormatHandler().PackAsync(source.Path, image, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(274432 + 4096, new FileInfo(image).Length);
        var metadata = MetadataReader.ReadFromImageFile(image);
        Assert.Equal("empty", Assert.Single(metadata.Partitions).NameString);
        Assert.Empty(metadata.Extents);
    }

    [Fact]
    public async Task Pack_GrowsMetadataSlotsForLargePartitionTables()
    {
        using var source = new TempDirectory();
        using var output = new TempDirectory();
        for (var i = 0; i < 1300; i++) source.WriteBytes($"partition_{i:D4}.img", []);
        var image = Path.Combine(output.Path, "super.img");
        var result = await new LpFormatHandler().PackAsync(source.Path, image, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        var metadata = MetadataReader.ReadFromImageFile(image);
        Assert.Equal(1300, metadata.Partitions.Count);
        Assert.True(metadata.Geometry.MetadataMaxSize > 65536);
        Assert.Equal(12288L + metadata.Geometry.MetadataMaxSize * 4L + 4096, new FileInfo(image).Length);
    }

    [Fact]
    public async Task Unpack_MapsMultipleLinearAndZeroExtentsInOrder()
    {
        using var temp = new TempDirectory();
        var image = CreateImage(temp, "system", [Linear(1), new() { TargetType = MetadataFormat.LP_TARGET_TYPE_ZERO, NumSectors = 2 }, Linear(1)]);
        var result = await new LpFormatHandler().UnpackAsync(image, Path.Combine(temp.Path, "out"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        var payload = File.ReadAllBytes(Path.Combine(temp.Path, "out", "system.img"));
        Assert.Equal(2048, payload.Length);
        Assert.All(payload.AsSpan(0, 512).ToArray(), value => Assert.Equal(0x7B, value));
        Assert.All(payload.AsSpan(512, 1024).ToArray(), value => Assert.Equal(0, value));
        Assert.All(payload.AsSpan(1536).ToArray(), value => Assert.Equal(0x7B, value));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:escape")]
    [InlineData("")]
    public async Task Unpack_RejectsUnsafeNamesBeforeWriting(string name)
    {
        using var temp = new TempDirectory();
        var image = CreateImage(temp, name, [Linear(1)]);
        var output = Path.Combine(temp.Path, "out");
        var result = await new LpFormatHandler().UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.img")));
    }

    [Fact]
    public async Task Unpack_RejectsMissingExtraDevice()
    {
        using var temp = new TempDirectory();
        var extent = Linear(1);
        extent = new LpMetadataExtent { NumSectors = extent.NumSectors, TargetData = extent.TargetData, TargetType = extent.TargetType, TargetSource = 1 };
        var image = CreateImage(temp, "system", [extent], extraDevice: true);
        var output = Path.Combine(temp.Path, "out");
        var result = await new LpFormatHandler().UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Unpack_TruncationAndNoOverwritePreserveExistingOutput()
    {
        using var temp = new TempDirectory();
        var image = CreateImage(temp, "system", [Linear(2)]);
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        var target = Path.Combine(output, "system.img");
        File.WriteAllText(target, "original");
        var handler = new LpFormatHandler();
        var noOverwrite = await handler.UnpackAsync(image, output, new UnpackOptions { Overwrite = false }, TestContext.Current.CancellationToken);
        Assert.False(noOverwrite.Success);
        using (var stream = File.OpenWrite(image)) stream.SetLength(DataOffset + 512);
        var truncated = await handler.UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(truncated.Success);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Single(Directory.EnumerateFiles(output));
    }

    [Fact]
    public async Task Unpack_RejectsOversizedZeroOutputAndHonorsCancellation()
    {
        using var temp = new TempDirectory();
        var image = CreateImage(temp, "system", [new() { TargetType = MetadataFormat.LP_TARGET_TYPE_ZERO, NumSectors = 9 }]);
        var output = Path.Combine(temp.Path, "out");
        var handler = new LpFormatHandler();
        var limited = await handler.UnpackAsync(image, output, new UnpackOptions { MaximumOutputBytes = 4096 }, TestContext.Current.CancellationToken);
        Assert.False(limited.Success);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.UnpackAsync(image, output,
            new UnpackOptions { Progress = new CancelProgress(cancellation) }, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Pack_FailedInputReadPreservesExistingOutputAndRemovesTemporaryFile()
    {
        using var source = new TempDirectory();
        using var output = new TempDirectory();
        source.WriteBytes("system.img", [1, 2, 3]);
        var image = Path.Combine(output.Path, "super.img");
        File.WriteAllText(image, "original");
        using var locked = new FileStream(Path.Combine(source.Path, "system.img"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new LpFormatHandler().PackAsync(source.Path, image, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("original", File.ReadAllText(image));
        Assert.Single(Directory.EnumerateFiles(output.Path));
    }

    private static LpMetadataExtent Linear(ulong sectors) => new()
    {
        NumSectors = sectors,
        TargetType = MetadataFormat.LP_TARGET_TYPE_LINEAR,
        TargetData = (ulong)DataOffset / 512
    };

    private static string CreateImage(TempDirectory temp, string name, LpMetadataExtent[] extents, bool extraDevice = false)
    {
        var device = new LpMetadataBlockDevice { Size = 1024 * 1024, FirstLogicalSector = 536, Alignment = 4096, PartitionName = "super"u8.ToArray() };
        var metadata = new LpMetadata
        {
            Geometry = new LpMetadataGeometry { MetadataMaxSize = 65536, MetadataSlotCount = 2, LogicalBlockSize = 4096 },
            Partitions = [new() { Name = Encoding.UTF8.GetBytes(name), NumExtents = (uint)extents.Length }],
            Extents = [.. extents],
            Groups = [new() { Name = "default"u8.ToArray() }],
            BlockDevices = extraDevice ? [device, device] : [device]
        };
        var image = Path.Combine(temp.Path, "input.super");
        MetadataWriter.WriteToImageFile(image, metadata);
        using var stream = File.OpenWrite(image);
        stream.Position = DataOffset;
        stream.Write(Enumerable.Repeat((byte)0x7B, 1024).ToArray());
        return image;
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IFirmwareProgress
    {
        public void Report(double percent, string message) => cancellation.Cancel();
    }
}