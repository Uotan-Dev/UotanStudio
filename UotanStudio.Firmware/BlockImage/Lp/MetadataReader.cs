using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BlockImage.Lp;

/// <summary>
/// Reads and validates Android super (dynamic partition) LP metadata: geometry blocks, metadata headers and the metadata tables.
/// <para>读取并校验 Android super（动态分区）的 LP 元数据：几何信息块、元数据头部以及元数据表。</para>
/// </summary>
/// <example>
/// <code>
/// using var image = MetadataReader.OpenImage("super.img");
/// LpMetadata metadata = MetadataReader.ReadFromImageStream(image);
/// </code>
/// </example>
public static class MetadataReader
{
    /// <summary>
    /// Opens the logical byte view of an image file, expanding sparse chunks while reading rather than materialising the image.
    /// Detection inspects only the first four bytes, so a shorter file is opened as a raw image.
    /// <para>打开镜像文件的逻辑字节视图，读取时按需展开稀疏数据块而不落盘展开镜像。格式识别仅检查前四个字节，因此更短的文件按原始镜像打开。</para>
    /// </summary>
    /// <param name="path">
    /// Image file path. A file whose first four bytes are <c>3A FF 26 ED</c>, the little-endian sparse magic <c>0xED26FF3A</c>, is treated as sparse.
    /// <para>镜像文件路径。前四个字节为 <c>3A FF 26 ED</c>（即小端序稀疏魔数 <c>0xED26FF3A</c>）的文件按稀疏镜像处理。</para>
    /// </param>
    /// <returns>
    /// A read-only seekable stream over the image's logical bytes: a <see cref="SparseReadStream"/> for sparse input, otherwise an
    /// asynchronous <see cref="FileStream"/> opened with <see cref="FileShare.Read"/> and a 64 KiB buffer. The caller owns the returned
    /// stream and must dispose it; disposing a <see cref="SparseReadStream"/> also releases the sparse model and its backing file handle.
    /// <para>覆盖镜像逻辑字节、只读且可寻址的流：稀疏输入为 <see cref="SparseReadStream"/>，否则为以 <see cref="FileShare.Read"/> 打开、缓冲区 64 KiB 的异步 <see cref="FileStream"/>。调用方拥有返回的流并必须释放它；释放 <see cref="SparseReadStream"/> 时同时释放稀疏模型及其底层文件句柄。</para>
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="path"/> does not exist. <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller lacks permission to read <paramref name="path"/>. <para>调用方没有读取 <paramref name="path"/> 的权限。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The file starts with the sparse magic but its header, chunk table or declared sizes fail sparse validation.
    /// <para>文件以稀疏魔数开头，但其头部、数据块表或声明的长度未通过稀疏校验。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The file ends before the structures its sparse headers declare. <para>文件在稀疏头部声明的结构结束之前就已结束。</para></exception>
    public static Stream OpenImage(string path)
    {
        var header = UotanStudio.Firmware.Core.FormatDetector.ReadHeader(path, 4);
        return header.AsSpan().SequenceEqual((ReadOnlySpan<byte>)[0x3a, 0xff, 0x26, 0xed])
            ? new SparseReadStream(SparseReader.FromImageFile(path))
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
    }

    internal const int MaximumMetadataBytes = 64 * 1024 * 1024;
    /// <summary>
    /// Reads LP metadata from an image file (uses slot 0 primary metadata).
    /// <para>从镜像文件读取 LP 元数据(使用槽 0 主元数据)。</para>
    /// </summary>
    /// <param name="path">
    /// Raw or sparse image file, opened through <see cref="OpenImage"/> and closed again before this call returns.
    /// <para>原始或稀疏镜像文件，由 <see cref="OpenImage"/> 打开，并在本调用返回前关闭。</para>
    /// </param>
    /// <returns>
    /// The validated geometry, header and metadata tables of the slot that parsed successfully.
    /// <para>成功解析槽位中已通过校验的几何信息、头部与元数据表。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// No geometry or metadata slot passes validation, as reported by <see cref="ReadFromImageStream"/>.
    /// <para>如同 <see cref="ReadFromImageStream"/> 所报告的那样，没有任何几何信息或元数据槽通过校验。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The image is shorter than the structures being read. <para>镜像长度不足以读取所需结构。</para></exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="path"/> does not exist. <para><paramref name="path"/> 不存在。</para></exception>
    public static LpMetadata ReadFromImageFile(string path)
    {
        using var stream = OpenImage(path);
        return ReadFromImageStream(stream);
    }

    /// <summary>
    /// Reads LP metadata from an image stream (uses slot 0 primary metadata).
    /// <para>从镜像流读取 LP 元数据(使用槽 0 主元数据)。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable stream over a super image or a standalone metadata region. The stream position is moved while geometry and metadata
    /// slots are probed and ends after the tables of the block that parsed successfully; the stream itself stays open and owned by the caller.
    /// <para>覆盖 super 镜像或独立元数据区域、可寻址的流。探测几何信息与元数据槽时会移动位置，解析成功后停留在所读数据块之后；流保持打开，所有权属于调用方。</para>
    /// </param>
    /// <returns>
    /// The validated geometry, header and metadata tables. Geometry is taken from the primary block at offset
    /// <c>4096</c>, then the backup block at offset <c>8192</c>; if both fail, the leading 4096 bytes are parsed as a geometry block and
    /// the metadata is read from offset <c>4096</c>. Metadata is taken from slot <c>0</c> primary first, then slot <c>0</c> backup.
    /// <para>已通过校验的几何信息、头部与元数据表。几何信息依次取自偏移 <c>4096</c> 的主块和偏移 <c>8192</c> 的备份块；两者均失败时，将起始 <c>4096</c> 字节按几何信息块解析，并从偏移 <c>4096</c> 读取元数据。元数据优先取自槽 <c>0</c> 主副本，其次取自槽 <c>0</c> 备份副本。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Every probed geometry and metadata block failed validation, or the fallback region itself is not a valid metadata block.
    /// <para>所有被探测的几何信息与元数据块均未通过校验，或回退区域本身不是有效的元数据块。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the structures of the last probed block. <para>流在最后一个被探测块所需结构结束之前就已结束。</para></exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> does not support seeking. <para><paramref name="stream"/> 不支持寻址。</para></exception>
    public static LpMetadata ReadFromImageStream(Stream stream)
    {
        LpMetadataGeometry geometry;
        try { ReadPrimaryGeometry(stream, out geometry); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            try { ReadBackupGeometry(stream, out geometry); }
            catch (Exception ex2) when (ex2 is InvalidDataException or EndOfStreamException)
            {
                ReadGeometry(stream, 0, out geometry);
                stream.Position = MetadataFormat.LP_METADATA_GEOMETRY_SIZE;
                return ParseMetadata(geometry, stream);
            }
        }
        try { return ReadPrimaryMetadata(stream, geometry, 0); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            stream.Position = GetBackupMetadataOffset(geometry, (ulong)stream.Length, 0);
            return ParseMetadata(geometry, stream);
        }
    }

    /// <summary>
    /// Parses the geometry structure from the beginning of an image stream.
    /// <para>从镜像流起始处解析几何结构。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable image stream; the reserved 4096-byte header block is skipped and reading starts at offset <c>4096</c>.
    /// <para>可寻址的镜像流；跳过保留的 4096 字节头部块，从偏移 <c>4096</c> 开始读取。</para>
    /// </param>
    /// <param name="geometry">
    /// The validated geometry on return: magic, structure size, SHA-256 checksum, per-slot metadata size, slot count and logical block size.
    /// Unset when the call throws.
    /// <para>返回时为已校验的几何信息：魔数、结构长度、SHA-256 校验和、每槽元数据大小、槽数量与逻辑块大小。抛出异常时不赋值。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The geometry magic or structure size is wrong, its dimensions are outside the supported limits, or the SHA-256 checksum computed
    /// over the structure with its checksum field zeroed does not match the stored value.
    /// <para>几何信息的魔数或结构长度错误，其尺寸超出支持范围，或在校验和字段置零后对该结构计算出的 SHA-256 与存储值不一致。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the geometry structure. <para>流在几何信息结构结束之前就已结束。</para></exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> does not support seeking. <para><paramref name="stream"/> 不支持寻址。</para></exception>
    public static void ReadPrimaryGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES, out geometry);

    /// <summary>
    /// Parses the backup geometry structure from an image stream.
    /// <para>从镜像流解析备份几何结构。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable image stream; reading starts at offset <c>8192</c>, the second geometry copy.
    /// <para>可寻址的镜像流；从偏移 <c>8192</c> 开始读取，即第二份几何信息副本。</para>
    /// </param>
    /// <param name="geometry">
    /// The validated backup geometry on return; unset when the call throws.
    /// <para>返回时为已校验的备份几何信息；抛出异常时不赋值。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The backup geometry magic, structure size, dimensions or SHA-256 checksum fail validation.
    /// <para>备份几何信息的魔数、结构长度、尺寸或 SHA-256 校验和未通过校验。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the backup geometry structure. <para>流在备份几何信息结构结束之前就已结束。</para></exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> does not support seeking. <para><paramref name="stream"/> 不支持寻址。</para></exception>
    public static void ReadBackupGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE, out geometry);

    private static void ReadGeometry(Stream stream, long offset, out LpMetadataGeometry geometry)
    {
        var buffer = new byte[MetadataFormat.Sizes.Geometry];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        geometry = LpMetadataGeometry.FromBytes(buffer);

        if (geometry.Magic != MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
        {
            throw new InvalidDataException("Invalid LP geometry magic.");
        }

        if (geometry.StructSize != MetadataFormat.Sizes.Geometry)
        {
            throw new InvalidDataException("Invalid LP geometry structure size.");
        }
        ValidateGeometry(geometry);
        var structSize = (int)geometry.StructSize;
        var structData = buffer.AsSpan(0, structSize).ToArray();
        structData.AsSpan(8, 32).Clear();
        var hash = Sha256.ComputeHash(structData);
        if (!hash.AsSpan().SequenceEqual(geometry.Checksum))
        {
            throw new InvalidDataException("LP geometry checksum mismatch.");
        }
    }

    /// <summary>
    /// Reads primary metadata for the given slot number.
    /// <para>读取指定槽号的主元数据。</para>
    /// </summary>
    /// <param name="stream">
    /// Seekable image stream; the position is moved to the slot's primary metadata offset before parsing.
    /// <para>可寻址的镜像流；解析前会将位置移动到该槽的主元数据偏移。</para>
    /// </param>
    /// <param name="geometry">
    /// Geometry describing the per-slot metadata size and slot count; validated again by <see cref="ParseMetadata"/>.
    /// <para>描述每槽元数据大小与槽数量的几何信息；由 <see cref="ParseMetadata"/> 再次校验。</para>
    /// </param>
    /// <param name="slotNumber">
    /// Zero-based metadata slot to read, which must be below <see cref="LpMetadataGeometry.MetadataSlotCount"/>.
    /// <para>要读取的从零开始的元数据槽，必须小于 <see cref="LpMetadataGeometry.MetadataSlotCount"/>。</para>
    /// </param>
    /// <returns>
    /// The validated header and metadata tables of that slot, with <paramref name="geometry"/> attached.
    /// <para>该槽已通过校验的头部与元数据表，并附带 <paramref name="geometry"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="slotNumber"/> is not below the geometry's slot count. <para><paramref name="slotNumber"/> 不小于几何信息中的槽数量。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The geometry dimensions are invalid, or the metadata block at the computed offset fails validation.
    /// <para>几何信息尺寸无效，或计算偏移处的元数据块未通过校验。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the metadata block. <para>流在元数据块结束之前就已结束。</para></exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> does not support seeking. <para><paramref name="stream"/> 不支持寻址。</para></exception>
    public static LpMetadata ReadPrimaryMetadata(Stream stream, LpMetadataGeometry geometry, uint slotNumber)
    {
        var offset = GetPrimaryMetadataOffset(geometry, slotNumber);
        stream.Position = offset;
        return ParseMetadata(geometry, stream);
    }

    /// <summary>
    /// Parses a metadata block (header + tables) from the current stream position.
    /// <para>从当前流位置解析元数据块(头部 + 表)。</para>
    /// </summary>
    /// <param name="geometry">
    /// Geometry that owns the block; its metadata size and slot count are re-validated and the per-slot ceiling is capped at
    /// <c>64</c> MiB, with a logical block size that is a power of two at least <c>512</c> bytes.
    /// <para>拥有该数据块的几何信息；其元数据大小与槽数量会被重新校验，每槽上限为 <c>64</c> MiB，逻辑块大小必须是不小于 <c>512</c> 字节的 2 的幂。</para>
    /// </param>
    /// <param name="stream">
    /// Readable stream positioned at the first byte of a metadata header. The header is read little-endian. Reading advances the
    /// position past the header and its tables; seeking is optional here, and a non-seekable stream is bounded only by the geometry.
    /// <para>定位在元数据头部第一字节、可读的流。头部按小端序读取。读取会把位置推进到头部及其数据表之后；此处不强制要求可寻址，不可寻址的流只受几何信息约束。</para>
    /// </param>
    /// <returns>
    /// The parsed block: the supplied geometry, the validated header, and the partition, extent, group and block-device tables. A
    /// table descriptor with zero entries yields an empty list, while the block-device table must hold at least one super device at index <c>0</c>.
    /// <para>解析后的数据块：传入的几何信息、已校验的头部，以及分区、范围、分区组和块设备四张表。条目数为零的描述符得到空列表，而块设备表必须在索引 <c>0</c> 处至少包含一个 super 设备。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The geometry dimensions are invalid; the header magic, major version (must be <c>10</c>), minor version (must be at most <c>2</c>)
    /// or header size (must be <c>128</c> for minor version below <c>2</c> and <c>256</c> for minor version <c>2</c>) are rejected; the header
    /// or table byte counts exceed the slot or the remaining seekable stream length; a SHA-256 header or table checksum does not match;
    /// a table descriptor entry size or range is wrong; the super device data area overlaps the metadata; or partition attributes, group
    /// and extent references, extent targets or their device bounds fall outside what the block declares. Partition attributes are masked
    /// with <c>0x3</c> for minor version <c>0</c> and <c>0xf</c> for later versions.
    /// <para>以下情形均抛出：几何信息尺寸无效；头部魔数、主版本（必须为 <c>10</c>）、次版本（不得大于 <c>2</c>）或头部长度（次版本小于 <c>2</c> 时必须为 <c>128</c>，次版本为 <c>2</c> 时必须为 <c>256</c>）被拒绝；头部或数据表字节数超出该槽或可寻址流剩余长度；SHA-256 头部或数据表校验和不匹配；表描述符的条目长度或范围错误；super 设备数据区域与元数据重叠；分区的属性、组与范围引用、范围目标或其设备边界超出数据块声明的范围。次版本为 <c>0</c> 时属性掩码为 <c>0x3</c>，更高版本为 <c>0xf</c>。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the header, the extended header or the declared tables have been read.
    /// <para>流在头部、扩展头部或声明的数据表读取完成之前就已结束。</para></exception>
    public static LpMetadata ParseMetadata(LpMetadataGeometry geometry, Stream stream)
    {
        ValidateGeometry(geometry);
        var headerBuffer = new byte[MetadataFormat.Sizes.LegacyHeader];
        stream.ReadExactly(headerBuffer);
        var header = LpMetadataHeader.FromBytes(headerBuffer);

        if (header.Magic != MetadataFormat.LP_METADATA_HEADER_MAGIC)
        {
            throw new InvalidDataException("Invalid LP metadata header magic.");
        }

        var headerSize = header.MinorVersion >= 2 ? MetadataFormat.Sizes.Header : MetadataFormat.Sizes.LegacyHeader;
        if (header.MajorVersion != MetadataFormat.LP_METADATA_MAJOR_VERSION || header.MinorVersion > 2 || header.HeaderSize != headerSize)
        {
            throw new InvalidDataException("Unsupported LP metadata header version or size.");
        }
        if (headerSize > headerBuffer.Length)
        {
            Array.Resize(ref headerBuffer, headerSize);
            stream.ReadExactly(headerBuffer.AsSpan(MetadataFormat.Sizes.LegacyHeader));
            header = LpMetadataHeader.FromBytes(headerBuffer);
        }
        if (header.HeaderSize > geometry.MetadataMaxSize || header.TablesSize > geometry.MetadataMaxSize - header.HeaderSize
            || (stream.CanSeek && header.TablesSize > stream.Length - stream.Position))
        {
            throw new InvalidDataException("LP metadata tables exceed their slot or input bounds.");
        }

        var headerHashData = headerBuffer.AsSpan(0, (int)Math.Min(header.HeaderSize, headerBuffer.Length)).ToArray();
        headerHashData.AsSpan(12, 32).Clear();
        var headerHash = Sha256.ComputeHash(headerHashData);
        if (!headerHash.AsSpan().SequenceEqual(header.HeaderChecksum))
        {
            throw new InvalidDataException("LP metadata header checksum mismatch.");
        }

        var tablesBuffer = new byte[header.TablesSize];
        stream.ReadExactly(tablesBuffer);
        var tablesHash = Sha256.ComputeHash(tablesBuffer);
        if (!tablesHash.AsSpan().SequenceEqual(header.TablesChecksum))
        {
            throw new InvalidDataException("LP metadata tables checksum mismatch.");
        }

        var metadata = new LpMetadata { Geometry = geometry, Header = header };
        ParseTable(tablesBuffer, header.Partitions, MetadataFormat.Sizes.Partition, metadata.Partitions, LpMetadataPartition.FromBytes);
        ParseTable(tablesBuffer, header.Extents, MetadataFormat.Sizes.Extent, metadata.Extents, LpMetadataExtent.FromBytes);
        ParseTable(tablesBuffer, header.Groups, MetadataFormat.Sizes.PartitionGroup, metadata.Groups, LpMetadataPartitionGroup.FromBytes);
        ParseTable(tablesBuffer, header.BlockDevices, MetadataFormat.Sizes.BlockDevice, metadata.BlockDevices, LpMetadataBlockDevice.FromBytes);
        ValidateBlockDevices(metadata);
        foreach (var partition in metadata.Partitions)
        {
            var validAttributes = header.MinorVersion >= 1 ? 0xfu : 0x3u;
            if ((partition.Attributes & ~validAttributes) != 0)
                throw new InvalidDataException("LP partition attributes are invalid for the metadata version.");
            if (partition.GroupIndex >= metadata.Groups.Count || partition.FirstExtentIndex > metadata.Extents.Count
                || partition.NumExtents > metadata.Extents.Count - partition.FirstExtentIndex)
                throw new InvalidDataException("LP partition references an invalid group or extent range.");
        }
        foreach (var extent in metadata.Extents)
        {
            if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO) continue;
            if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource >= metadata.BlockDevices.Count)
                throw new InvalidDataException("LP extent references an invalid target.");
            var device = metadata.BlockDevices[(int)extent.TargetSource];
            var sectors = device.Size / MetadataFormat.LP_SECTOR_SIZE;
            if (extent.TargetData < device.FirstLogicalSector || extent.TargetData > sectors || extent.NumSectors > sectors - extent.TargetData)
                throw new InvalidDataException("LP extent exceeds the target device data area.");
        }
        return metadata;
    }

    internal static void ValidateBlockDevices(LpMetadata metadata)
    {
        ValidateGeometry(metadata.Geometry);
        if (metadata.BlockDevices.Count == 0)
            throw new InvalidDataException("LP metadata does not specify a super device.");
        var superDevice = metadata.BlockDevices[0];
        var metadataBytes = checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + 2UL * MetadataFormat.LP_METADATA_GEOMETRY_SIZE
            + 2UL * metadata.Geometry.MetadataMaxSize * metadata.Geometry.MetadataSlotCount);
        var firstUsableSector = metadataBytes / MetadataFormat.LP_SECTOR_SIZE;
        if (superDevice.FirstLogicalSector < firstUsableSector
            || superDevice.FirstLogicalSector > superDevice.Size / MetadataFormat.LP_SECTOR_SIZE)
            throw new InvalidDataException("LP super device data area overlaps metadata or exceeds the device size.");
    }

    private static void ParseTable<T>(byte[] buffer, LpMetadataTableDescriptor desc, uint expectedSize, List<T> list, Func<ReadOnlySpan<byte>, T> fromBytes)
    {
        if (desc.EntrySize != expectedSize || desc.Offset > buffer.Length
            || (ulong)desc.NumEntries * desc.EntrySize > (ulong)buffer.Length - desc.Offset)
        {
            throw new InvalidDataException("Invalid LP table descriptor.");
        }
        if (desc.NumEntries == 0)
        {
            return;
        }
        for (uint i = 0; i < desc.NumEntries; i++)
        {
            var offset = checked((int)((ulong)desc.Offset + ((ulong)i * desc.EntrySize)));
            if (offset + desc.EntrySize > buffer.Length)
            {
                throw new InvalidDataException("LP table entry exceeds table buffer.");
            }
            list.Add(fromBytes(buffer.AsSpan(offset, (int)desc.EntrySize)));
        }
    }

    /// <summary>
    /// Calculates the byte offset of the primary metadata for a given slot.
    /// <para>计算给定槽号的主元数据字节偏移。</para>
    /// </summary>
    /// <param name="geometry">
    /// Validated geometry supplying the per-slot metadata size and the slot count.
    /// <para>已校验的几何信息，提供每槽元数据大小与槽数量。</para>
    /// </param>
    /// <param name="slotNumber">
    /// Zero-based metadata slot, which must be below <see cref="LpMetadataGeometry.MetadataSlotCount"/>.
    /// <para>从零开始的元数据槽，必须小于 <see cref="LpMetadataGeometry.MetadataSlotCount"/>。</para>
    /// </param>
    /// <returns>
    /// Absolute byte offset <c>4096 + 2 * 4096 + slotNumber * MetadataMaxSize</c>, i.e. past the reserved block and both geometry copies.
    /// <para>绝对字节偏移 <c>4096 + 2 * 4096 + slotNumber * MetadataMaxSize</c>，即位于保留块与两份几何信息副本之后。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <paramref name="geometry"/> has metadata sizes, slot counts or a logical block size outside the supported limits.
    /// <para><paramref name="geometry"/> 的元数据大小、槽数量或逻辑块大小超出支持范围。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="slotNumber"/> is not below the geometry's slot count. <para><paramref name="slotNumber"/> 不小于几何信息中的槽数量。</para></exception>
    public static long GetPrimaryMetadataOffset(LpMetadataGeometry geometry, uint slotNumber)
    {
        ValidateGeometry(geometry);
        if (slotNumber >= geometry.MetadataSlotCount) throw new ArgumentOutOfRangeException(nameof(slotNumber));
        return checked(MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2) + ((long)slotNumber * geometry.MetadataMaxSize));
    }

    /// <summary>
    /// Calculates the backup slot offset following the primary metadata slots.
    /// <para>计算位于全部主元数据槽之后的备份槽偏移。</para>
    /// </summary>
    /// <param name="geometry">
    /// Validated geometry supplying the per-slot metadata size and the slot count.
    /// <para>已校验的几何信息，提供每槽元数据大小与槽数量。</para>
    /// </param>
    /// <param name="deviceSize">
    /// Total size of the image or super device in bytes, used to reject a backup slot that would not fit.
    /// <para>镜像或 super 设备的总字节大小，用于判定放不下备份槽的情形。</para>
    /// </param>
    /// <param name="slotNumber">
    /// Zero-based metadata slot, which must be below <see cref="LpMetadataGeometry.MetadataSlotCount"/>.
    /// <para>从零开始的元数据槽，必须小于 <see cref="LpMetadataGeometry.MetadataSlotCount"/>。</para>
    /// </param>
    /// <returns>
    /// Absolute byte offset of the slot's backup copy, <c>GetPrimaryMetadataOffset(...) + MetadataMaxSize * MetadataSlotCount</c>, which
    /// always leaves a full <c>MetadataMaxSize</c> of bytes inside <paramref name="deviceSize"/>.
    /// <para>该槽备份副本的绝对字节偏移，即 <c>GetPrimaryMetadataOffset(...) + MetadataMaxSize * MetadataSlotCount</c>；在 <paramref name="deviceSize"/> 内必定留有完整的 <c>MetadataMaxSize</c> 字节。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// <paramref name="geometry"/> is outside the supported limits, or the backup slot starts beyond <paramref name="deviceSize"/> or is
    /// truncated by it. <para><paramref name="geometry"/> 超出支持范围，或备份槽起点超出 <paramref name="deviceSize"/>、被其截断。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="slotNumber"/> is not below the geometry's slot count. <para><paramref name="slotNumber"/> 不小于几何信息中的槽数量。</para></exception>
    public static long GetBackupMetadataOffset(LpMetadataGeometry geometry, ulong deviceSize, uint slotNumber)
    {
        var offset = checked(GetPrimaryMetadataOffset(geometry, slotNumber) + (long)geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        if ((ulong)offset > deviceSize || geometry.MetadataMaxSize > deviceSize - (ulong)offset)
            throw new InvalidDataException("LP backup metadata exceeds the device bounds.");
        return offset;
    }

    internal static void ValidateGeometry(LpMetadataGeometry geometry)
    {
        if (geometry.MetadataMaxSize < MetadataFormat.Sizes.LegacyHeader || geometry.MetadataMaxSize > MaximumMetadataBytes
            || geometry.MetadataMaxSize % MetadataFormat.LP_SECTOR_SIZE != 0 || geometry.MetadataSlotCount is 0 or > 32
            || geometry.LogicalBlockSize < MetadataFormat.LP_SECTOR_SIZE || (geometry.LogicalBlockSize & (geometry.LogicalBlockSize - 1)) != 0)
            throw new InvalidDataException("Invalid or excessive LP geometry dimensions.");
    }
}