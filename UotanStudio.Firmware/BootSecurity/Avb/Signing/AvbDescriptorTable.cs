using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

/// <summary>
/// One raw AVB descriptor entry retained by <see cref="AvbDescriptorTable"/>.
/// <para><see cref="AvbDescriptorTable"/> 保留的一条原始 AVB 描述符条目。</para>
/// </summary>
public sealed class AvbDescriptorTableEntry
{
    /// <summary>Numeric descriptor tag read from the entry header.
    /// <para>从条目头部读取的数字描述符标签。</para></summary>
    /// <value><c>0</c> property, <c>1</c> hashtree, <c>2</c> hash, <c>3</c> kernel command line, <c>4</c> chain partition.
    /// <para><c>0</c> 属性、<c>1</c> 哈希树、<c>2</c> 哈希、<c>3</c> 内核命令行、<c>4</c> 链式分区。</para></value>
    public ulong Tag { get; init; }

    /// <summary>Partition name carried by hash, hashtree and chain descriptors.
    /// <para>哈希、哈希树与链式分区描述符携带的分区名。</para></summary>
    /// <value>The decoded name, or <see langword="null"/> for descriptor kinds without one.
    /// <para>解码后的名称；没有分区名的描述符类型为 <see langword="null"/>。</para></value>
    public string? PartitionName { get; init; }

    /// <summary>Total entry size including its 16-byte tag-and-length header and alignment padding.
    /// <para>包含 16 字节标签与长度头部及对齐填充的条目总大小。</para></summary>
    /// <value>The byte count occupied by this entry in the descriptor table.
    /// <para>该条目在描述符表中占用的字节数。</para></value>
    public int Size => RawBytes.Length;

    internal byte[] RawBytes { get; init; } = [];
}

/// <summary>
/// Bounded view of the raw descriptor table inside a VBMeta auxiliary block; entries are kept verbatim so
/// unknown descriptor kinds survive a descriptor update.
/// <para>VBMeta 辅助块中原始描述符表的有界视图；条目按原样保留，使未知描述符类型在更新后仍然存在。</para>
/// </summary>
public sealed class AvbDescriptorTable
{
    private const int DescriptorHeaderSize = 16;
    private const int MaximumEntries = 4096;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const ulong HashDescriptorTag = 2;
    private const ulong HashtreeDescriptorTag = 1;
    private const ulong ChainDescriptorTag = 4;

    private AvbDescriptorTable(IReadOnlyList<AvbDescriptorTableEntry> entries) => Entries = entries;

    /// <summary>Descriptor entries in wire order. <para>按线格式顺序排列的描述符条目。</para></summary>
    /// <value>A read-only snapshot; the table itself is immutable.
    /// <para>只读快照；表本身不可变。</para></value>
    public IReadOnlyList<AvbDescriptorTableEntry> Entries { get; }

    /// <summary>Parses the descriptor table from a complete VBMeta image.
    /// <para>从完整 VBMeta 镜像解析描述符表。</para></summary>
    /// <param name="vbmetaData">Complete VBMeta bytes carrying the <c>AVB0</c> header; regions are bounded to 16 MiB and 4096 entries.
    /// <para>携带 <c>AVB0</c> 头部的完整 VBMeta 字节；区域限制为 16 MiB 与 4096 个条目。</para></param>
    /// <returns>The parsed table, empty when the header declares no descriptor region.
    /// <para>解析后的表；头部未声明描述符区时为空表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="vbmetaData"/> is <see langword="null"/>.
    /// <para><paramref name="vbmetaData"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The header, auxiliary region or one entry violates the AVB layout.
    /// <para>头部、辅助区或某个条目违反 AVB 布局。</para></exception>
    public static AvbDescriptorTable Parse(byte[] vbmetaData)
    {
        ArgumentNullException.ThrowIfNull(vbmetaData);
        if (vbmetaData.Length < AvbVBMetaImageHeader.Size || !vbmetaData.AsSpan(0, 4).SequenceEqual("AVB0"u8))
        {
            throw new InvalidDataException("Invalid VBMeta descriptor container.");
        }

        var data = vbmetaData.AsSpan();
        var authentication = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(12, 8));
        var auxiliary = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(20, 8));
        var descriptorOffset = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(96, 8));
        var descriptorSize = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(104, 8));
        var available = (ulong)data.Length - AvbVBMetaImageHeader.Size;
        if (authentication > available || auxiliary > available - authentication
            || descriptorOffset > auxiliary || descriptorSize > auxiliary - descriptorOffset
            || descriptorSize > MaximumTableBytes)
        {
            throw new InvalidDataException("VBMeta descriptor region exceeds its container or the 16 MiB limit.");
        }

        if (descriptorSize == 0)
        {
            return new AvbDescriptorTable([]);
        }

        _ = AvbSignatureVerifier.ReadDescriptorsStrict(vbmetaData);
        var start = AvbVBMetaImageHeader.Size + (int)authentication + (int)descriptorOffset;
        return ParseEntries(data.Slice(start, (int)descriptorSize));
    }

    /// <summary>Reads the descriptor table embedded in the VBMeta block of a signed partition image.
    /// <para>读取已签名分区镜像 VBMeta 块中嵌入的描述符表。</para></summary>
    /// <param name="imagePath">Raw or Android Sparse partition image carrying a valid AVB footer.
    /// <para>携带合法 AVB 尾部的原始或 Android Sparse 分区镜像。</para></param>
    /// <param name="cancellationToken">Cancels file reads. <para>取消文件读取。</para></param>
    /// <returns>The descriptor table of the embedded VBMeta image.
    /// <para>内嵌 VBMeta 镜像的描述符表。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> is empty or whitespace.
    /// <para><paramref name="imagePath"/> 为空或仅含空白。</para></exception>
    /// <exception cref="FileNotFoundException">The image does not exist. <para>镜像不存在。</para></exception>
    /// <exception cref="InvalidDataException">The image has no valid footer or its VBMeta region is out of range.
    /// <para>镜像没有合法尾部，或其 VBMeta 区域越界。</para></exception>
    public static async Task<AvbDescriptorTable> FromSignedImageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        cancellationToken.ThrowIfCancellationRequested();
        await using var image = await AvbImageSource.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var stream = image.Stream;
        if (stream.Length < AvbFooter.Size)
        {
            throw new InvalidDataException("The image is too small to carry an AVB footer.");
        }

        var footerBytes = new byte[AvbFooter.Size];
        stream.Position = stream.Length - AvbFooter.Size;
        await stream.ReadExactlyAsync(footerBytes, cancellationToken).ConfigureAwait(false);
        var footer = AvbFooter.TryLoad(footerBytes)
            ?? throw new InvalidDataException("The image does not carry a supported AVB footer.");
        var end = checked((ulong)(stream.Length - AvbFooter.Size));
        if (footer.VbmetaSize < AvbVBMetaImageHeader.Size || footer.VbmetaSize > MaximumTableBytes
            || footer.VbmetaOffset > end || footer.VbmetaSize > end - footer.VbmetaOffset)
        {
            throw new InvalidDataException("The AVB footer references an invalid VBMeta region.");
        }

        var vbmeta = new byte[(int)footer.VbmetaSize];
        stream.Position = checked((long)footer.VbmetaOffset);
        await stream.ReadExactlyAsync(vbmeta, cancellationToken).ConfigureAwait(false);
        return Parse(vbmeta);
    }

    /// <summary>Replaces the hash and hashtree descriptors for one partition while retaining every other entry.
    /// <para>替换某个分区的哈希与哈希树描述符，同时保留其他所有条目。</para></summary>
    /// <param name="partitionName">Partition whose content descriptors are replaced.
    /// <para>要替换内容描述符的分区。</para></param>
    /// <param name="replacement">Table whose hash/hashtree entries replace the matching entries; entries for other partitions are ignored.
    /// <para>其哈希/哈希树条目用于替换匹配条目的表；其他分区的条目会被忽略。</para></param>
    /// <returns>A new table with the matching content descriptors replaced.
    /// <para>替换匹配内容描述符后的新表。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> is empty or whitespace.
    /// <para><paramref name="partitionName"/> 为空或仅含空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is <see langword="null"/>.
    /// <para><paramref name="replacement"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The partition is protected by a chain descriptor, which this table editor does not retarget.
    /// <para>该分区由链式描述符保护，而本表编辑器不会改写这种描述符。</para></exception>
    public AvbDescriptorTable ReplaceContentDescriptors(string partitionName, AvbDescriptorTable replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(replacement);
        var kept = new List<AvbDescriptorTableEntry>(Entries.Count + replacement.Entries.Count);
        foreach (var entry in Entries)
        {
            if (entry.PartitionName == partitionName && entry.Tag == ChainDescriptorTag)
            {
                throw new InvalidDataException(
                    $"Partition '{partitionName}' uses a chain descriptor; chain descriptors are preserved and cannot be retargeted.");
            }

            if (entry.PartitionName == partitionName && entry.Tag is HashDescriptorTag or HashtreeDescriptorTag)
            {
                continue;
            }

            kept.Add(entry);
        }

        foreach (var entry in replacement.Entries)
        {
            if (entry.PartitionName == partitionName && entry.Tag is HashDescriptorTag or HashtreeDescriptorTag)
            {
                kept.Add(entry);
            }
        }

        return new AvbDescriptorTable(kept);
    }

    internal byte[] ToArray()
    {
        var total = 0;
        foreach (var entry in Entries)
        {
            total = checked(total + entry.RawBytes.Length);
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var entry in Entries)
        {
            entry.RawBytes.CopyTo(result, offset);
            offset += entry.RawBytes.Length;
        }

        return result;
    }

    private static AvbDescriptorTable ParseEntries(ReadOnlySpan<byte> table)
    {
        var result = new List<AvbDescriptorTableEntry>();
        for (var cursor = 0; cursor < table.Length;)
        {
            if (table.Length - cursor < DescriptorHeaderSize || result.Count == MaximumEntries)
            {
                throw new InvalidDataException("The descriptor header is truncated or the count exceeds 4096.");
            }

            var tag = BinaryPrimitives.ReadUInt64BigEndian(table.Slice(cursor, 8));
            var length = BinaryPrimitives.ReadUInt64BigEndian(table.Slice(cursor + 8, 8));
            if (length % 8 != 0 || length > (ulong)(table.Length - cursor - DescriptorHeaderSize))
            {
                throw new InvalidDataException("The descriptor body is unaligned or exceeds its declared region.");
            }

            var body = table.Slice(cursor + DescriptorHeaderSize, (int)length);
            result.Add(new AvbDescriptorTableEntry
            {
                Tag = tag,
                PartitionName = ReadPartitionName(tag, body),
                RawBytes = table.Slice(cursor, DescriptorHeaderSize + (int)length).ToArray(),
            });
            cursor += DescriptorHeaderSize + (int)length;
        }

        return new AvbDescriptorTable(result);
    }

    private static string? ReadPartitionName(ulong tag, ReadOnlySpan<byte> body)
    {
        var (lengthOffset, nameOffset, fixedSize) = tag switch
        {
            HashtreeDescriptorTag => (88, 164, 164),
            HashDescriptorTag => (40, 116, 116),
            ChainDescriptorTag => (4, 76, 76),
            _ => (-1, -1, 0),
        };
        if (fixedSize == 0 || body.Length < fixedSize)
        {
            return null;
        }

        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(lengthOffset, 4));
        if (nameLength > body.Length - nameOffset)
        {
            return null;
        }

        return Encoding.UTF8.GetStringTrimNul(body.Slice(nameOffset, (int)nameLength));
    }
}