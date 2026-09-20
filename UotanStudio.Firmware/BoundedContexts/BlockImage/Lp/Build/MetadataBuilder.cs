using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Build;

/// <summary>Builds single-device logical partition metadata. <para>构建单设备逻辑分区元数据。</para></summary>
public sealed class MetadataBuilder
{
    private readonly List<Partition> _partitions = [];
    private readonly List<LpMetadataPartitionGroup> _groups = [];
    private LpMetadataBlockDevice _blockDevice;
    private LpMetadataGeometry _geometry;
    private ulong _nextLinearSector;

    /// <summary>
    /// Creates a builder for the given device size and metadata layout.
    /// <para>为给定设备大小与元数据布局创建构建器。</para>
    /// </summary>
    public static MetadataBuilder New(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount)
        => New(deviceSize, metadataMaxSize, metadataSlotCount, 4096);

    /// <summary>
    /// Creates a builder with an explicit logical block size; extent addresses remain 512-byte sectors.
    /// <para>按指定逻辑块大小创建构建器；extent 地址仍以 512 字节扇区计量。</para>
    /// </summary>
    public static MetadataBuilder New(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount, uint logicalBlockSize)
    {
        var geometry = new LpMetadataGeometry
        {
            Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC,
            StructSize = MetadataFormat.Sizes.Geometry,
            Checksum = new byte[32],
            MetadataMaxSize = metadataMaxSize,
            MetadataSlotCount = metadataSlotCount,
            LogicalBlockSize = logicalBlockSize
        };
        MetadataReader.ValidateGeometry(geometry);
        if (deviceSize % logicalBlockSize != 0)
            throw new ArgumentException("Device size must be a multiple of the logical block size.", nameof(deviceSize));

        var reservedBytes = checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
            + (ulong)metadataMaxSize * metadataSlotCount * 2);
        var firstLogicalByte = AlignUp(reservedBytes, logicalBlockSize);
        if (firstLogicalByte > deviceSize || logicalBlockSize > deviceSize - firstLogicalByte)
            throw new ArgumentException("Device must have room for metadata and at least one logical block.", nameof(deviceSize));
        var firstLogicalSector = firstLogicalByte / MetadataFormat.LP_SECTOR_SIZE;
        return new MetadataBuilder
        {
            _geometry = geometry,
            _nextLinearSector = firstLogicalSector,
            _blockDevice = new LpMetadataBlockDevice
            {
                FirstLogicalSector = firstLogicalSector,
                Alignment = logicalBlockSize,
                AlignmentOffset = 0,
                Size = deviceSize,
                PartitionName = Encoding.UTF8.GetBytes("super"),
                Flags = 0
            }
        };
    }

    /// <summary>
    /// Adds a partition, rounding its size up to a logical block; an empty partition has no extents.
    /// <para>添加分区，大小向上对齐逻辑块；空分区不分配 extent。</para>
    /// </summary>
    public void AddPartition(string name, string groupName, ulong sizeInBytes, uint attributes = 0)
    {
        ValidateName(name, nameof(name));
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("LP partition names allow only ASCII letters, digits, and underscores.", nameof(name));
        ValidateName(groupName, nameof(groupName));
        if (_partitions.Any(p => p.Name == name))
            throw new ArgumentException("Partition name already exists.", nameof(name));
        MetadataReader.ValidateGeometry(_geometry);
        var sectors = AlignUp(sizeInBytes, _geometry.LogicalBlockSize) / MetadataFormat.LP_SECTOR_SIZE;
        var deviceSectors = _blockDevice.Size / MetadataFormat.LP_SECTOR_SIZE;
        if (_nextLinearSector > deviceSectors || sectors > deviceSectors - _nextLinearSector)
            throw new ArgumentException("Partition exceeds the available device data area.", nameof(sizeInBytes));
        var extent = new LpMetadataExtent
        {
            NumSectors = sectors,
            TargetType = MetadataFormat.LP_TARGET_TYPE_LINEAR,
            TargetData = _nextLinearSector,
            TargetSource = 0
        };
        _nextLinearSector = checked(_nextLinearSector + sectors);

        var partition = new Partition(name, groupName, attributes, extent);
        _partitions.Add(partition);
    }

    /// <summary>
    /// Adds a partition group.
    /// <para>添加分区组。</para>
    /// </summary>
    public void AddGroup(string name, ulong maximumSize, uint flags = 0)
    {
        ValidateName(name, nameof(name));
        if (_groups.Any(g => g.NameString == name))
            throw new ArgumentException("Group name already exists.", nameof(name));
        _groups.Add(new LpMetadataPartitionGroup
        {
            Name = Encoding.UTF8.GetBytes(name),
            Flags = flags,
            MaximumSize = maximumSize
        });
    }

    /// <summary>
    /// Exports the built metadata.
    /// <para>导出构建的元数据。</para>
    /// </summary>
    public LpMetadata Build()
    {
        MetadataReader.ValidateGeometry(_geometry);
        if (_groups.All(g => g.NameString != "default"))
        {
            _groups.Insert(0, new LpMetadataPartitionGroup
            {
                Name = Encoding.UTF8.GetBytes("default"),
                Flags = 0,
                MaximumSize = 0
            });
        }

        var metadata = new LpMetadata
        {
            Geometry = _geometry,
            Partitions = [],
            Extents = [],
            Groups = [.. _groups],
            BlockDevices = [_blockDevice]
        };

        var groupBytes = new ulong[metadata.Groups.Count];
        foreach (var p in _partitions)
        {
            var groupIndex = (uint)metadata.Groups.FindIndex(g => g.NameString == p.GroupName);
            if (groupIndex == uint.MaxValue)
            {
                throw new InvalidOperationException($"Group '{p.GroupName}' not found for partition '{p.Name}'.");
            }
            foreach (var extent in p.Extents)
                groupBytes[groupIndex] = checked(groupBytes[groupIndex] + extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE);
            var maximumSize = metadata.Groups[(int)groupIndex].MaximumSize;
            if (maximumSize != 0 && groupBytes[groupIndex] > maximumSize)
                throw new InvalidOperationException($"Partitions exceed the maximum size of group '{p.GroupName}'.");
            metadata.Partitions.Add(new LpMetadataPartition
            {
                Name = Encoding.UTF8.GetBytes(p.Name),
                Attributes = p.Attributes,
                FirstExtentIndex = (uint)metadata.Extents.Count,
                NumExtents = p.Extents.Count == 0 ? 0 : (uint)p.Extents.Count,
                GroupIndex = groupIndex
            });
            metadata.Extents.AddRange(p.Extents);
        }

        return metadata;
    }

    private static ulong AlignUp(ulong size, uint alignment)
    {
        var remainder = size % alignment;
        return remainder == 0 ? size : checked(size + alignment - remainder);
    }

    private static void ValidateName(string name, string parameterName)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || Encoding.UTF8.GetByteCount(name) > 36)
            throw new ArgumentException("LP names must contain 1 to 36 UTF-8 bytes without NUL.", parameterName);
    }

    /// <summary>Describes a planned partition. <para>描述规划中的分区。</para></summary>
    public sealed class Partition
    {
        /// <summary>Gets the partition name. <para>获取分区名称。</para></summary>
        public string Name { get; }

        /// <summary>Gets the group name. <para>获取组名称。</para></summary>
        public string GroupName { get; }

        /// <summary>Gets partition attributes. <para>获取分区属性。</para></summary>
        public uint Attributes { get; }

        /// <summary>Gets the planned extents. <para>获取规划的 extent。</para></summary>
        public List<LpMetadataExtent> Extents { get; } = [];

        internal Partition(string name, string groupName, uint attributes, LpMetadataExtent extent)
        {
            Name = name;
            GroupName = groupName;
            Attributes = attributes;
            if (extent.NumSectors != 0) Extents.Add(extent);
        }
    }
}