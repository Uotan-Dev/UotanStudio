using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// Bounded parser for the structured regions of an Allwinner SD-card image. Every read is bounded by the caller
/// supplied region, every offset is validated before use, and no region is trusted to sit inside another.
/// <para>全志 SD 卡镜像各结构化区域的有界解析器。所有读取都以调用方给出的区域为界，每个偏移在使用前校验，
/// 且不假定任一区域包含在另一区域之内。</para>
/// </summary>
internal static class SunxiCardParser
{
    /// <summary>Byte length of one <c>sunxi-package</c> item record. <para>一条 <c>sunxi-package</c> 条目记录的字节长度。</para></summary>
    public const int PackageItemRecordSize = 0x170;

    /// <summary>Byte length of the <c>sunxi-package</c> header that precedes the item records.
    /// <para>条目记录之前的 <c>sunxi-package</c> 头部字节长度。</para></summary>
    public const int PackageHeaderSize = 0x3C;

    /// <summary>Hard ceiling on the declared item count of a boot package. <para>启动包声明条目数的硬性上限。</para></summary>
    public const int MaximumPackageItems = 64;

    /// <summary>Byte length of the eGON <c>boot_file_head</c>. <para>eGON <c>boot_file_head</c> 的字节长度。</para></summary>
    public const int EgonHeaderSize = 64;

    /// <summary>Byte length of the FAT boot sector header needed to describe a volume.
    /// <para>描述一个卷所需的 FAT 引导扇区头字节长度。</para></summary>
    public const int FatHeaderSize = 512;

    /// <summary>Largest root directory this parser walks. <para>本解析器遍历的最大根目录字节数。</para></summary>
    public const int MaximumDirectoryBytes = 64 * 1024;

    /// <summary>Largest classification window read from a partition payload start.
    /// <para>从分区负载起始处读取的最大分类窗口字节数。</para></summary>
    public const int ProbeLength = 512;

    /// <summary>
    /// Reads and validates an eGON first-stage loader header.
    /// <para>读取并校验 eGON 一级引导程序头部。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream. <para>可读、可定位的镜像流。</para></param>
    /// <param name="offset">Byte offset of the loader. <para>引导程序的字节偏移。</para></param>
    /// <param name="limit">End of the region the loader must fit inside. <para>引导程序必须落在其内的区域末端。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The parsed header, or <see langword="null"/> when the offset carries no eGON loader.
    /// <para>解析出的头部；该偏移没有 eGON 引导程序时为 <see langword="null"/>。</para></returns>
    public static SunxiEgonHeader? ReadEgon(Stream stream, long offset, long limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > limit - EgonHeaderSize)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[EgonHeaderSize];
        if (!TryReadAt(stream, offset, header, cancellationToken)
            || !header[4..12].SequenceEqual(SunxiEgonHeader.Magic))
        {
            return null;
        }

        var platform = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        return new SunxiEgonHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            Encoding.ASCII.GetString(header[4..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
            platform,
            ReadPrintableBigEndian(header[20..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[32..]),
            ReadPrintable(header[48..EgonHeaderSize]));
    }

    /// <summary>
    /// Reads a <c>sunxi-package</c> boot package header together with its item table. The item record layout is
    /// fixed at 0x170 bytes: a <c>MIE;</c> tag, a 64-byte name, a 32-bit package-relative payload offset at +0x44,
    /// a 32-bit stored length at +0x48, a type word at +0x50 and a running address at +0x54.
    /// <para>读取 <c>sunxi-package</c> 启动包头及其条目表。条目记录固定 0x170 字节：<c>MIE;</c> 标记、64 字节名称、
    /// 偏移 +0x44 的 32 位包内相对负载偏移、偏移 +0x48 的 32 位存储长度、偏移 +0x50 的类型字与偏移 +0x54 的装载地址。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream. <para>可读、可定位的镜像流。</para></param>
    /// <param name="offset">Byte offset of the package. <para>启动包的字节偏移。</para></param>
    /// <param name="limit">End of the region the package must fit inside. <para>启动包必须落在其内的区域末端。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The parsed header, or <see langword="null"/> when the offset carries no package or its item table escapes the region.
    /// <para>解析出的头部；该偏移没有启动包，或条目表越出区域时为 <see langword="null"/>。</para></returns>
    public static SunxiPackageHeader? ReadPackage(Stream stream, long offset, long limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > limit - PackageHeaderSize)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[PackageHeaderSize];
        if (!TryReadAt(stream, offset, header, cancellationToken)
            || !header[..16].StartsWith("sunxi-package"u8))
        {
            return null;
        }

        var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        var tableBytes = (long)itemCount * PackageItemRecordSize;
        if (itemCount > MaximumPackageItems || offset > limit - PackageHeaderSize - tableBytes)
        {
            return null;
        }

        var items = new List<SunxiPackageItem>((int)itemCount);
        var record = new byte[PackageItemRecordSize];
        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordOffset = offset + PackageHeaderSize + ((long)index * PackageItemRecordSize);
            if (!TryReadAt(stream, recordOffset, record, cancellationToken))
            {
                break;
            }

            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x44));
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x48));
            var payloadOffset = offset + relativeOffset;
            if (payloadOffset > limit || payloadLength > limit - payloadOffset)
            {
                continue;
            }

            items.Add(new SunxiPackageItem(
                index,
                ReadAscii(record.AsSpan(4, 0x40)),
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x50)),
                payloadOffset,
                payloadLength,
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x54)),
                ProbePayload(stream, payloadOffset, payloadLength, cancellationToken)));
        }

        return new SunxiPackageHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
            itemCount,
            BinaryPrimitives.ReadUInt32LittleEndian(header[36..]),
            items);
    }

    /// <summary>
    /// Reads and verifies the vendor U-Boot environment block: a CRC-32 word, a flags byte, then NUL-separated
    /// <c>key=value</c> text, with the CRC covering every byte after the five-byte prefix.
    /// <para>读取并校验厂商 U-Boot 环境块：CRC-32 字、标志字节，随后是以 NUL 分隔的 <c>key=value</c> 文本；
    /// CRC 覆盖五字节前缀之后的全部字节。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream. <para>可读、可定位的镜像流。</para></param>
    /// <param name="offset">Byte offset of the environment slot. <para>环境槽位的字节偏移。</para></param>
    /// <param name="length">Byte length of the slot. <para>槽位的字节长度。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The parsed block, or <see langword="null"/> when the slot carries no environment text.
    /// <para>解析出的环境块；槽位不携带环境文本时为 <see langword="null"/>。</para></returns>
    public static SunxiEnvironmentBlock? ReadEnvironment(Stream stream, long offset, long length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 8)
        {
            return null;
        }

        var area = (int)Math.Min(length, SunxiEnvironmentBlock.StorageLength);
        var buffer = new byte[area];
        if (!TryReadAt(stream, offset, buffer, cancellationToken) || !IsEnvironmentText(buffer.AsSpan(5)))
        {
            return null;
        }

        var stored = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var covered = buffer.Length >= SunxiEnvironmentBlock.StorageLength
            ? buffer.AsSpan(5, SunxiEnvironmentBlock.StorageLength - 5)
            : buffer.AsSpan(5);
        return new SunxiEnvironmentBlock(stored, buffer[4], stored == Crc32Ieee.Compute(covered),
            ParseVariables(buffer.AsSpan(5)));
    }

    /// <summary>
    /// Enumerates the root directory of the FAT volume a partition stores, which is how the vendor
    /// <c>boot-resource</c> partition carries the boot logo.
    /// <para>枚举分区中存储的 FAT 卷根目录；厂商 <c>boot-resource</c> 分区正是以这种方式承载启动 logo。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream. <para>可读、可定位的镜像流。</para></param>
    /// <param name="offset">Byte offset of the volume. <para>卷的字节偏移。</para></param>
    /// <param name="length">Byte length of the volume slot. <para>卷槽位的字节长度。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The root directory entries; empty when the slot is not a FAT volume this parser understands.
    /// <para>根目录条目；槽位不是本解析器可理解的 FAT 卷时为空。</para></returns>
    public static IReadOnlyList<SunxiBootResourceFile> ReadFatRootDirectory(Stream stream, long offset, long length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < FatHeaderSize)
        {
            return [];
        }

        var header = new byte[FatHeaderSize];
        if (!TryReadAt(stream, offset, header, cancellationToken))
        {
            return [];
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(11));
        var sectorsPerCluster = header[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var fatCount = header[16];
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(17));
        var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22));
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0
            || reservedSectors == 0 || fatCount == 0 || rootEntries == 0 || sectorsPerFat == 0)
        {
            return [];
        }

        var rootOffset = (long)(reservedSectors + ((uint)fatCount * sectorsPerFat)) * bytesPerSector;
        var rootBytes = Math.Min((long)rootEntries * 32, MaximumDirectoryBytes);
        if (rootOffset > length - rootBytes)
        {
            return [];
        }

        var dataOffset = rootOffset + ((long)rootEntries * 32);
        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        var directory = new byte[rootBytes];
        if (!TryReadAt(stream, offset + rootOffset, directory, cancellationToken))
        {
            return [];
        }

        var files = new List<SunxiBootResourceFile>();
        var longName = new StringBuilder();
        for (var position = 0; position + 32 <= directory.Length; position += 32)
        {
            var entry = directory.AsSpan(position, 32);
            if (entry[0] == 0x00)
            {
                break;
            }

            if (entry[0] == 0xE5)
            {
                longName.Clear();
                continue;
            }

            var attributes = entry[11];
            if ((attributes & 0x3F) == 0x0F)
            {
                AppendLongNameEntry(longName, entry);
                continue;
            }

            var cluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
            var absolute = cluster >= 2 && clusterBytes > 0
                ? offset + dataOffset + ((long)(cluster - 2) * clusterBytes)
                : -1;
            if (absolute < 0 || (long)(uint)BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]) > length - (absolute - offset))
            {
                absolute = -1;
            }

            files.Add(new SunxiBootResourceFile(longName.ToString(), ReadShortName(entry), attributes, cluster,
                BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]), (attributes & 0x10) != 0 ? -1 : absolute));
            longName.Clear();
        }

        return files;
    }

    /// <summary>
    /// Classifies the leading bytes of a partition payload.
    /// <para>对分区负载起始字节进行分类。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable image stream. <para>可读、可定位的镜像流。</para></param>
    /// <param name="offset">Byte offset of the payload. <para>负载的字节偏移。</para></param>
    /// <param name="length">Byte length of the significant payload. <para>有效负载的字节长度。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The detected kind. <para>识别出的种类。</para></returns>
    public static SunxiCardPayload ProbePayload(Stream stream, long offset, long length,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || offset < 0)
        {
            return SunxiCardPayload.Empty;
        }

        Span<byte> probe = stackalloc byte[ProbeLength];
        var head = probe[..(int)Math.Min(ProbeLength, length)];
        if (!TryReadAt(stream, offset, head, cancellationToken))
        {
            return SunxiCardPayload.Unknown;
        }

        if (head.StartsWith("hsqs"u8))
        {
            return SunxiCardPayload.SquashFs;
        }

        if (head.StartsWith("ANDROID!"u8))
        {
            return SunxiCardPayload.AndroidBootImage;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xD0, 0x0D, 0xFE, 0xED]))
        {
            return SunxiCardPayload.FlattenedDeviceTree;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x7F, 0x45, 0x4C, 0x46]))
        {
            return SunxiCardPayload.ElfImage;
        }

        if (head.Length >= 12 && head[4..12].SequenceEqual(SunxiEgonHeader.Magic))
        {
            return SunxiCardPayload.EgonBootloader;
        }

        if (head.Length >= 64 && head[56..60].SequenceEqual("RISCV"u8))
        {
            return SunxiCardPayload.LinuxImage;
        }

        if (head.IndexOf("OpenSBI"u8) >= 0)
        {
            return SunxiCardPayload.OpenSbiFirmware;
        }

        if (head.IndexOf("U-Boot 20"u8) >= 0)
        {
            return SunxiCardPayload.UBootBinary;
        }

        if (head.Length >= 62
            && BinaryPrimitives.ReadUInt16LittleEndian(head[11..]) is 512 or 1024 or 2048 or 4096
            && (head[54..62].SequenceEqual("FAT16   "u8) || head[54..62].SequenceEqual("FAT32   "u8)))
        {
            return SunxiCardPayload.FatVolume;
        }

        if (head.Length >= 6 && IsEnvironmentText(head[5..]))
        {
            return SunxiCardPayload.Environment;
        }

        if (head.IndexOfAnyExcept((byte)0) < 0)
        {
            return SunxiCardPayload.Empty;
        }

        var end = head.IndexOf((byte)0);
        var text = end < 0 ? head : head[..end];
        return text.Length is > 4 and <= 64 && EndsWithAsciiIgnoreCase(text, ".fex"u8) && IsTextLike(text)
            ? SunxiCardPayload.Placeholder
            : SunxiCardPayload.Unknown;
    }

    /// <summary>
    /// Reports whether a byte range holds only NUL, printable ASCII and line breaks, which is the shape every
    /// observed vendor payload takes for an empty or referenced partition.
    /// <para>判断字节区间是否只包含 NUL、可打印 ASCII 与换行；观测到的厂商空分区或占位分区负载均为此形态。</para>
    /// </summary>
    /// <param name="data">Bytes to inspect. <para>待检查的字节。</para></param>
    /// <returns><see langword="true"/> when the range is text-like. <para>区间为文本形态时为 <see langword="true"/>。</para></returns>
    public static bool IsTextLike(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value is not (0 or (byte)'\n' or (byte)'\r' or (byte)'\t') && value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvironmentText(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return false;
        }

        var end = data.IndexOf((byte)0);
        var first = end < 0 ? data : data[..end];
        return first.Length >= 3 && first.Contains((byte)'=') && first.IndexOfAnyExcept((byte)0) >= 0
            && IsTextLike(first);
    }

    private static Dictionary<string, string> ParseVariables(ReadOnlySpan<byte> data)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in Encoding.ASCII.GetString(data).Split('\0'))
        {
            var separator = assignment.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                variables[assignment[..separator]] = assignment[(separator + 1)..];
            }
        }

        return variables;
    }

    private static void AppendLongNameEntry(StringBuilder builder, ReadOnlySpan<byte> entry)
    {
        Span<char> characters = stackalloc char[13];
        var count = 0;
        ReadOnlySpan<int> starts = [1, 14, 28];
        ReadOnlySpan<int> lengths = [10, 12, 4];
        for (var part = 0; part < 3; part++)
        {
            for (var index = 0; index < lengths[part]; index += 2)
            {
                var unit = BinaryPrimitives.ReadUInt16LittleEndian(entry[(starts[part] + index)..]);
                if (unit is not (0x0000 or 0xFFFF) && count < characters.Length)
                {
                    characters[count++] = (char)unit;
                }
            }
        }

        builder.Insert(0, new string(characters[..count]));
    }

    private static string ReadShortName(ReadOnlySpan<byte> entry)
    {
        var name = ReadAscii(entry[..8]);
        var extension = ReadAscii(entry[8..11]);
        return name.Length == 0 ? string.Empty : extension.Length == 0 ? name : $"{name}.{extension}";
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? data : data[..end]).TrimEnd(' ');
    }

    private static string ReadPrintable(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        var slice = end < 0 ? data : data[..end];
        return slice.Length > 0 && IsTextLike(slice) ? Encoding.ASCII.GetString(slice) : string.Empty;
    }

    private static bool EndsWithAsciiIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> suffix)
    {
        if (text.Length < suffix.Length)
        {
            return false;
        }

        var tail = text[^suffix.Length..];
        for (var index = 0; index < suffix.Length; index++)
        {
            if (char.ToLowerInvariant((char)tail[index]) != char.ToLowerInvariant((char)suffix[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadPrintableBigEndian(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return string.Empty;
            }
        }

        return Encoding.ASCII.GetString(data).Trim();
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> destination,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = offset;
        stream.ReadExactly(destination);
        return true;
    }
}