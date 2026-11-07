using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class GptImageComposerTests
{
    private static readonly Guid LinuxDataGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid EfiSystemGuid = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    [Fact]
    public void WriteNewImage_TwoPartitionsOn512ByteDisk_ReadsBackDeclaredGeometry()
    {
        using var temp = new TempDirectory();
        var diskId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        const int blockSize = 512;
        const ulong blockCount = 8192;
        var firstId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var secondId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var target = Path.Combine(temp.Path, "composed.img");

        GptImageComposer.WriteNewImage(target, blockSize, blockCount,
        [
            new GptImagePartition("system", LinuxDataGuid, firstId, 34, 137),
            new GptImagePartition("esp", EfiSystemGuid, secondId, 138, 169)
        ], diskId, TestContext.Current.CancellationToken);

        var image = File.ReadAllBytes(target);
        Assert.Equal((long)(blockCount * blockSize), image.Length);
        Assert.Equal("EFI PART"u8.ToArray(), image.AsSpan((int)((blockCount - 1) * blockSize), 8).ToArray());
        Assert.Equal(0x55, image[510]);
        Assert.Equal(0xAA, image[511]);
        Assert.Equal(0xEE, image[450]);

        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, blockSize, blockCount, TestContext.Current.CancellationToken);
        }

        Assert.Equal(diskId, reread.DiskId);
        Assert.Equal(2, reread.Partitions.Count);
        Assert.Equal("system", reread.Partitions[0].Name);
        Assert.Equal(LinuxDataGuid, reread.Partitions[0].TypeId);
        Assert.Equal(firstId, reread.Partitions[0].Id);
        Assert.Equal(34UL, reread.Partitions[0].FirstLba);
        Assert.Equal(137UL, reread.Partitions[0].LastLba);
        Assert.Equal("esp", reread.Partitions[1].Name);
        Assert.Equal(EfiSystemGuid, reread.Partitions[1].TypeId);
        Assert.Equal(secondId, reread.Partitions[1].Id);
        Assert.Equal(138UL, reread.Partitions[1].FirstLba);
        Assert.Equal(169UL, reread.Partitions[1].LastLba);
        Assert.Equal(0UL, reread.Partitions[0].Attributes);
        Assert.Equal(0UL, reread.Partitions[1].Attributes);
        Assert.All(image.AsSpan(34 * blockSize, (int)((169UL + 1 - 34) * blockSize)).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteNewImage_DefaultDiskId_ProducesNonEmptyIdentifier()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "composed.img");
        const int blockSize = 512;
        const ulong blockCount = 8192;

        GptImageComposer.WriteNewImage(target, blockSize, blockCount,
        [
            new GptImagePartition("data", LinuxDataGuid, Guid.NewGuid(), 34, 137)
        ], cancellationToken: TestContext.Current.CancellationToken);

        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, blockSize, blockCount, TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(Guid.Empty, reread.DiskId);
        var single = Assert.Single(reread.Partitions);
        Assert.Equal("data", single.Name);
    }

    [Fact]
    public void WriteNewImage_OverlappingPartitions_ThrowsAndKeepsDestination()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "composed.img");

        Assert.Throws<InvalidDataException>(() => GptImageComposer.WriteNewImage(target, 512, 8192,
        [
            new GptImagePartition("a", LinuxDataGuid, Guid.NewGuid(), 34, 137),
            new GptImagePartition("b", LinuxDataGuid, Guid.NewGuid(), 100, 169)
        ], cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void WriteNewImage_EmptyPartitionList_WritesBareImage()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "composed.img");
        const int blockSize = 512;
        const ulong blockCount = 8192;

        GptImageComposer.WriteNewImage(target, blockSize, blockCount, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((long)(blockCount * blockSize), new FileInfo(target).Length);
        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, blockSize, blockCount, TestContext.Current.CancellationToken);
        }

        Assert.Empty(reread.Partitions);
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(File.ReadAllBytes(target).AsSpan((int)((blockCount - 1) * blockSize), 8)));
    }
}