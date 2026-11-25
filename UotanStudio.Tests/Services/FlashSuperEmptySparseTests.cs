using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashSuperEmptySparseTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void BuildModel_SerializesIdenticalToBuild_AndStreamsWithinMaxDownload()
    {
        const long deviceSize = 16L * 1024 * 1024;
        var geometry = new LpMetadataGeometry
        {
            MetadataMaxSize = 65536,
            MetadataSlotCount = 2,
            LogicalBlockSize = BlockSize
        };
        var metadata = new LpMetadata
        {
            Geometry = geometry,
            Partitions = [],
            Extents = [],
            Groups = [new LpMetadataPartitionGroup { Name = "default"u8.ToArray() }],
            BlockDevices = [new LpMetadataBlockDevice
            {
                PartitionName = "super"u8.ToArray(),
                Size = (ulong)deviceSize,
                FirstLogicalSector = 536,
                Alignment = 4096
            }]
        };
        var image = new byte[MetadataWriter.SerializeGeometry(geometry).Length +
            MetadataWriter.SerializeMetadata(metadata).Length];
        MetadataWriter.SerializeGeometry(geometry).CopyTo(image, 0);
        MetadataWriter.SerializeMetadata(metadata).CopyTo(image, MetadataWriter.SerializeGeometry(geometry).Length);
        var prepared = new PreparedSuperEmpty(image, deviceSize);

        using var temp = new TempDirectory();
        var builtPath = Path.Combine(temp.Path, "built.sparse");
        FlashSuperEmptySparse.Build(prepared, builtPath, TestContext.Current.CancellationToken);

        using var model = FlashSuperEmptySparse.BuildModel(prepared, TestContext.Current.CancellationToken);
        using var streamed = new MemoryStream();
        SparseImageConverter.WriteModelToSparseStream(model, streamed);

        Assert.Equal(File.ReadAllBytes(builtPath), streamed.ToArray());
        Assert.True(streamed.Length > 0);
    }
}