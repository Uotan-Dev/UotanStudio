using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// Bounded reader for Allwinner SD-card images: a protective MBR, a GPT pair whose first usable block sits far
/// past the boot region, an eGON first-stage loader at LBA 16 and a <c>sunxi-package</c> boot package before the
/// first usable block. Each partition slot is reported with the role its payload actually starts with, and the
/// vendor <c>env</c> and <c>boot-resource</c> partitions are decoded rather than left opaque.
/// <para>全志 SD 卡镜像的有界读取器：保护性 MBR、首个可用块远在引导区之后的 GPT 主备对、LBA 16 处的 eGON 一级
/// 引导程序，以及首个可用块之前的 <c>sunxi-package</c> 启动包。每个分区槽位都附带其负载实际起始处的角色，
/// 厂商 <c>env</c> 与 <c>boot-resource</c> 分区会被解码而不是当作不透明数据。</para>
/// </summary>
internal static class SunxiCardImageFormat
{
    /// <summary>Logical block size of every Allwinner card image. <para>全志卡镜像的逻辑块大小。</para></summary>
    public const int BlockSize = 512;

    /// <summary>Byte offset the vendor boot ROM reads the first-stage loader from. <para>厂商引导 ROM 读取一级引导程序的字节偏移。</para></summary>
    public const long EgonOffset = 0x2000;

    /// <summary>Declared byte length of a card first-stage loader slot. <para>卡一级引导程序槽位的声明字节长度。</para></summary>
    public const long EgonLength = 0x10000;

    /// <summary>Largest pre-partition boot region this reader scans for a boot package.
    /// <para>本读取器为查找启动包而扫描的最大分区前引导区字节数。</para></summary>
    public const long MaximumBootRegionScan = 64L * 1024 * 1024;

    private const long ScanChunkSize = 64 * 1024;
    private const string BootResourcePartition = "boot-resource";

    /// <summary>
    /// Reports whether a leading byte window belongs to an Allwinner SD-card image: the MBR signature, a GPT header
    /// at LBA 1 and the <c>eGON.BT0</c> loader magic at LBA 16 all have to be present, which is what separates this
    /// layout from a plain GPT disk.
    /// <para>判断头部字节窗口是否属于全志 SD 卡镜像：MBR 签名、LBA 1 的 GPT 头部与 LBA 16 处的 <c>eGON.BT0</c>
    /// 引导魔数必须同时存在；这正是本布局与普通 GPT 磁盘的区别。</para>
    /// </summary>
    /// <param name="header">Leading bytes already read by the caller. <para>调用方预先读入的起始字节。</para></param>
    /// <returns><see langword="true"/> when every one of the three markers is present.
    /// <para>三个标记全部存在时为 <see langword="true"/>。</para></returns>
    public static bool MatchesMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < EgonOffset + 12)
        {
            return false;
        }

        return header[510] == 0x55 && header[511] == 0xAA
            && header[BlockSize..].StartsWith("EFI PART"u8)
            && header[(int)EgonOffset..].Slice(4, 8).SequenceEqual(SunxiEgonHeader.Magic);
    }

    /// <summary>
    /// Reads the whole card image structure: the validated GPT, the pre-partition boot region, the U-Boot
    /// environments and the <c>boot-resource</c> file list.
    /// <para>读取整份卡镜像结构：已校验的 GPT、分区前的引导区、U-Boot 环境与 <c>boot-resource</c> 文件清单。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream; its position is restored on return.
    /// <para>可读、可定位的镜像流；返回时恢复其位置。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The parsed image. <para>解析出的镜像。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可定位。</para></exception>
    /// <exception cref="InvalidDataException">The image is too short, is not an Allwinner card image, or its GPT pair does not validate.
    /// <para>镜像过短、不是全志卡镜像，或其 GPT 主备对未通过校验。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static SunxiCardImage Read(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Card image reading requires a readable, seekable stream.", nameof(stream));
        }

        var original = stream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = stream.Length;
            var blockCount = (ulong)(length / BlockSize);
            if (length < EgonOffset + EgonLength || blockCount < 64)
            {
                throw new InvalidDataException("The card image is too short to carry a sunxi boot region and a GPT.");
            }

            Span<byte> marker = stackalloc byte[16];
            ReadAt(stream, EgonOffset);
            stream.ReadExactly(marker);
            if (!marker.Slice(4, 8).SequenceEqual(SunxiEgonHeader.Magic))
            {
                throw new InvalidDataException("The image carries no eGON.BT0 first-stage loader at LBA 16.");
            }

            var table = GptReader.Read(stream, BlockSize, blockCount, cancellationToken);
            var usableOffset = checked((long)(table.FirstUsableLba * BlockSize));
            var bootRegion = ReadBootRegion(stream, usableOffset, cancellationToken);
            var bootImage = ReadPartitionBootImage(stream, table, cancellationToken);
            var partitions = new List<SunxiCardPartition>(table.Partitions.Count);
            var environments = new Dictionary<string, SunxiEnvironmentBlock>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<SunxiBootResourceFile> bootResourceFiles = [];
            foreach (var partition in table.Partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = SunxiCardParser.ProbePayload(stream, partition.ByteOffset, partition.ByteLength,
                    cancellationToken);
                var contentLength = payload == SunxiCardPayload.Environment
                    ? Math.Min(partition.ByteLength, SunxiEnvironmentBlock.StorageLength)
                    : TrimmedLength(stream, partition.ByteOffset, partition.ByteLength, cancellationToken);
                var exportName = SafeExportName(partition.Name, partition.Index);
                partitions.Add(new SunxiCardPartition(partition.Index, partition.Name, partition.FirstLba,
                    partition.LastLba, partition.Attributes, partition.TypeId, partition.Id, partition.ByteOffset,
                    partition.ByteLength, contentLength, payload, exportName));

                if (payload == SunxiCardPayload.Environment
                    && SunxiCardParser.ReadEnvironment(stream, partition.ByteOffset, contentLength,
                        cancellationToken) is { } environment)
                {
                    environments[partition.Name] = environment;
                }

                if (string.Equals(partition.Name, BootResourcePartition, StringComparison.OrdinalIgnoreCase))
                {
                    bootResourceFiles = SunxiCardParser.ReadFatRootDirectory(stream, partition.ByteOffset,
                        contentLength, cancellationToken);
                }
            }

            return new SunxiCardImage(BlockSize, blockCount, table.DiskId, table.FirstUsableLba, table.LastUsableLba,
                partitions, bootRegion, bootImage, environments, bootResourceFiles);
        }
        finally
        {
            stream.Position = original;
        }
    }

    /// <summary>
    /// Builds the vendor file name a partition is exported under: the partition name with the vendor <c>.fex</c>
    /// suffix, sanitized so it can never escape the output directory.
    /// <para>构建分区导出时使用的厂商文件名：分区名加厂商 <c>.fex</c> 后缀，并做清理以确保永远不会逃出输出目录。</para>
    /// </summary>
    /// <param name="partitionName">GPT partition name. <para>GPT 分区名。</para></param>
    /// <param name="index">Slot index used when the name is empty. <para>名称为空时使用的槽位序号。</para></param>
    /// <returns>A relative file name without separators. <para>不含分隔符的相对文件名。</para></returns>
    public static string SafeExportName(string partitionName, int index)
    {
        var cleaned = new string([.. (partitionName ?? string.Empty)
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character) && character > 0x1F)])
            .Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 || cleaned is "." or ".." ? $"partition_{index}.fex" : $"{cleaned}.fex";
    }

    private static SunxiCardBootRegion ReadBootRegion(Stream stream, long usableOffset,
        CancellationToken cancellationToken)
    {
        var scanEnd = Math.Min(usableOffset, checked(EgonOffset + MaximumBootRegionScan));
        var egon = SunxiCardParser.ReadEgon(stream, EgonOffset, usableOffset, cancellationToken);
        var packageOffset = FindMatch(stream, EgonOffset + EgonLength, scanEnd, "sunxi-package"u8, cancellationToken);
        SunxiPackageHeader? package = null;
        if (packageOffset is { } offset)
        {
            package = SunxiCardParser.ReadPackage(stream, offset, usableOffset, cancellationToken);
            if (package is null)
            {
                return new SunxiCardBootRegion(EgonOffset, egon, null, null, 0);
            }
        }

        var regionLength = package is null ? 0L : usableOffset - packageOffset!.Value;
        return new SunxiCardBootRegion(EgonOffset, egon, packageOffset, package, regionLength);
    }

    private static long? FindMatch(Stream stream, long start, long end, ReadOnlySpan<byte> magic,
        CancellationToken cancellationToken)
    {
        if (end <= start)
        {
            return null;
        }

        var overlap = magic.Length - 1;
        var maximumWindow = (int)ScanChunkSize + (2 * overlap);
        var buffer = ArrayPool<byte>.Shared.Rent(maximumWindow);
        try
        {
            for (var position = start; position < end; position += ScanChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readStart = Math.Max(start, position - overlap);
                var readEnd = Math.Min(long.Max(position + ScanChunkSize + overlap, readStart + 1), end);
                var wanted = (int)Math.Min(readEnd - readStart, maximumWindow);
                var span = buffer.AsSpan(0, wanted);
                stream.Position = readStart;
                var read = 0;
                while (read < span.Length)
                {
                    var count = stream.Read(span[read..]);
                    if (count <= 0)
                    {
                        break;
                    }

                    read += count;
                }

                var found = span[..read].IndexOf(magic);
                if (found >= 0)
                {
                    return readStart + found;
                }

                if (read < wanted)
                {
                    return null;
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the segment layout of the partition the vendor environment boots as <c>boot</c>. The vendor kernel
    /// path reuses the Android boot-image header but stores a RISC-V <c>Image</c> where Android keeps its kernel
    /// blob, so the header offsets are read as declared and the payload magic is reported separately.
    /// <para>读取厂商环境按 <c>boot</c> 引导的那个分区的段布局。厂商内核路径复用了 Android 启动镜像头部，
    /// 但在 Android 存放内核 blob 的位置放的是 RISC-V <c>Image</c>，因此按声明读取头部偏移，并单独报告负载魔数。</para>
    /// </summary>
    private static SunxiBootImage? ReadPartitionBootImage(Stream stream, GptTable table,
        CancellationToken cancellationToken)
    {
        const int HeaderBytes = 0x680;
        foreach (var partition in table.Partitions)
        {
            var span = stream.Length - partition.ByteOffset;
            if (span < HeaderBytes)
            {
                continue;
            }

            var header = new byte[HeaderBytes];
            stream.Position = partition.ByteOffset;
            var read = 0;
            while (read < header.Length)
            {
                var count = stream.Read(header, read, header.Length - read);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }

            if (read < HeaderBytes || !header.AsSpan().StartsWith("ANDROID!"u8))
            {
                continue;
            }

            var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
            if (pageSize is not (512 or 1024 or 2048 or 4096 or 8192 or 16384 or 32768 or 65536))
            {
                continue;
            }

            var kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var ramdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var secondSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            var deviceTreeSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40));
            return new SunxiBootImage(
                partition.Name,
                pageSize,
                kernelSize,
                ramdiskSize,
                secondSize,
                deviceTreeSize,
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36)),
                ReadText(header.AsSpan(48, 16)),
                ReadText(header.AsSpan(64, 512)),
                ProbeSegment(stream, partition.ByteOffset + pageSize, kernelSize, cancellationToken));
        }

        return null;
    }

    private static SunxiCardPayload ProbeSegment(Stream stream, long offset, long length,
        CancellationToken cancellationToken)
        => SunxiCardParser.ProbePayload(stream, offset, length, cancellationToken);

    private static string ReadText(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        var slice = end < 0 ? data : data[..end];
        return SunxiCardParser.IsTextLike(slice) ? Encoding.ASCII.GetString(slice) : string.Empty;
    }

    private static long TrimmedLength(Stream stream, long offset, long length, CancellationToken cancellationToken)
    {
        if (length <= 0 || offset < 0 || offset > stream.Length - length)
        {
            return 0;
        }

        var window = (int)Math.Min(ScanChunkSize, length);
        var buffer = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(window, remaining);
                var span = buffer.AsSpan(0, wanted);
                stream.Position = offset + remaining - wanted;
                var read = 0;
                while (read < span.Length)
                {
                    var count = stream.Read(span[read..]);
                    if (count <= 0)
                    {
                        break;
                    }

                    read += count;
                }

                var last = span[..read].LastIndexOfAnyExcept((byte)0);
                if (last >= 0)
                {
                    var significant = remaining - wanted + last + 1;
                    return Math.Min(length, (significant + BlockSize - 1) / BlockSize * BlockSize);
                }

                remaining -= read == 0 ? wanted : Math.Min(wanted, read);
            }

            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadAt(Stream stream, long offset) => stream.Position = offset;
}