using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;

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
    /// <param name="deviceSize">
    /// Total super size in bytes, a multiple of the default 4096-byte logical block size.
    /// <para>super 总大小（字节），须为默认 4096 字节逻辑块大小的整数倍。</para>
    /// </param>
    /// <param name="metadataMaxSize">
    /// Per-slot metadata capacity in bytes.
    /// <para>每个槽位的元数据容量（字节）。</para>
    /// </param>
    /// <param name="metadataSlotCount">
    /// Number of metadata slots reserved by the geometry.
    /// <para>几何信息预留的元数据槽位数量。</para>
    /// </param>
    /// <returns>
    /// A builder whose data area starts after the reserved metadata region with a 4096-byte logical block size.
    /// <para>返回数据区紧随预留元数据区域、逻辑块大小为 4096 字节的构建器。</para>
    /// </returns>
    public static MetadataBuilder New(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount)
        => New(deviceSize, metadataMaxSize, metadataSlotCount, 4096);

    /// <summary>
    /// Creates a builder with an explicit logical block size; extent addresses remain 512-byte sectors.
    /// <para>按指定逻辑块大小创建构建器；extent 地址仍以 512 字节扇区计量。</para>
    /// </summary>
    /// <param name="deviceSize">
    /// Total super size in bytes; it must be a multiple of <paramref name="logicalBlockSize"/> and leave room for the metadata plus at least one logical block.
    /// <para>super 总大小（字节）；须为 <paramref name="logicalBlockSize"/> 的整数倍，并在元数据之外至少留出一个逻辑块。</para>
    /// </param>
    /// <param name="metadataMaxSize">
    /// Per-slot metadata capacity in bytes, validated together with the geometry dimensions.
    /// <para>每个槽位的元数据容量（字节），随几何尺寸一并校验。</para>
    /// </param>
    /// <param name="metadataSlotCount">
    /// Number of metadata slots reserved by the geometry.
    /// <para>几何信息预留的元数据槽位数量。</para>
    /// </param>
    /// <param name="logicalBlockSize">
    /// Alignment unit in bytes; must be at least one sector and a power of two.
    /// <para>对齐单位（字节）；须不小于一个扇区且为 2 的幂。</para>
    /// </param>
    /// <returns>
    /// A builder that lays out a single <c>super</c> block device starting at the first aligned data sector.
    /// <para>返回从首个对齐数据扇区开始排布单一 <c>super</c> 块设备的构建器。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="deviceSize"/> is not block-aligned or too small for the reserved layout.
    /// <para><paramref name="deviceSize"/> 未按块对齐，或不足以容纳预留布局。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The resulting geometry dimensions are invalid or excessive.
    /// <para>得到的几何尺寸无效或超出上限。</para>
    /// </exception>
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
    /// <param name="name">
    /// Partition name: 1 to 36 UTF-8 bytes without NUL, limited to ASCII letters, digits and underscores, unique across partitions.
    /// <para>分区名：1 至 36 个 UTF-8 字节且不含 NUL，仅限 ASCII 字母、数字与下划线，且在所有分区间唯一。</para>
    /// </param>
    /// <param name="groupName">
    /// Owning group name, validated by the same name rules; the group itself is checked when <see cref="Build"/> runs.
    /// <para>所属组名，按相同名称规则校验；组本身的存在性在 <see cref="Build"/> 时检查。</para>
    /// </param>
    /// <param name="sizeInBytes">
    /// Requested partition size, aligned up and appended to the next free linear extent position.
    /// <para>请求的分区大小，向上对齐后追加到下一个空闲线性 extent 位置。</para>
    /// </param>
    /// <param name="attributes">
    /// Raw LP partition attribute flags stored unchanged.
    /// <para>原样保存的 LP 分区属性标志。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A name is invalid or duplicated, or the aligned size does not fit the device data area.
    /// <para>名称无效或重复，或对齐后的大小超出设备数据区。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The builder geometry fails validation.
    /// <para>构建器几何信息未通过校验。</para>
    /// </exception>
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
    /// <param name="name">
    /// Group name: 1 to 36 UTF-8 bytes without NUL, unique across groups.
    /// <para>组名：1 至 36 个 UTF-8 字节且不含 NUL，在所有组间唯一。</para>
    /// </param>
    /// <param name="maximumSize">
    /// Combined partition-size budget in bytes; <c>0</c> means unlimited.
    /// <para>分区合计大小预算（字节）；<c>0</c> 表示不限制。</para>
    /// </param>
    /// <param name="flags">
    /// Raw LP group flags stored unchanged.
    /// <para>原样保存的 LP 组标志。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name is invalid or already added.
    /// <para>名称无效或已添加。</para>
    /// </exception>
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
    /// <returns>
    /// A single-device <see cref="LpMetadata"/> with a synthetic <c>default</c> group when none was added, partition entries resolved to group indices, and extents appended in add order.
    /// <para>返回单设备的 <see cref="LpMetadata"/>：未添加组时会合成一个 <c>default</c> 组，分区条目已解析为组索引，extent 按添加顺序追加。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A partition references a missing group, or the partitions of a group exceed its nonzero maximum size.
    /// <para>分区引用了不存在的组，或某组成员分区的合计大小超出其非零上限。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The builder geometry fails validation.
    /// <para>构建器几何信息未通过校验。</para>
    /// </exception>
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
        /// <value>
        /// The validated ASCII name passed to <see cref="MetadataBuilder.AddPartition"/>.
        /// <para>即传给 <see cref="MetadataBuilder.AddPartition"/> 的已校验 ASCII 名称。</para>
        /// </value>
        public string Name { get; }

        /// <summary>Gets the group name. <para>获取组名称。</para></summary>
        /// <value>
        /// The owning group name, resolved to a group index when the metadata is built.
        /// <para>所属组名称，在构建元数据时解析为组索引。</para>
        /// </value>
        public string GroupName { get; }

        /// <summary>Gets partition attributes. <para>获取分区属性。</para></summary>
        /// <value>
        /// The raw LP attribute flags, copied unchanged into the built metadata.
        /// <para>原始 LP 属性标志，原样复制到构建出的元数据。</para>
        /// </value>
        public uint Attributes { get; }

        /// <summary>Gets the planned extents. <para>获取规划的 extent。</para></summary>
        /// <value>
        /// Linear extents on the sole block device; empty for a zero-sized partition.
        /// <para>位于唯一块设备上的线性 extent；零大小分区时为空。</para>
        /// </value>
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