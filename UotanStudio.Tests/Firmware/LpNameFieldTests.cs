using System.Buffers.Binary;
using System.Text;
using UotanStudio.Delivery;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the fixed 36-byte LP name fields (partition, group and block-device) against values the field
/// cannot hold. Because the whole entry record is copied, a clipped name overwrites the adjacent attribute, flag or index
/// bytes and silently renames a partition on the device, where <c>dump-super</c>, <c>fastboot</c> and the kernel
/// read back exactly these bytes; an over-long name must therefore be rejected instead of clipped. The builder
/// contract is ASCII, so a non-ASCII name is rejected before it is encoded, and the Delivery super builder cannot
/// publish an unvalidated device name.
/// <para>守护三个定宽 36 字节的 LP 名称字段（分区名、组名、块设备分区名），拒绝字段无法承载的值。
/// 由于复制的是整条记录，裁剪后的名称会覆盖相邻的属性、标志或索引字节，并会在设备上静默改名，因为
/// <c>dump-super</c>、<c>fastboot</c> 与内核读回的就是这些字节；因此超长名称必须被拒绝而不是裁剪。构建器
/// 约定名称为 ASCII，所以非 ASCII 名称在编码之前就被拒绝，Delivery 的 super 构建也不能以未经校验的设备名
/// 发布镜像。</para>
/// </summary>
public sealed class LpNameFieldTests
{
    private const int NameFieldBytes = 36;
    private const int DescriptorPartitions = 80;
    private const int DescriptorGroups = 104;
    private const int DescriptorBlockDevices = 116;

    [Fact]
    public void AddPartition_ThirtySevenByteName_ThrowsWithMeasuredBytesAndFieldLimit()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        var name = new string('p', NameFieldBytes + 1);

        var error = Assert.Throws<ArgumentException>(() => builder.AddPartition(name, "default", 4096));

        Assert.Equal("name", error.ParamName);
        Assert.Contains("LP partition name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("37 UTF-8 bytes exceed the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGroup_ThirtySevenByteName_ThrowsWithMeasuredBytesAndFieldLimit()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        var name = new string('g', NameFieldBytes + 1);

        var error = Assert.Throws<ArgumentException>(() => builder.AddGroup(name, 0));

        Assert.Equal("name", error.ParamName);
        Assert.Contains("LP group name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("37 UTF-8 bytes exceed the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGroup_CjkNameInsideFieldWidth_ThrowsAsNonAscii()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        var name = "系统分区";
        Assert.Equal(12, Encoding.UTF8.GetByteCount(name));

        var error = Assert.Throws<ArgumentException>(() => builder.AddGroup(name, 0));

        Assert.Equal("name", error.ParamName);
        Assert.Contains("LP group name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("U+7CFB is outside the ASCII range stored by the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGroup_EmbeddedNul_ThrowsInsteadOfPublishingATruncatedName()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        var name = "qti\0dyn";

        var error = Assert.Throws<ArgumentException>(() => builder.AddGroup(name, 0));

        Assert.Contains("LP group name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("a NUL byte ends the 36-byte name field", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPartition_CjkNameInsideFieldWidth_ThrowsBeforeEncodingIntoTheField()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        var name = "超级分区";
        Assert.Equal(12, Encoding.UTF8.GetByteCount(name));

        var error = Assert.Throws<ArgumentException>(() => builder.AddPartition(name, "default", 4096));

        Assert.Equal("name", error.ParamName);
        Assert.Contains("LP partition name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("U+8D85 is outside the ASCII range stored by the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => builder.AddGroup(name, 0));
    }

    [Fact]
    public void EntryToBytes_OverlongRawName_ThrowsInsteadOfWritingIntoAdjacentFields()
    {
        var name = new string('x', 40);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var partitionError = Assert.Throws<InvalidDataException>(
            () => new LpMetadataPartition { Name = nameBytes, Attributes = 1 }.ToBytes());
        Assert.Contains("LP partition name is not representable: 40 UTF-8 bytes exceed the 36-byte name field: '" + name + "'",
            partitionError.Message, StringComparison.Ordinal);

        var groupError = Assert.Throws<InvalidDataException>(
            () => new LpMetadataPartitionGroup { Name = nameBytes, MaximumSize = 4096 }.ToBytes());
        Assert.Contains("LP group name is not representable: 40 UTF-8 bytes exceed the 36-byte name field: '" + name + "'",
            groupError.Message, StringComparison.Ordinal);

        var deviceError = Assert.Throws<InvalidDataException>(
            () => new LpMetadataBlockDevice { PartitionName = nameBytes }.ToBytes());
        Assert.Contains("LP block-device partition name is not representable", deviceError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeMetadata_BlockDeviceNameWiderThanField_ThrowsForDeliveryStyleMutation()
    {
        var metadata = MetadataBuilder.New(1024 * 1024, 65536, 2).Build();
        var deviceName = new string('超', 13);
        var deviceNameBytes = Encoding.UTF8.GetBytes(deviceName);
        Assert.Equal(39, deviceNameBytes.Length);

        metadata.BlockDevices[0] = metadata.BlockDevices[0] with { PartitionName = deviceNameBytes };

        var error = Assert.Throws<InvalidDataException>(() => MetadataWriter.SerializeMetadata(metadata));

        Assert.Contains("LP block-device partition name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("39 UTF-8 bytes exceed the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(deviceName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteToImageStream_MaximumWidthAsciiNames_LandRawInTheirOwnFields()
    {
        var partitionName = new string('p', NameFieldBytes);
        var groupName = new string('g', NameFieldBytes - 4) + "-dyn";
        Assert.Equal(NameFieldBytes, groupName.Length);
        Assert.Equal(NameFieldBytes, Encoding.UTF8.GetByteCount(groupName));
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        builder.AddGroup(groupName, 4096);
        builder.AddPartition(partitionName, groupName, 4096, 1);
        var metadata = builder.Build();

        using var image = new MemoryStream(new byte[1024 * 1024]);
        MetadataWriter.WriteToImageStream(image, metadata);
        var bytes = image.ToArray();
        var slot = MetadataFormat.LP_PARTITION_RESERVED_BYTES + (2 * MetadataFormat.LP_METADATA_GEOMETRY_SIZE);
        Assert.Equal(MetadataFormat.LP_METADATA_HEADER_MAGIC, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot + 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot + DescriptorPartitions + 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot + DescriptorGroups + 4)));

        var partitionStart = EntryStart(bytes, slot, DescriptorPartitions, 0, MetadataFormat.Sizes.Partition);
        Assert.Equal(Padded(partitionName), bytes.AsSpan(partitionStart, NameFieldBytes).ToArray());
        Assert.Equal(partitionName, Encoding.UTF8.GetString(bytes.AsSpan(partitionStart, NameFieldBytes)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(partitionStart + NameFieldBytes)));

        var syntheticStart = EntryStart(bytes, slot, DescriptorGroups, 0, MetadataFormat.Sizes.PartitionGroup);
        Assert.Equal(Padded("default"), bytes.AsSpan(syntheticStart, NameFieldBytes).ToArray());
        var groupStart = EntryStart(bytes, slot, DescriptorGroups, 1, MetadataFormat.Sizes.PartitionGroup);
        Assert.Equal(Padded(groupName), bytes.AsSpan(groupStart, NameFieldBytes).ToArray());
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(groupStart + NameFieldBytes)));
        Assert.Equal(4096ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(groupStart + NameFieldBytes + 4)));

        var deviceStart = EntryStart(bytes, slot, DescriptorBlockDevices, 0, MetadataFormat.Sizes.BlockDevice);
        Assert.Equal(Padded("super"), bytes.AsSpan(deviceStart + 24, NameFieldBytes).ToArray());
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(deviceStart + 60)));

        using var replay = new MemoryStream(bytes);
        var parsed = MetadataReader.ReadFromImageStream(replay);
        Assert.Equal(partitionName, Assert.Single(parsed.Partitions).NameString);
        Assert.Equal("default", parsed.Groups[0].NameString);
        Assert.Equal(groupName, parsed.Groups[1].NameString);
    }

    [Fact]
    public async Task DeliverySuperBuilder_NonAsciiDeviceNameWiderThanField_PublishesNothing()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "super.img");
        var deviceName = new string('超', 13);
        Assert.Equal(39, Encoding.UTF8.GetByteCount(deviceName));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DeliverySuperBuilder.BuildAsync(
            [new() { Partition = "system" }],
            new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024, Empty = true, DeviceName = deviceName },
            output,
            token: TestContext.Current.CancellationToken));

        Assert.Contains("LP block-device partition name is not representable", error.Message, StringComparison.Ordinal);
        Assert.Contains("39 UTF-8 bytes exceed the 36-byte name field", error.Message, StringComparison.Ordinal);
        Assert.Contains(deviceName, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    private static byte[] Padded(string value)
    {
        var field = new byte[NameFieldBytes];
        Encoding.UTF8.GetBytes(value).CopyTo(field);
        return field;
    }

    private static int EntryStart(byte[] image, int slot, int descriptorOffset, int entryIndex, int entrySize)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slot + 8)));
        var tableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slot + descriptorOffset)));
        return slot + headerSize + tableOffset + (entryIndex * entrySize);
    }
}