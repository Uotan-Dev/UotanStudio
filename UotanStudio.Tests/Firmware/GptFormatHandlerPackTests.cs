using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class GptFormatHandlerPackTests : IDisposable
{
    private static readonly Guid LinuxFileSystemData = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gpt-pack-" + Guid.NewGuid().ToString("N"));

    public GptFormatHandlerPackTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task PackAsync_UnmodifiedTable_RepublishesImageByteIdentically()
    {
        var source = WriteDisk("source.img");
        var target = Path.Combine(_root, "republished.img");

        var result = await new GptFormatHandler().PackAsync(source, target, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.Contains("GPT", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackAsync_PublishedImage_KeepsBothEntryArraysAndPartitions()
    {
        var source = WriteDisk("paired.img");
        var target = Path.Combine(_root, "published.img");

        await new GptFormatHandler().PackAsync(source, target, TestContext.Current.CancellationToken);

        using var stream = File.OpenRead(target);
        var table = GptReader.Read(stream, 512, (ulong)(stream.Length / 512), TestContext.Current.CancellationToken);
        Assert.Equal(2, table.Partitions.Count);
        Assert.Equal("system", table.Partitions[0].Name);
        Assert.Equal("vendor", table.Partitions[1].Name);
        Assert.Equal(128u, table.PartitionEntryCount);
    }

    [Fact]
    public async Task PackAsync_ImageWithoutGptHeader_ReturnsFailureAndWritesNothing()
    {
        var source = Path.Combine(_root, "no-gpt.img");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        var target = Path.Combine(_root, "out.img");

        var result = await new GptFormatHandler().PackAsync(source, target, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("EFI PART", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void FormatCapabilities_GptIsWritableAndAlwaysFailingPackersAreReadOnly()
    {
        Assert.IsAssignableFrom<IWritableFormatHandler>(new GptFormatHandler());
        Assert.IsNotAssignableFrom<IWritableFormatHandler>(new OppoFormatHandler());
        Assert.IsNotAssignableFrom<IWritableFormatHandler>(new AvbFormatHandler());
        Assert.IsNotAssignableFrom<IWritableFormatHandler>(new DiffFormatHandler());
    }

    private string WriteDisk(string name)
    {
        var path = Path.Combine(_root, name);
        var table = GptTableWriter.Create(Guid.NewGuid(), 512, 8192,
        [
            new GptPartition(0, LinuxFileSystemData, Guid.NewGuid(), 34, 2000, 0, "system", 34 * 512, 1967 * 512),
            new GptPartition(1, LinuxFileSystemData, Guid.NewGuid(), 2001, 4000, 0, "vendor", 2001 * 512, 2000 * 512)
        ]);
        GptTableWriter.WriteNewImage(path, table, TestContext.Current.CancellationToken);
        return path;
    }
}