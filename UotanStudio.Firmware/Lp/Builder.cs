using System.Text;

namespace UotanStudio.Firmware.Lp;

public sealed class MetadataBuilder
{
    private readonly List<Partition> _partitions = [];
    private readonly List<LpMetadataPartitionGroup> _groups = [];
    private LpMetadataBlockDevice _blockDevice;
    private ulong _nextLinearSector;

    /// <summary>
    /// Creates a builder for the given device size and metadata layout.
    /// <para>为给定设备大小与元数据布局创建构建器。</para>
    /// </summary>
    public static MetadataBuilder New(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount)
    {
        return new MetadataBuilder
        {
            _blockDevice = new LpMetadataBlockDevice
            {
                FirstLogicalSector = (MetadataFormat.LP_PARTITION_RESERVED_BYTES + (2 * MetadataFormat.LP_METADATA_GEOMETRY_SIZE)
                                      + (metadataMaxSize * metadataSlotCount * 2UL)) / MetadataFormat.LP_SECTOR_SIZE,
                Alignment = 4096,
                AlignmentOffset = 0,
                Size = deviceSize,
                PartitionName = Encoding.UTF8.GetBytes("super"),
                Flags = 0
            }
        };
    }

    /// <summary>
    /// Adds a partition with a linear extent of the given size (bytes).
    /// <para>添加具有给定大小(字节)线性 extent 的分区。</para>
    /// </summary>
    public void AddPartition(string name, string groupName, ulong sizeInBytes, uint attributes = 0)
    {
        var sectors = (sizeInBytes + MetadataFormat.LP_SECTOR_SIZE - 1) / MetadataFormat.LP_SECTOR_SIZE;
        var extent = new LpMetadataExtent
        {
            NumSectors = sectors,
            TargetType = MetadataFormat.LP_TARGET_TYPE_LINEAR,
            TargetData = _nextLinearSector,
            TargetSource = 0
        };
        _nextLinearSector += sectors;

        var partition = new Partition(name, groupName, attributes, extent);
        _partitions.Add(partition);
    }

    /// <summary>
    /// Adds a partition group.
    /// <para>添加分区组。</para>
    /// </summary>
    public void AddGroup(string name, ulong maximumSize, uint flags = 0)
    {
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

        if (_groups.All(g => g.NameString != "default"))
        {
            _groups.Insert(0, new LpMetadataPartitionGroup
            {
                Name = Encoding.UTF8.GetBytes("default"),
                Flags = 0,
                MaximumSize = 0
            });
        }

        var geometry = new LpMetadataGeometry
        {
            Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC,
            StructSize = MetadataFormat.Sizes.Geometry,
            Checksum = new byte[32],
            MetadataMaxSize = 64 * 1024,
            MetadataSlotCount = 2,
            LogicalBlockSize = 4096
        };

        var metadata = new LpMetadata
        {
            Geometry = geometry,
            Partitions = [],
            Extents = [],
            Groups = [.. _groups],
            BlockDevices = [_blockDevice]
        };

        foreach (var p in _partitions)
        {
            var groupIndex = (uint)metadata.Groups.FindIndex(g => g.NameString == p.GroupName);
            if (groupIndex == uint.MaxValue)
            {
                throw new InvalidOperationException($"Group '{p.GroupName}' not found for partition '{p.Name}'.");
            }
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

    public sealed class Partition
    {
        public string Name { get; }

        public string GroupName { get; }

        public uint Attributes { get; }

        public List<LpMetadataExtent> Extents { get; } = [];

        internal Partition(string name, string groupName, uint attributes, LpMetadataExtent extent)
        {
            Name = name;
            GroupName = groupName;
            Attributes = attributes;
            Extents.Add(extent);
        }
    }
}
