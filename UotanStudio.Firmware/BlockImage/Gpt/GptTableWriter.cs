using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Serializes a <see cref="GptTable"/> into UEFI 1.0 bytes: a protective MBR (type <c>0xEE</c>), the
/// primary header and entry array after LBA 1, and the backup entry array and header before the last
/// block. Header CRCs cover the declared header size with the CRC field zeroed and the entry-array CRC
/// covers exactly <c>PartitionEntryCount * PartitionEntrySize</c> bytes, per the UEFI specification.
/// Writes always target a new file or a caller-provided stream and never modify a source image in place.
/// <para>将 <see cref="GptTable"/> 序列化为 UEFI 1.0 字节：保护性 MBR（类型 <c>0xEE</c>）、LBA 1 之后的主头部与条目数组，
/// 以及最末块之前的备份条目数组与头部。按 UEFI 规范，头部 CRC 覆盖声明的头部大小且计算时 CRC 字段清零，
/// 条目数组 CRC 精确覆盖 <c>PartitionEntryCount * PartitionEntrySize</c> 字节。写入始终面向新文件或调用方提供的流，
/// 绝不就地修改源镜像。</para>
/// </summary>
internal static class GptTableWriter
{
    private const uint MaximumEntries = 65536;
    private const uint MaximumEntrySize = 4096;
    private const ulong MaximumArrayBytes = 16 * 1024 * 1024;
    private const uint MinimumHeaderSize = 92;
    private const ulong MinimumReservedArrayBytes = 16384;
    private const ulong MaximumAttributes = 0x0000000000000007;

    /// <summary>
    /// Creates an unattached table with the canonical layout: primary array at LBA 2, backup array
    /// ending one block before the backup header, and usable ranges derived from the array size.
    /// <para>创建使用规范布局的独立表：主数组位于 LBA 2，备份数组结束于备份头部前一块，可用区间由数组大小推导。</para>
    /// </summary>
    /// <param name="diskId">
    /// Disk identifier written into both headers; <see cref="Guid.Empty"/> is rejected.
    /// <para>写入两个头部的磁盘标识符；拒绝 <see cref="Guid.Empty"/>。</para>
    /// </param>
    /// <param name="blockSize">
    /// Logical block size, a power of two from 512 through 65536.
    /// <para>逻辑块大小，须为 512 至 65536 之间 2 的幂。</para>
    /// </param>
    /// <param name="blockCount">
    /// Device capacity in blocks; must be at least 6 and fit signed stream addressing.
    /// <para>以块计的设备容量；至少为 6 且须能用有符号流偏移寻址。</para>
    /// </param>
    /// <param name="partitions">
    /// Entries in emit order; slot indexes are reassigned from the position in this list.
    /// <para>按输出顺序排列的条目；槽位索引按列表中的位置重新分配。</para>
    /// </param>
    /// <param name="entryCount">
    /// Total entry slots in each array, from 1 through 65536; defaults to the conventional 128.
    /// <para>每个数组的条目槽位总数，范围 1 至 65536；默认沿用常见的 128。</para>
    /// </param>
    /// <param name="entrySize">
    /// Bytes per entry slot, a power of two from 128 through 4096.
    /// <para>每个条目槽位的字节数，须为 128 至 4096 之间 2 的幂。</para>
    /// </param>
    /// <returns>
    /// A table whose byte ranges, layout fields and metadata fingerprint match what the writer emits.
    /// <para>返回的表，其字节范围、布局字段与元数据指纹均与写入器输出一致。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="diskId"/> is empty, or two entries share a unique GUID.
    /// <para><paramref name="diskId"/> 为空，或两个条目共享相同的唯一 GUID。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The geometry or entry dimensions are outside the supported range, or more partitions were supplied
    /// than there are entry slots.
    /// <para>几何或条目维度超出支持范围，或提供的分区数量超过条目槽位数。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A partition range, attribute bit or name (including embedded NUL) is not encodable, or ranges overlap.
    /// <para>分区范围、属性位或名称（含内嵌 NUL）不可编码，或范围存在重叠。</para>
    /// </exception>
    public static GptTable Create(Guid diskId, int blockSize, ulong blockCount,
        IReadOnlyList<GptPartition> partitions, uint entryCount = 128, uint entrySize = 128)
    {
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "The GPT block size must be a power of two from 512 through 65536.");
        }
        if (blockCount < 6 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount),
                "The GPT block count is below the minimum geometry or exceeds addressable capacity.");
        }
        if (entryCount is 0 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount),
                "The GPT entry count must be between 1 and 65536.");
        }
        if (entrySize is < 128 or > MaximumEntrySize || (entrySize & (entrySize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entrySize),
                "The GPT entry size must be a power of two from 128 through 4096.");
        }
        if (diskId == Guid.Empty)
        {
            throw new ArgumentException("The GPT disk identifier must not be empty.", nameof(diskId));
        }
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions.Count > entryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partitions),
                "The created GPT carries more partitions than declared entry slots.");
        }
        var arrayBytes = checked((ulong)entryCount * entrySize);
        if (arrayBytes > MaximumArrayBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount),
                "The GPT entry array exceeds the supported metadata limit.");
        }
        var arrayBlocks = (arrayBytes + (uint)blockSize - 1) / (uint)blockSize;
        var reservedBlocks = (Math.Max(arrayBytes, MinimumReservedArrayBytes) + (uint)blockSize - 1) / (uint)blockSize;
        if (reservedBlocks >= blockCount - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount),
                "The device capacity cannot reserve both GPT metadata areas.");
        }
        var firstUsable = 2 + reservedBlocks;
        var lastUsable = blockCount - 2 - reservedBlocks;

        var ids = new HashSet<Guid>();
        var rewritten = new List<GptPartition>(partitions.Count);
        for (var index = 0; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            ValidateEntryShape(partition, firstUsable, lastUsable);
            if (partition.TypeId == Guid.Empty)
            {
                throw new InvalidDataException("A created GPT partition must carry a type GUID.");
            }
            if (!ids.Add(partition.Id))
            {
                throw new ArgumentException($"Duplicate GPT partition GUID: {partition.Id}.", nameof(partitions));
            }
            rewritten.Add(partition with
            {
                Index = index,
                ByteOffset = checked((long)(partition.FirstLba * (uint)blockSize)),
                ByteLength = checked((long)((partition.LastLba - partition.FirstLba + 1) * (uint)blockSize))
            });
        }
        ValidateNoOverlap(rewritten);

        var layout = new Layout((uint)MinimumHeaderSize, firstUsable, lastUsable, entryCount, entrySize,
            2, blockCount - 1 - arrayBlocks);
        var table = new GptTable(diskId, blockSize, blockCount, rewritten, string.Empty,
            layout.HeaderSize, layout.FirstUsable, layout.LastUsable, layout.EntryCount, layout.EntrySize,
            layout.PrimaryArrayLba, layout.BackupArrayLba);
        var fingerprint = ComputeFingerprint(table);
        return new GptTable(diskId, blockSize, blockCount, [.. rewritten], fingerprint,
            layout.HeaderSize, layout.FirstUsable, layout.LastUsable, layout.EntryCount, layout.EntrySize,
            layout.PrimaryArrayLba, layout.BackupArrayLba);
    }

    /// <summary>
    /// Writes the protective MBR, both headers and both entry arrays into a seekable destination at
    /// the layout recorded by the table. The stream length is never changed and bytes outside the
    /// metadata regions are untouched.
    /// <para>按表记录的布局将保护性 MBR、两个头部与两个条目数组写入可寻址目标。不改变流长度，元数据区域之外的字节保持不动。</para>
    /// </summary>
    /// <param name="target">
    /// Caller-owned writable, seekable stream of at least <c>BlockCount * BlockSize</c> bytes; it is
    /// not flushed or closed and its position is restored on every exit.
    /// <para>调用方持有的可写、可寻址流，长度至少为 <c>BlockCount * BlockSize</c> 字节；不刷新也不关闭，
    /// 无论成功与否其位置都会恢复。</para>
    /// </param>
    /// <param name="table">
    /// Validated or created table; every invariant the reader enforces is re-checked here before any
    /// byte is written.
    /// <para>已读取或新创建的表；写入任何字节前会在此重新校验读取器强制的全部不变量。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked before each region write.
    /// <para>在每个区域写入前检查的取消令牌。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> cannot be written or seeked.
    /// <para><paramref name="target"/> 不可写或不可寻址。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The table violates a GPT invariant, or the stream is shorter than the declared capacity.
    /// <para>表违反 GPT 不变量，或流短于声明容量。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static void WriteRegions(Stream target, GptTable table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);
        if (!target.CanWrite || !target.CanSeek)
        {
            throw new ArgumentException("The GPT write target must be writable and seekable.", nameof(target));
        }
        var capacity = ValidateTable(table);
        if (target.Length < capacity)
        {
            throw new InvalidDataException("The GPT write target is shorter than the declared capacity.");
        }
        var arrayBytes = checked((ulong)table.PartitionEntryCount * table.PartitionEntrySize);
        var arrayBlocks = (arrayBytes + (uint)table.BlockSize - 1) / (uint)table.BlockSize;
        var entryArray = BuildEntryArray(table);
        var arrayCrc = Crc32Ieee.Compute(entryArray);
        var primaryHeader = BuildHeaderBlock(table, 1, table.BlockCount - 1, table.PrimaryEntryArrayLba, arrayCrc);
        var backupHeader = BuildHeaderBlock(table, table.BlockCount - 1, 1, table.BackupEntryArrayLba, arrayCrc);

        var original = target.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Position = 0;
            target.Write(BuildProtectiveMbr(table.BlockCount));
            if (table.BlockSize > 512)
            {
                target.Write(new byte[table.BlockSize - 512], 0, table.BlockSize - 512);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var arrayByteLength = checked((long)arrayBytes);
            var arrayPadding = checked((long)arrayBlocks * table.BlockSize) - arrayByteLength;
            target.Position = checked((long)table.PrimaryEntryArrayLba * table.BlockSize);
            target.Write(entryArray);
            WriteZeroRun(target, arrayPadding);
            target.Position = checked((long)table.BackupEntryArrayLba * table.BlockSize);
            target.Write(entryArray);
            WriteZeroRun(target, checked((long)(table.BlockCount - 1) * table.BlockSize)
                - checked((long)table.BackupEntryArrayLba * table.BlockSize) - arrayByteLength - arrayPadding);
            cancellationToken.ThrowIfCancellationRequested();
            target.Position = checked((long)1 * table.BlockSize);
            target.Write(primaryHeader);
            target.Position = checked((long)(table.BlockCount - 1) * table.BlockSize);
            target.Write(backupHeader);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            target.Position = original;
        }
    }

    /// <summary>
    /// Publishes a new disk image that contains only the table's metadata over a zero-filled
    /// capacity, using a temporary file and an atomic replacement of the destination.
    /// <para>发布仅包含表元数据、其余容量补零的新磁盘镜像，使用临时文件并原子替换目标。</para>
    /// </summary>
    /// <param name="targetPath">
    /// Destination image path; an existing file is replaced only after the whole image has been written.
    /// <para>目标镜像路径；已存在的文件仅在整张镜像完整写出后被替换。</para>
    /// </param>
    /// <param name="table">
    /// Validated or created table; its capacity fixes the produced file length.
    /// <para>已读取或新创建的表；其容量决定生成文件的长度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked before and during the writes; cancellation leaves the destination untouched.
    /// <para>在写入之前与期间检查的取消令牌；取消不会改动目标。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The table violates a GPT invariant.
    /// <para>表违反 GPT 不变量。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The destination or its temporary publication file cannot be written.
    /// <para>无法写入目标或其临时发布文件。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static void WriteNewImage(string targetPath, GptTable table, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(table);
        var capacity = ValidateTable(table);
        FileExtraction.WriteAtomic(targetPath, output =>
        {
            output.SetLength(capacity);
            WriteRegions(output, table, cancellationToken);
            output.Flush(flushToDisk: true);
        }, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Copies a base disk image and overwrites its GPT metadata regions with the table.
    /// <para>复制基础磁盘镜像并用该表覆写其 GPT 元数据区域。</para>
    /// </summary>
    /// <param name="baseImagePath">
    /// Source disk image; it is only read and never modified.
    /// <para>源磁盘镜像；仅读取，绝不修改。</para>
    /// </param>
    /// <param name="targetPath">
    /// Destination image path, replaced atomically only after the copy and metadata write succeed.
    /// <para>目标镜像路径；仅在复制与元数据写入成功后原子替换。</para>
    /// </param>
    /// <param name="table">
    /// Validated or created table describing the destination geometry.
    /// <para>描述目标几何的已读取或新创建的表。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked through the copy and the metadata writes.
    /// <para>在复制与元数据写入期间持续检查的取消令牌。</para>
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="baseImagePath"/> does not exist.
    /// <para><paramref name="baseImagePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The table violates a GPT invariant or its capacity differs from the base image length.
    /// <para>表违反 GPT 不变量，或其容量与基础镜像长度不一致。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The destination or its temporary publication file cannot be written.
    /// <para>无法写入目标或其临时发布文件。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static void ApplyToImageFile(string baseImagePath, string targetPath, GptTable table,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(table);
        var capacity = ValidateTable(table);
        var source = Path.GetFullPath(baseImagePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The GPT base image was not found.", source);
        }
        FileExtraction.WriteAtomic(targetPath, output =>
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            if (input.Length != capacity)
            {
                throw new InvalidDataException("The GPT table capacity does not match the base image length.");
            }
            Core.Streams.StreamCopy.CopyExactly(input, output, capacity, cancellationToken);
            WriteRegions(output, table, cancellationToken);
            output.Flush(flushToDisk: true);
        }, cancellationToken: cancellationToken);
    }

    private readonly record struct Layout(uint HeaderSize, ulong FirstUsable, ulong LastUsable,
        uint EntryCount, uint EntrySize, ulong PrimaryArrayLba, ulong BackupArrayLba);

    private static void ValidateEntryShape(GptPartition partition, ulong firstUsable, ulong lastUsable)
    {
        if (partition.Id == Guid.Empty)
        {
            throw new InvalidDataException("A created GPT partition must carry a unique GUID.");
        }
        if (partition.FirstLba > partition.LastLba || partition.FirstLba < firstUsable || partition.LastLba > lastUsable)
        {
            throw new InvalidDataException("A created GPT partition range is outside the usable area.");
        }
        if ((partition.Attributes & ~MaximumAttributes) != 0)
        {
            throw new InvalidDataException("Only the three low GPT attribute bits may be set.");
        }
        if (partition.Name.Contains('\0'))
        {
            throw new InvalidDataException("A GPT partition name may not contain an embedded NUL character.");
        }
        if (Encoding.Unicode.GetByteCount(partition.Name) > 72)
        {
            throw new InvalidDataException("A GPT partition name exceeds the 72-byte UTF-16 field.");
        }
    }

    private static void ValidateNoOverlap(IReadOnlyList<GptPartition> partitions)
    {
        var ordered = partitions.OrderBy(partition => partition.FirstLba).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].FirstLba <= ordered[index - 1].LastLba)
            {
                throw new InvalidDataException("Created GPT partitions overlap.");
            }
        }
    }

    private static string ComputeFingerprint(GptTable table)
    {
        var array = BuildEntryArray(table);
        var arrayCrc = Crc32Ieee.Compute(array);
        var primary = BuildHeaderBlock(table, 1, table.BlockCount - 1, table.PrimaryEntryArrayLba, arrayCrc);
        var backup = BuildHeaderBlock(table, table.BlockCount - 1, 1, table.BackupEntryArrayLba, arrayCrc);
        using var hash = Sha256.CreateAccumulator();
        hash.Append(Sha256.ComputeHash(primary));
        hash.Append(Sha256.ComputeHash(backup));
        hash.Append(array);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static long ValidateTable(GptTable table)
    {
        var blockSize = table.BlockSize;
        var blockCount = table.BlockCount;
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new InvalidDataException("The GPT block size must be a power of two from 512 through 65536.");
        }
        if (blockCount < 6 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
        {
            throw new InvalidDataException("The GPT block count is below the minimum geometry or exceeds addressable capacity.");
        }
        if (table.DiskId == Guid.Empty)
        {
            throw new InvalidDataException("The GPT disk identifier must not be empty.");
        }
        if (table.HeaderSize is < MinimumHeaderSize or > 65536 || table.HeaderSize > blockSize)
        {
            throw new InvalidDataException("The GPT header size is outside the block.");
        }
        if (table.PartitionEntryCount is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("The GPT entry count must be between 1 and 65536.");
        }
        if (table.PartitionEntrySize is < 128 or > MaximumEntrySize
            || (table.PartitionEntrySize & (table.PartitionEntrySize - 1)) != 0)
        {
            throw new InvalidDataException("The GPT entry size must be a power of two from 128 through 4096.");
        }
        var arrayBytes = checked((ulong)table.PartitionEntryCount * table.PartitionEntrySize);
        if (arrayBytes > MaximumArrayBytes)
        {
            throw new InvalidDataException("The GPT entry array exceeds the supported metadata limit.");
        }
        var arrayBlocks = (arrayBytes + (uint)blockSize - 1) / (uint)blockSize;
        var reservedBlocks = (Math.Max(arrayBytes, MinimumReservedArrayBytes) + (uint)blockSize - 1) / (uint)blockSize;
        if (reservedBlocks >= blockCount - 1)
        {
            throw new InvalidDataException("The GPT capacity cannot reserve both metadata areas.");
        }
        if (table.FirstUsableLba < 2 + reservedBlocks || table.LastUsableLba > blockCount - 2 - reservedBlocks
            || table.FirstUsableLba > table.LastUsableLba)
        {
            throw new InvalidDataException("The GPT usable range does not reserve both metadata areas.");
        }
        if (table.PrimaryEntryArrayLba < 2 || table.PrimaryEntryArrayLba >= blockCount
            || arrayBlocks > blockCount - table.PrimaryEntryArrayLba
            || table.PrimaryEntryArrayLba + arrayBlocks > table.FirstUsableLba)
        {
            throw new InvalidDataException("The GPT primary entry array overlaps the usable storage.");
        }
        if (table.BackupEntryArrayLba <= table.LastUsableLba || table.BackupEntryArrayLba >= blockCount
            || arrayBlocks > blockCount - table.BackupEntryArrayLba
            || table.BackupEntryArrayLba + arrayBlocks > blockCount - 1)
        {
            throw new InvalidDataException("The GPT backup entry array overlaps the usable storage or the backup header.");
        }

        var ids = new HashSet<Guid>();
        var slots = new HashSet<int>();
        foreach (var partition in table.Partitions)
        {
            ValidateEntryShape(partition, table.FirstUsableLba, table.LastUsableLba);
            if (partition.Index < 0 || partition.Index >= (int)table.PartitionEntryCount || !slots.Add(partition.Index))
            {
                throw new InvalidDataException("A GPT partition carries an out-of-range or duplicated slot index.");
            }
            if (!ids.Add(partition.Id))
            {
                throw new InvalidDataException($"Duplicate GPT partition GUID: {partition.Id}.");
            }
        }
        ValidateNoOverlap(table.Partitions);
        return checked((long)(blockCount * (uint)blockSize));
    }

    internal static byte[] BuildEntryArray(GptTable table)
    {
        var entrySize = (int)table.PartitionEntrySize;
        var array = new byte[checked((int)table.PartitionEntryCount * entrySize)];
        foreach (var partition in table.Partitions)
        {
            var entry = array.AsSpan(partition.Index * entrySize, entrySize);
            partition.TypeId.TryWriteBytes(entry[..16]);
            partition.Id.TryWriteBytes(entry[16..32]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], partition.FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], partition.LastLba);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[48..], partition.Attributes);
            Encoding.Unicode.GetBytes(partition.Name, entry.Slice(56, 72));
        }
        return array;
    }

    internal static byte[] BuildHeaderBlock(GptTable table, ulong myLba, ulong alternateLba, ulong arrayLba, uint arrayCrc)
    {
        var block = new byte[table.BlockSize];
        "EFI PART"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), table.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(24), myLba);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(32), alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(40), table.FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(48), table.LastUsableLba);
        table.DiskId.TryWriteBytes(block.AsSpan(56, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(72), arrayLba);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(80), table.PartitionEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(84), table.PartitionEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(88), arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16),
            Crc32Ieee.Compute(block.AsSpan(0, (int)table.HeaderSize)));
        return block;
    }

    internal static byte[] BuildProtectiveMbr(ulong blockCount)
    {
        var mbr = new byte[512];
        mbr[446] = 0x00;
        mbr[447] = 0x00;
        mbr[448] = 0x02;
        mbr[449] = 0x00;
        mbr[450] = 0xEE;
        mbr[451] = 0xFF;
        mbr[452] = 0xFF;
        mbr[453] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458),
            blockCount - 1 > uint.MaxValue ? uint.MaxValue : (uint)(blockCount - 1));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    private static void WriteZeroRun(Stream target, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, count);
            target.Write(buffer, 0, chunk);
            count -= chunk;
        }
    }
}