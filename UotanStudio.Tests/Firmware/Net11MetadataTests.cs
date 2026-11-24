using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Lp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Net11MetadataTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(92)]
    [InlineData(104)]
    [InlineData(116)]
    public void ParseMetadata_ExcessiveDescriptorCount_RejectsBeforeAllocation(int descriptorOffset)
    {
        var serialized = MetadataWriter.SerializeMetadata(CreateBuilder().Build());
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(descriptorOffset + 4), uint.MaxValue);
        FixHeaderChecksum(serialized);

        var error = Assert.Throws<InvalidDataException>(() => Parse(serialized));

        Assert.Equal("Invalid LP table descriptor.", error.Message);
    }

    [Theory]
    [InlineData(80u, uint.MaxValue, 52u)]
    [InlineData(80u, 0u, 51u)]
    [InlineData(92u, uint.MaxValue, 24u)]
    [InlineData(92u, 0u, 23u)]
    [InlineData(104u, uint.MaxValue, 48u)]
    [InlineData(104u, 0u, 47u)]
    [InlineData(116u, uint.MaxValue, 64u)]
    [InlineData(116u, 0u, 63u)]
    public void ParseMetadata_InvalidDescriptorOffsetOrEntrySize_Rejects(int descriptorOffset, uint offset, uint entrySize)
    {
        var serialized = MetadataWriter.SerializeMetadata(CreateBuilder().Build());
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(descriptorOffset), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(descriptorOffset + 8), entrySize);
        FixHeaderChecksum(serialized);

        var error = Assert.Throws<InvalidDataException>(() => Parse(serialized));

        Assert.Equal("Invalid LP table descriptor.", error.Message);
    }

    [Fact]
    public void ParseMetadata_ZeroEntryTables_ReturnEmptyListsAndRoundTrip()
    {
        var metadata = CreateBuilder().Build();
        metadata.Groups.Clear();
        var serialized = MetadataWriter.SerializeMetadata(metadata);

        var parsed = Parse(serialized);

        Assert.Empty(parsed.Partitions);
        Assert.Empty(parsed.Extents);
        Assert.Empty(parsed.Groups);
        Assert.Single(parsed.BlockDevices);
        Assert.Equal(serialized, MetadataWriter.SerializeMetadata(parsed));
    }

    [Fact]
    public void ParseMetadata_ZeroBlockDeviceCount_RejectsMissingSuperDevice()
    {
        var metadata = CreateBuilder().Build();
        metadata.BlockDevices.Clear();

        var error = Assert.Throws<InvalidDataException>(() => Parse(MetadataWriter.SerializeMetadata(metadata)));

        Assert.Equal("LP metadata does not specify a super device.", error.Message);
    }

    [Fact]
    public void ParseMetadata_MultipleEntriesInEveryTable_PreservesCapacityAndSerialization()
    {
        var builder = CreateBuilder();
        for (var index = 0; index < 3; index++)
        {
            builder.AddGroup($"group_{index}", 0);
        }

        for (var index = 0; index < 10; index++)
        {
            builder.AddPartition($"partition_{index}", $"group_{index % 3}", 4096);
        }

        var metadata = builder.Build();
        for (var index = 0; index < 4; index++)
        {
            metadata.BlockDevices.Add(metadata.BlockDevices[0] with
            {
                FirstLogicalSector = 600 + (ulong)index * 100,
                PartitionName = Encoding.UTF8.GetBytes($"backup_{index}")
            });
        }
        var serialized = MetadataWriter.SerializeMetadata(metadata);

        var parsed = Parse(serialized);

        Assert.Equal(10, parsed.Partitions.Count);
        Assert.Equal(10, parsed.Partitions.Capacity);
        Assert.Equal(10, parsed.Extents.Count);
        Assert.Equal(10, parsed.Extents.Capacity);
        Assert.Equal(4, parsed.Groups.Count);
        Assert.Equal(4, parsed.Groups.Capacity);
        Assert.Equal(5, parsed.BlockDevices.Count);
        Assert.Equal(5, parsed.BlockDevices.Capacity);
        Assert.Equal(serialized, MetadataWriter.SerializeMetadata(parsed));
    }

    [Fact]
    public void AddPartitionAndGroup_DuplicateOrdinalNames_RejectWithoutPollutingState()
    {
        var builder = CreateBuilder();
        builder.AddGroup("default", 0);
        builder.AddPartition("system", "default", 4096);

        Assert.Throws<ArgumentException>(() => builder.AddGroup("default", 0));
        Assert.Throws<ArgumentException>(() => builder.AddPartition("system", "default", 4096));

        var metadata = builder.Build();
        Assert.Equal(["system"], metadata.PartitionNames);
        Assert.Equal(["default"], metadata.Groups.Select(group => group.NameString));
        Assert.Single(metadata.Extents);
    }

    [Fact]
    public void AddPartitionAndGroup_CaseVariants_RemainDistinct()
    {
        var builder = CreateBuilder();
        builder.AddGroup("Group", 0);
        builder.AddGroup("group", 0);
        builder.AddPartition("System", "Group", 4096);
        builder.AddPartition("system", "group", 4096);

        var metadata = builder.Build();

        Assert.Equal(["System", "system"], metadata.PartitionNames);
        Assert.Equal(["default", "Group", "group"], metadata.Groups.Select(group => group.NameString));
        Assert.Equal([1u, 2u], metadata.Partitions.Select(partition => partition.GroupIndex));
    }

    [Fact]
    public void AddPartition_FailedValidation_DoesNotReserveNameOrAdvanceExtents()
    {
        var builder = CreateBuilder();
        Assert.Throws<OverflowException>(() => builder.AddPartition("retry", "default", ulong.MaxValue));

        builder.AddPartition("retry", "default", 4096);
        var metadata = builder.Build();

        Assert.Equal(["retry"], metadata.PartitionNames);
        Assert.Equal(536UL, Assert.Single(metadata.Extents).TargetData);
    }

    [Fact]
    public void Build_MissingGroupFailure_DoesNotCommitSyntheticDefaultAndCanRetry()
    {
        var builder = CreateBuilder();
        builder.AddPartition("system", "dynamic", 4096);
        Assert.Throws<InvalidOperationException>(builder.Build);

        builder.AddGroup("dynamic", 0);
        builder.AddGroup("default", 0);
        var metadata = builder.Build();

        Assert.Equal(["dynamic", "default"], metadata.Groups.Select(group => group.NameString));
        Assert.Equal(0u, Assert.Single(metadata.Partitions).GroupIndex);
    }

    [Fact]
    public void Build_MultipleExtents_PreservesAdditionOrderAndMatchesGoldenSerialization()
    {
        var metadata = CreateOrderedBuilder().Build();
        var serialized = MetadataWriter.SerializeMetadata(metadata);

        Assert.Equal(["zeta", "alpha"], metadata.PartitionNames);
        Assert.Equal(["default", "group_a", "group_b"], metadata.Groups.Select(group => group.NameString));
        Assert.Equal([536UL, 552UL], metadata.Extents.Select(extent => extent.TargetData));
        Assert.Equal([16UL, 16UL], metadata.Extents.Select(extent => extent.NumSectors));
        Assert.Equal(2, metadata.Partitions.Capacity);
        Assert.Equal(2, metadata.Extents.Capacity);
        Assert.Equal(488, serialized.Length);
        Assert.Equal("567BB75768533EB729B779D18616EDB055B04D86E85746FE9B81D466D52F72EF",
            Convert.ToHexString(LpMetadata.ComputeSha256(serialized)));
    }

    [Fact]
    public void Read_OverlappingPartitionsInReverseImageOrder_Rejects()
    {
        var disk = GptReaderTests.CreateDisk(512);
        WriteEntry(disk, 0, 60, 65, "image-first");
        WriteEntry(disk, 1, 63, 67, "image-second");
        GptReaderTests.FixArrays(disk);
        using var source = new MemoryStream(disk);

        var error = Assert.Throws<InvalidDataException>(() =>
            GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken));

        Assert.Equal("GPT partitions overlap.", error.Message);
    }

    [Fact]
    public void Read_NonOverlappingPartitionsInReverseImageOrder_PreservesImageOrder()
    {
        var disk = GptReaderTests.CreateDisk(512);
        WriteEntry(disk, 0, 60, 63, "image-first");
        WriteEntry(disk, 1, 40, 43, "low");
        WriteEntry(disk, 2, 50, 52, "middle");
        GptReaderTests.FixArrays(disk);
        using var source = new MemoryStream(disk);

        var table = GptReader.Read(source, 512, 128, TestContext.Current.CancellationToken);

        Assert.Equal(["image-first", "low", "middle"], table.Partitions.Select(partition => partition.Name));
        Assert.Equal([0, 1, 2], table.Partitions.Select(partition => partition.Index));
    }

    private static MetadataBuilder CreateBuilder() => MetadataBuilder.New(1048576, 65536, 2);

    private static MetadataBuilder CreateOrderedBuilder()
    {
        var builder = CreateBuilder();
        builder.AddGroup("group_a", 8192, 1);
        builder.AddGroup("group_b", 12288, 2);
        builder.AddPartition("zeta", "group_b", 4097, 1);
        builder.AddPartition("alpha", "group_a", 4097, 8);
        return builder;
    }

    private static LpMetadata Parse(byte[] serialized)
    {
        using var stream = new MemoryStream(serialized);
        var geometry = new LpMetadataGeometry
        {
            MetadataMaxSize = 65536,
            MetadataSlotCount = 2,
            LogicalBlockSize = 4096
        };
        return MetadataReader.ParseMetadata(geometry, stream);
    }

    private static void FixHeaderChecksum(byte[] serialized)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(serialized.AsSpan(8)));
        serialized.AsSpan(12, 32).Clear();
        LpMetadata.ComputeSha256(serialized.AsSpan(0, headerSize)).CopyTo(serialized.AsSpan(12, 32));
    }

    private static void WriteEntry(byte[] disk, int index, ulong first, ulong last, string name)
    {
        var type = disk.AsSpan(1024, 16).ToArray();
        var entry = disk.AsSpan(1024 + (index * 128), 128);
        entry.Clear();
        type.AsSpan().CopyTo(entry[..16]);
        new Guid($"00000000-0000-0000-0000-{index + 1:D12}").TryWriteBytes(entry[16..32]);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], first);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], last);
        Encoding.Unicode.GetBytes(name, entry[56..128]);
    }
}