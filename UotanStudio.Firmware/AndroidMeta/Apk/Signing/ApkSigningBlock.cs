using System.Buffers.Binary;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Parses and builds the APK Signing Block that sits immediately before the ZIP central directory.
/// All numeric fields are little-endian per the official v2/v3 specification.
/// <para>解析并构建紧邻 ZIP 中央目录之前的 APK 签名分块。所有数字字段按官方 v2/v3 规范采用小端序。</para>
/// </summary>
internal static class ApkSigningBlock
{
    /// <summary>Locates the APK Signing Block in the stream (if present) and returns its offset and contents.
    /// <para>在流中定位 APK 签名分块（若存在），返回其偏移与内容。</para></summary>
    /// <param name="stream">Readable, seekable APK stream; it is repositioned but not closed.
    /// <para>可读、可定位的 APK 流；只重定位、不关闭。</para></param>
    /// <param name="sections">Located ZIP sections whose central-directory offset bounds the search window.
    /// <para>已定位的 ZIP 区段，其中央目录偏移限定搜索窗口。</para></param>
    /// <returns>The block offset and full contents (both size prefixes, ID-value pairs and magic), or <see langword="null"/> when no valid signing block precedes the central directory.
    /// <para>分块偏移与完整分块字节（含两个长度前缀、ID-值对与 magic）；中央目录之前不存在有效签名块时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The magic matches but the 8-byte size prefix contradicts the block layout.
    /// <para>magic 匹配，但 8 字节长度前缀与分块布局矛盾。</para></exception>
    public static (long Offset, byte[] Contents)? Find(Stream stream, in ApkZipSections sections)
    {
        const long minimumBlockSize = 32;
        if (sections.CentralDirectoryOffset < minimumBlockSize)
        {
            return null;
        }

        stream.Seek(sections.CentralDirectoryOffset - 24, SeekOrigin.Begin);
        var footer = new byte[24];
        stream.ReadExactly(footer);

        if (!footer.AsSpan(8, 16).SequenceEqual(ApkSignatureConstants.SigningBlockMagic))
        {
            return null;
        }

        var blockSize = BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(0, 8));
        if (blockSize < 8 || blockSize > (ulong)sections.CentralDirectoryOffset)
        {
            throw new InvalidDataException("APK Signing Block size fields are inconsistent. APK 签名分块的长度字段不一致。");
        }

        var totalSize = checked((long)blockSize + 8);
        var blockOffset = sections.CentralDirectoryOffset - totalSize;
        stream.Seek(blockOffset, SeekOrigin.Begin);
        var contents = new byte[totalSize];
        stream.ReadExactly(contents);
        return (blockOffset, contents);
    }

    /// <summary>
    /// Extracts the ID-value pairs from a signing block's contents.
    /// <para>从签名分块内容中提取全部 ID-值对。</para>
    /// </summary>
    /// <param name="blockContents">Full block bytes as returned by <see cref="Find"/> (leading size
    /// prefix, pairs, trailing size, magic). <para><see cref="Find"/> 返回的完整分块字节（前导长度、条目对、
    /// 尾部长度与 magic）。</para></param>
    /// <returns>ID-value pairs in stored order; the value excludes the pair's ID field.
    /// <para>按存储顺序返回的 ID-值对；值部分不含该对的 ID 字段。</para></returns>
    /// <exception cref="InvalidDataException">A pair length is below 4 or overruns the payload region.
    /// <para>某个对的长度小于 4 或越过有效载荷区域边界。</para></exception>
    public static List<(uint Id, byte[] Value)> ParsePairs(byte[] blockContents)
    {
        var pairs = new List<(uint, byte[])>();
        var payloadLength = blockContents.Length - 24;
        var offset = 8;
        while (offset + 12 <= payloadLength)
        {
            var pairLength = BinaryPrimitives.ReadUInt64LittleEndian(blockContents.AsSpan(offset, 8));
            if (pairLength < 4 || offset + 8 + (long)pairLength > payloadLength)
            {
                throw new InvalidDataException("APK Signing Block contains a corrupt ID-value pair. APK 签名分块包含损坏的 ID-值对。");
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(blockContents.AsSpan(offset + 8, 4));
            var value = new byte[pairLength - 4];
            Array.Copy(blockContents, offset + 12, value, 0, value.Length);
            pairs.Add((id, value));
            offset = checked(offset + 8 + (int)pairLength);
        }

        return pairs;
    }

    /// <summary>
    /// Builds a complete APK Signing Block from ID-value pairs.
    /// <para>由 ID-值对构建完整的 APK 签名分块。</para>
    /// </summary>
    /// <param name="pairs">ID-value pairs to embed, written in enumeration order.
    /// <para>要嵌入的 ID-值对，按枚举顺序写入。</para></param>
    /// <returns>Self-consistent block bytes ready to insert before the central directory: leading size,
    /// pairs, trailing size and the 16-byte magic. <para>可直接插入中央目录之前的自洽分块字节：前导长度、各条目对、
    /// 尾部长度与 16 字节 magic。</para></returns>
    /// <exception cref="OverflowException">The combined pair sizes exceed the addressable block size.
    /// <para>各对合计大小超出可寻址的分块尺寸。</para></exception>
    public static byte[] Build(IEnumerable<(uint Id, byte[] Value)> pairs)
    {
        long payloadLength = 0;
        foreach (var (_, value) in pairs)
        {
            payloadLength += 12 + value.Length;
        }

        var blockSize = payloadLength + 24;
        var block = new byte[checked(blockSize + 8)];
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0, 8), (ulong)blockSize);

        var offset = 8;
        foreach (var (id, value) in pairs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset, 8), (ulong)(4 + value.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 8, 4), id);
            value.CopyTo(block, offset + 12);
            offset = checked(offset + 12 + value.Length);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset, 8), (ulong)blockSize);
        ApkSignatureConstants.SigningBlockMagic.CopyTo(block.AsSpan(offset + 8, 16));
        return block;
    }

    /// <summary>
    /// Computes the offset of the signing block relative to the start of the file
    /// (i.e. the central directory offset that the EOCD must advertise after signing).
    /// <para>计算签名块相对文件起始的偏移（即签名后 EOCD 应填写的中央目录偏移）。</para>
    /// </summary>
    /// <param name="zipContentEndOffset">Offset where the signing block starts, i.e. the end of the ZIP
    /// entry data and former central-directory position. <para>签名块起始处的偏移，即 ZIP 条目数据的末尾，
    /// 也是原中央目录的位置。</para></param>
    /// <param name="signingBlock">Built block whose length shifts the central directory.
    /// <para>已构建的分块，其长度决定中央目录的后移量。</para></param>
    /// <returns><paramref name="zipContentEndOffset"/> plus the block length.
    /// <para><paramref name="zipContentEndOffset"/> 加上分块长度。</para></returns>
    public static long ComputeEocdCentralDirectoryOffset(long zipContentEndOffset, byte[] signingBlock)
    {
        return zipContentEndOffset + signingBlock.Length;
    }
}