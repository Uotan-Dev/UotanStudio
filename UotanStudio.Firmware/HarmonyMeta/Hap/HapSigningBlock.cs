using System.Buffers.Binary;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>One parsed signing block entry. <para>一个已解析的签名块条目。</para></summary>
/// <param name="TypeId">Block type ID, e.g. <see cref="HapSignatureConstants.BlockIdSignature"/>.
/// <para>块类型 ID，例如 <see cref="HapSignatureConstants.BlockIdSignature"/>。</para></param>
/// <param name="Value">The block value bytes. <para>块值字节。</para></param>
public readonly record struct HapBlockEntry(uint TypeId, byte[] Value);

/// <summary>
/// Assembles and parses the HAP signing block: 12-byte <c>[type][length][offset]</c> little-endian headers, the
/// concatenated values, a <c>u32</c> block count, a <c>u64</c> total size that includes the 28-byte fixed tail
/// (size field itself + magic + version), the 16-byte magic and the trailing <c>u32</c> scheme version.
/// <para>组装与解析 HAP 签名块：12 字节小端 <c>[type][length][offset]</c> 头、连续值、<c>u32</c> 块数、
/// 含 28 字节固定尾部（长度字段自身+魔数+版本）的 <c>u64</c> 总长、16 字节魔数与末尾 <c>u32</c> 方案版本。</para>
/// </summary>
/// <remarks>
/// Value offsets are measured from the start of the block payload, matching the official signer; install-time
/// profile parsing reads the same absolute offsets.
/// <para>值偏移自块负载起点计，与官方签名器一致；装机侧 Profile 解析使用同样的绝对偏移。</para>
/// </remarks>
public static class HapSigningBlock
{
    /// <summary>Size of one TLV header. <para>单个 TLV 头的大小。</para></summary>
    public const int HeaderSize = 12;

    /// <summary>Fixed tail after the values: count(4) + size(8) + magic(16) + version(4).
    /// <para>值之后的固定尾部：块数(4)+长度(8)+魔数(16)+版本(4)。</para></summary>
    public const int TailSize = 4 + 8 + 16 + 4;

    /// <summary>
    /// Assembles the signing block for the supplied entries; the scheme version and magic follow
    /// <paramref name="compatibleSdkVersion"/>.
    /// <para>按给定条目组装签名块；方案版本与魔数跟随 <paramref name="compatibleSdkVersion"/>。</para>
    /// </summary>
    /// <param name="blocks">Entries in emission order. <para>按输出顺序排列的条目。</para></param>
    /// <param name="compatibleSdkVersion">The HAP's minimum compatible SDK version.
    /// <para>HAP 的最小兼容 SDK 版本。</para></param>
    /// <returns>The complete signing block bytes. <para>完整签名块字节。</para></returns>
    public static byte[] Build(ReadOnlySpan<HapBlockEntry> blocks, uint compatibleSdkVersion)
    {
        var headerSize = blocks.Length * HeaderSize;
        var dataSize = 0;
        foreach (var block in blocks)
        {
            checked
            {
                dataSize += block.Value.Length;
            }
        }

        var totalSize = headerSize + dataSize + TailSize;
        var buffer = new byte[totalSize];

        var offset = 0;
        uint valueOffset = (uint)headerSize;
        foreach (var block in blocks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), block.TypeId);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), (uint)block.Value.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8), valueOffset);
            valueOffset += (uint)block.Value.Length;
            offset += HeaderSize;
        }

        foreach (var block in blocks)
        {
            block.Value.CopyTo(buffer, offset);
            offset += block.Value.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)blocks.Length);
        offset += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), (ulong)totalSize);
        offset += 8;

        var version = compatibleSdkVersion >= HapSignatureConstants.SchemeVersionBoundary
            ? HapSignatureConstants.SchemeVersionV3
            : HapSignatureConstants.SchemeVersionV2;
        var magic = version == HapSignatureConstants.SchemeVersionV3
            ? HapSignatureConstants.MagicV3
            : HapSignatureConstants.MagicV2;
        magic.CopyTo(buffer, offset);
        offset += magic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), version);
        return buffer;
    }

    /// <summary>
    /// Locates an existing signing block in the stream: it ends immediately before the central directory, so the
    /// <c>u64</c> size right before the magic anchors the block start.
    /// <para>在流中定位既有签名块：块恰好结束于中央目录之前，魔数前的 <c>u64</c> 长度锚定块起点。</para>
    /// </summary>
    /// <param name="stream">Seekable signed-package stream; the position is preserved.
    /// <para>可定位的已签包流；位置保持不变。</para></param>
    /// <param name="centralDirectoryOffset">Central directory start offset from the EOCD.
    /// <para>EOCD 记录的中央目录起始偏移。</para></param>
    /// <returns>
    /// The block start offset and length, or <see langword="null"/> when the bytes before the central directory are
    /// not a HAP signing block.
    /// <para>块起始偏移与长度；中央目录之前不是 HAP 签名块时为 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">The block size field is inconsistent with the magic.
    /// <para>块长度字段与魔数不一致。</para></exception>
    public static (long Start, int Length)? Find(Stream stream, long centralDirectoryOffset)
    {
        if (centralDirectoryOffset < TailSize)
        {
            return null;
        }

        var original = stream.Position;
        try
        {
            Span<byte> tail = stackalloc byte[TailSize];
            stream.Seek(centralDirectoryOffset - TailSize, SeekOrigin.Begin);
            stream.ReadExactly(tail);

            var version = BinaryPrimitives.ReadUInt32LittleEndian(tail[^4..]);
            Span<byte> magic = tail[^20..^4];
            var isV3 = magic.SequenceEqual(HapSignatureConstants.MagicV3);
            var isV2 = magic.SequenceEqual(HapSignatureConstants.MagicV2);
            if (!isV2 && !isV3)
            {
                return null;
            }

            var expectedVersion = isV3
                ? HapSignatureConstants.SchemeVersionV3
                : HapSignatureConstants.SchemeVersionV2;
            if (version != expectedVersion)
            {
                throw new InvalidDataException(
                    $"HAP signing block version {version} does not match its magic. HAP 签名块版本 {version} 与魔数不匹配。");
            }

            var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail[4..12]);
            if (total < HeaderSize + TailSize || total > centralDirectoryOffset)
            {
                throw new InvalidDataException(
                    $"HAP signing block size {total} is out of range. HAP 签名块长度 {total} 越界。");
            }

            var start = centralDirectoryOffset - total;
            return (start, (int)total);
        }
        finally
        {
            stream.Seek(original, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Parses the TLV headers of a signing block and returns the entries in file order; values are read as
    /// sub-ranges of <paramref name="block"/> so the caller keeps the backing buffer alive.
    /// <para>解析签名块的 TLV 头并按文件顺序返回条目；值以 <paramref name="block"/> 的子区间返回，
    /// 由调用方持有底层缓冲。</para>
    /// </summary>
    /// <param name="block">The complete signing block bytes. <para>完整签名块字节。</para></param>
    /// <returns>Parsed entries. <para>解析出的条目。</para></returns>
    /// <exception cref="InvalidDataException">A header is truncated, its offset/length escape the value region,
    /// or the trailing count disagrees with the header count.
    /// <para>头被截断、偏移/长度越过值区，或尾部块数与头数量不符。</para></exception>
    public static List<HapBlockEntry> Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < HeaderSize + TailSize)
        {
            throw new InvalidDataException("HAP signing block is too small. HAP 签名块过小。");
        }

        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(block[^28..^20]);
        if (total != block.Length)
        {
            throw new InvalidDataException(
                $"HAP signing block size field {total} disagrees with the buffer length {block.Length}. " +
                $"HAP 签名块长度字段 {total} 与缓冲区长度 {block.Length} 不一致。");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[^32..^28]);
        if (count == 0 || count > 64)
        {
            throw new InvalidDataException(
                $"HAP signing block count {count} is unreasonable. HAP 签名块数量 {count} 不合理。");
        }

        var result = new List<HapBlockEntry>((int)count);
        var headerEnd = (long)count * HeaderSize;
        if (headerEnd > block.Length - TailSize)
        {
            throw new InvalidDataException("HAP signing block headers exceed the value region. HAP 签名块头越过值区。");
        }

        for (var index = 0; index < count; index++)
        {
            var offset = index * HeaderSize;
            var typeId = BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(block[(offset + 4)..]);
            var valueOffset = BinaryPrimitives.ReadUInt32LittleEndian(block[(offset + 8)..]);
            if (valueOffset + length > (uint)(block.Length - TailSize) || valueOffset < (uint)headerEnd)
            {
                throw new InvalidDataException(
                    $"HAP signing block entry 0x{typeId:X8} escapes the value region. HAP 签名块条目 0x{typeId:X8} 越过值区。");
            }

            result.Add(new HapBlockEntry(typeId, block.Slice((int)valueOffset, (int)length).ToArray()));
        }

        return result;
    }
}