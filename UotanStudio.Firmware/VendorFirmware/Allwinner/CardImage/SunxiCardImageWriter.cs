using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// One partition to lay into an Allwinner SD-card image: its GPT slot identity, the sector range the vendor table
/// declares, and the payload file that fills it.
/// <para>要排入全志 SD 卡镜像的一个分区：其 GPT 槽位标识、厂商表声明的扇区范围，以及填充它的负载文件。</para>
/// </summary>
/// <param name="Name">GPT partition name, which the vendor boot flow resolves by name.
/// <para>GPT 分区名；厂商引导流程按名称解析。</para></param>
/// <param name="FirstSector">First 512-byte sector of the slot. <para>槽位的首个 512 字节扇区。</para></param>
/// <param name="SectorCount">Slot capacity in 512-byte sectors. <para>槽位容量，以 512 字节扇区计数。</para></param>
/// <param name="TypeId">Partition type GUID written into the entry. <para>写入条目的分区类型 GUID。</para></param>
/// <param name="SourcePath">Absolute path of the payload file, or <see langword="null"/> to leave the slot zero-filled.
/// <para>负载文件的绝对路径；为 <see langword="null"/> 时该槽位保持零填充。</para></param>
public sealed record SunxiCardWritePartition(string Name, ulong FirstSector, ulong SectorCount, Guid TypeId,
    string? SourcePath);

/// <summary>
/// Everything one card-image build needs beyond the partition list: the boot region the vendor boot ROM reads and
/// the capacity the image declares.
/// <para>除分区清单之外，一次卡镜像构建所需的全部输入：厂商引导 ROM 读取的引导区，以及镜像声明的容量。</para>
/// </summary>
/// <param name="FirstStageLoaderPath">Path of the eGON first-stage loader (<c>boot0</c>), or <see langword="null"/> to
/// leave the loader slot empty. <para>eGON 一级引导程序（<c>boot0</c>）的路径；为 <see langword="null"/> 时引导槽位留空。</para></param>
/// <param name="BootPackagePath">Path of the packed <c>sunxi-package</c> boot package (<c>boot1</c>), or
/// <see langword="null"/> when absent. <para>打包的 <c>sunxi-package</c> 启动包（<c>boot1</c>）路径；不存在时为 <see langword="null"/>。</para></param>
/// <param name="BlockCount">Image capacity in 512-byte blocks; must cover every partition and both GPT copies.
/// <para>镜像容量，以 512 字节块计数；必须覆盖所有分区与两份 GPT。</para></param>
public sealed record SunxiCardBuildInput(string? FirstStageLoaderPath, string? BootPackagePath, ulong BlockCount);

/// <summary>
/// Outcome of one card-image build. <para>一次卡镜像构建的结果。</para>
/// </summary>
/// <param name="ImageBytes">Total image length in bytes. <para>镜像总字节长度。</para></param>
/// <param name="PartitionCount">GPT entries published. <para>已发布的分区条目数。</para></param>
/// <param name="BootPackageOffset">Byte offset the boot package was written at, or <see langword="null"/> when the
/// build carried none. <para>启动包写入的字节偏移；本次构建未携带启动包时为 <see langword="null"/>。</para></param>
public sealed record SunxiCardBuildResult(long ImageBytes, int PartitionCount, long? BootPackageOffset);

/// <summary>
/// Assembles an Allwinner SD-card image the vendor boot ROM accepts: a protective MBR, a GPT pair whose first usable
/// block sits past the boot region, an eGON first-stage loader at LBA 16, an optional <c>sunxi-package</c> boot
/// package before the first usable block, and one payload per partition. The layout is the one
/// <see cref="SunxiCardImageFormat"/> reads back, so a build round-trips through the parser.
/// <para>组装厂商引导 ROM 接受的全志 SD 卡镜像：保护性 MBR、首个可用块位于引导区之后的 GPT 主备对、LBA 16 处的
/// eGON 一级引导程序、可选且位于首个可用块之前的 <c>sunxi-package</c> 启动包，以及每个分区各一份负载。布局与
/// <see cref="SunxiCardImageFormat"/> 回读的布局一致，因此构建结果能被解析器往返读取。</para>
/// </summary>
/// <remarks>
/// The platform GPT writer cannot be reused here: it derives the first usable block from the entry array
/// (<c>2 + reservedBlocks</c>), which would place partitions on top of the boot region. This writer therefore emits
/// the GPT itself and pins the first usable block past the vendor boot region.
/// <para>
/// 此处不能复用平台 GPT 写入器：它由条目数组推导首个可用块（<c>2 + reservedBlocks</c>），会把分区放到引导区之上。
/// 因此本写入器自行写出 GPT，并把首个可用块固定在厂商引导区之后。
/// </para>
/// </remarks>
public static class SunxiCardImageWriter
{
    /// <summary>Logical block size of every Allwinner card image. <para>全志卡镜像的逻辑块大小。</para></summary>
    public const int BlockSize = SunxiCardImageFormat.BlockSize;

    /// <summary>Byte offset the vendor boot ROM reads the first-stage loader from, which is LBA 16.
    /// <para>厂商引导 ROM 读取一级引导程序的字节偏移，即 LBA 16。</para></summary>
    public const long FirstStageLoaderOffset = SunxiCardImageFormat.EgonOffset;

    /// <summary>Byte length of the first-stage loader slot. <para>一级引导程序槽位的字节长度。</para></summary>
    public const long FirstStageLoaderLength = SunxiCardImageFormat.EgonLength;

    /// <summary>First usable logical block of every image this writer produces; it clears the vendor boot region.
    /// <para>本写入器产出的每个镜像的首个可用逻辑块；它避开了厂商引导区。</para></summary>
    public const ulong FirstUsableBlock = 34_816;

    /// <summary>Logical block the boot package is written at when one is supplied.
    /// <para>提供启动包时其写入的逻辑块。</para></summary>
    public const ulong BootPackageBlock = 32_800;

    /// <summary>Partition-entry capacity of the emitted GPT. <para>产出 GPT 的分区条目容量。</para></summary>
    private const int EntryCapacity = 128;

    /// <summary>Logical block the primary partition-entry array starts at. LBA 2 would overlap the eGON loader at
    /// LBA 16, so the vendor layout places the array at LBA 2048 like its own card images do.
    /// <para>主分区条目数组起始的逻辑块。LBA 2 会与 LBA 16 的 eGON 引导程序重叠，因此厂商布局像其自家卡镜像一样
    /// 把数组放在 LBA 2048。</para></summary>
    public const ulong EntryArrayBlock = 2048;

    /// <summary>Byte length of one partition entry. <para>单个分区条目的字节长度。</para></summary>
    private const int EntrySize = 128;

    /// <summary>Upper bound for one partition payload, so a hostile file cannot drive an unbounded write.
    /// <para>单个分区负载的上限，避免恶意文件驱动无界写入。</para></summary>
    private const long MaximumPayloadBytes = 512L << 30;

    private static readonly Guid DefaultTypeId = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    /// <summary>
    /// Builds the card image at <paramref name="destinationPath"/>. The file is published whole: a failure leaves no
    /// truncated image behind.
    /// <para>在 <paramref name="destinationPath"/> 构建卡镜像。文件整体发布：失败不会留下截断的镜像。</para>
    /// </summary>
    /// <param name="input">Boot region paths and declared capacity. <para>引导区路径与声明的容量。</para></param>
    /// <param name="partitions">Partitions in table order; slots must fit the declared capacity and must not overlap.
    /// <para>按表顺序排列的分区；槽位必须落在声明容量内且互不重叠。</para></param>
    /// <param name="destinationPath">Destination image path. <para>目标镜像路径。</para></param>
    /// <param name="progress">Optional callback reporting the partition name and cumulative bytes copied.
    /// <para>可选回调，报告当前分区名与累计已复制字节。</para></param>
    /// <param name="cancellationToken">Cancels between and during writes. <para>在写入之间与写入期间取消。</para></param>
    /// <returns>What the build produced. <para>构建产出。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="partitions"/> is
    /// <see langword="null"/>. <para><paramref name="input"/> 或 <paramref name="partitions"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The destination path is blank. <para>目标路径为空白。</para></exception>
    /// <exception cref="InvalidDataException">A slot is out of range, two slots overlap, the loader is too large for its
    /// slot, the boot package collides with partitions, or a payload exceeds its slot.
    /// <para>槽位越界、两个槽位重叠、引导程序超出其槽位、启动包与分区冲突，或负载超出其槽位。</para></exception>
    /// <exception cref="IOException">A source file is missing or the image cannot be written.
    /// <para>源文件缺失或镜像无法写入。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static SunxiCardBuildResult Build(SunxiCardBuildInput input,
        IReadOnlyList<SunxiCardWritePartition> partitions, string destinationPath,
        IProgress<(string Partition, long BytesCopied)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var blockCount = input.BlockCount;
        var totalBytes = checked((long)blockCount * BlockSize);
        ValidateLayout(input, partitions, totalBytes);

        var staging = destinationPath + ".uotan-tmp";
        try
        {
            using (var image = new FileStream(staging, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                image.SetLength(totalBytes);
                WriteProtectiveMbr(image);
                var loaderOffset = WriteFirstStageLoader(image, input, cancellationToken);
                _ = loaderOffset;
                var packageOffset = WriteBootPackage(image, input, totalBytes, cancellationToken);
                WriteGpt(image, blockCount, partitions, cancellationToken);
                WritePayloads(image, partitions, progress, cancellationToken);
                image.Flush(flushToDisk: true);
            }

            File.Move(staging, destinationPath, overwrite: true);
            return new SunxiCardBuildResult(totalBytes, partitions.Count,
                input.BootPackagePath is null ? null : (long)BootPackageBlock * BlockSize);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Verifies every slot and the boot region fit, and that the GPT metadata areas do not overlap partition data.
    /// <para>校验每个槽位与引导区都放得下，且 GPT 元数据区不与分区数据重叠。</para>
    /// </summary>
    private static void ValidateLayout(SunxiCardBuildInput input, IReadOnlyList<SunxiCardWritePartition> partitions,
        long totalBytes)
    {
        if (totalBytes <= 0 || (ulong)(totalBytes / BlockSize) < FirstUsableBlock + 34)
        {
            throw new InvalidDataException(
                $"The declared capacity of {totalBytes} bytes cannot hold the boot region and both GPT copies.");
        }

        var sorted = partitions.OrderBy(partition => partition.FirstSector).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            var partition = sorted[index];
            if (partition.SectorCount == 0)
            {
                throw new InvalidDataException($"Partition '{partition.Name}' declares no sectors.");
            }

            if (partition.FirstSector < FirstUsableBlock)
            {
                throw new InvalidDataException(
                    $"Partition '{partition.Name}' starts at sector {partition.FirstSector}, inside the vendor boot region that ends at {FirstUsableBlock}.");
            }

            var end = checked(partition.FirstSector + partition.SectorCount);
            if (end > (ulong)(totalBytes / BlockSize) - 34)
            {
                throw new InvalidDataException(
                    $"Partition '{partition.Name}' ends at sector {end}, past the image's last usable sector.");
            }

            if (index > 0)
            {
                var previous = sorted[index - 1];
                if (partition.FirstSector < previous.FirstSector + previous.SectorCount)
                {
                    throw new InvalidDataException(
                        $"Partitions '{previous.Name}' and '{partition.Name}' overlap.");
                }
            }
        }

        if (input.FirstStageLoaderPath is { } loaderPath)
        {
            var length = new FileInfo(loaderPath).Length;
            if (length > FirstStageLoaderLength)
            {
                throw new InvalidDataException(
                    $"The first-stage loader is {length} bytes, above its {FirstStageLoaderLength}-byte slot.");
            }
        }

        if (input.BootPackagePath is { } packagePath)
        {
            var length = new FileInfo(packagePath).Length;
            var packageStart = (long)BootPackageBlock * BlockSize;
            var packageEnd = checked(packageStart + length);
            if (packageEnd > totalBytes)
            {
                throw new InvalidDataException(
                    $"The boot package ends at offset {packageEnd}, past the {totalBytes}-byte image.");
            }

            foreach (var partition in sorted)
            {
                var start = (long)partition.FirstSector * BlockSize;
                if (packageStart < start + (long)partition.SectorCount * BlockSize && start < packageEnd)
                {
                    throw new InvalidDataException(
                        $"The boot package collides with partition '{partition.Name}'.");
                }
            }
        }
    }

    private static void WriteProtectiveMbr(FileStream image)
    {
        Span<byte> sector = stackalloc byte[512];
        sector[510] = 0x55;
        sector[511] = 0xAA;
        sector[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[(446 + 8)..], 1);
        image.Position = 0;
        image.Write(sector);
    }

    private static long? WriteFirstStageLoader(FileStream image, SunxiCardBuildInput input,
        CancellationToken cancellationToken)
    {
        if (input.FirstStageLoaderPath is not { } path)
        {
            return null;
        }

        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.SequentialScan);
        image.Position = FirstStageLoaderOffset;
        source.CopyTo(image);
        return FirstStageLoaderOffset;
    }

    private static long? WriteBootPackage(FileStream image, SunxiCardBuildInput input, long totalBytes,
        CancellationToken cancellationToken)
    {
        if (input.BootPackagePath is not { } path)
        {
            return null;
        }

        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.SequentialScan);
        image.Position = (long)BootPackageBlock * BlockSize;
        source.CopyTo(image);
        return image.Position;
    }

    private static void WritePayloads(FileStream image, IReadOnlyList<SunxiCardWritePartition> partitions,
        IProgress<(string Partition, long BytesCopied)>? progress, CancellationToken cancellationToken)
    {
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slotBytes = checked((long)partition.SectorCount * BlockSize);
            if (partition.SourcePath is not { } path)
            {
                continue;
            }

            var length = new FileInfo(path).Length;
            if (length > slotBytes)
            {
                throw new InvalidDataException(
                    $"The payload for partition '{partition.Name}' is {length} bytes, above its {slotBytes}-byte slot.");
            }

            if (length > MaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    $"The payload for partition '{partition.Name}' exceeds the {MaximumPayloadBytes}-byte limit.");
            }

            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                FileOptions.SequentialScan);
            image.Position = checked((long)partition.FirstSector * BlockSize);
            var buffer = new byte[1 << 20];
            long copied = 0;
            progress?.Report((partition.Name, 0));
            while (copied < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, length - copied);
                var read = source.Read(buffer, 0, want);
                if (read <= 0)
                {
                    break;
                }

                image.Write(buffer, 0, read);
                copied += read;
                progress?.Report((partition.Name, copied));
            }

            if (copied < length)
            {
                throw new EndOfStreamException(
                    $"The payload for partition '{partition.Name}' ended after {copied} of {length} bytes.");
            }
        }
    }

    private static void WriteGpt(FileStream image, ulong blockCount,
        IReadOnlyList<SunxiCardWritePartition> partitions, CancellationToken cancellationToken)
    {
        var lastUsable = blockCount - 34;
        var entries = new byte[EntryCapacity * EntrySize];
        for (var index = 0; index < partitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = partitions[index];
            var entry = entries.AsSpan(index * EntrySize, EntrySize);
            var typeId = partition.TypeId == Guid.Empty ? DefaultTypeId : partition.TypeId;
            typeId.TryWriteBytes(entry, bigEndian: false, out _);
            DeriveUniqueId(partition.Name).TryWriteBytes(entry[16..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], partition.FirstSector);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], partition.FirstSector + partition.SectorCount - 1);
            Encoding.Unicode.GetBytes(partition.Name).CopyTo(entry[56..]);
        }

        var entriesCrc = Crc32Ieee.Compute(entries);
        var backupArrayBlock = checked(blockCount - 33);
        var primaryHeader = BuildHeader(1, blockCount - 1, lastUsable, EntryArrayBlock, entriesCrc);
        var backupHeader = BuildHeader(blockCount - 1, 1, lastUsable, backupArrayBlock, entriesCrc);

        image.Position = BlockSize;
        image.Write(primaryHeader);
        image.Position = checked((long)EntryArrayBlock * BlockSize);
        image.Write(entries);
        image.Position = checked((long)backupArrayBlock * BlockSize);
        image.Write(entries);
        image.Position = checked((long)(blockCount - 1) * BlockSize);
        image.Write(backupHeader);
    }

    /// <summary>
    /// Builds one GPT header block with the given geometry. The first usable block is this writer's own constant, so
    /// partitions never land on the vendor boot region.
    /// <para>按给定几何构建一个 GPT 头部块。首个可用块使用本写入器自身的常量，使分区永远不会落在厂商引导区上。</para>
    /// </summary>
    private static byte[] BuildHeader(ulong myLba, ulong alternateLba, ulong lastUsable, ulong entriesLba,
        uint entriesCrc)
    {
        var block = new byte[BlockSize];
        "EFI PART"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 0x0001_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), 92);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(24), myLba);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(32), alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(40), FirstUsableBlock);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(48), lastUsable);
        DeriveDiskId().TryWriteBytes(block.AsSpan(56), bigEndian: false, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(72), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(80), EntryCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(84), EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(88), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), Crc32Ieee.Compute(block.AsSpan(0, 92)));
        return block;
    }

    /// <summary>
    /// Derives a stable disk identifier so repeated builds of the same material produce identical headers.
    /// <para>推导稳定的磁盘标识符，使同一材料的重复构建产出完全一致的头部。</para>
    /// </summary>
    private static Guid DeriveDiskId() => new("15536006-38B1-315B-87DD-BA2DF3034625");

    /// <summary>
    /// Derives a deterministic unique partition GUID from the partition name. This is an integrity label, not a
    /// security claim. <para>由分区名推导确定性的唯一分区 GUID。该标识是完整性标记，不构成安全声明。</para>
    /// </summary>
    private static Guid DeriveUniqueId(string name)
    {
        var digest = UotanStudio.Hash.Digests.Md5.ComputeHash(
            Encoding.UTF8.GetBytes($"sunxi-card-image:{name}"));
        digest[7] = (byte)((digest[7] & 0x0F) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}