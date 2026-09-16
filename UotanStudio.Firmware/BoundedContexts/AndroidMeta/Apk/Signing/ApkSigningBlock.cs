using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Parses and builds the APK Signing Block that sits immediately before the ZIP central directory.
/// All numeric fields are little-endian per the official v2/v3 specification.
/// <para>解析并构建紧邻 ZIP 中央目录之前的 APK 签名分块。所有数字字段按官方 v2/v3 规范采用小端序。</para>
/// </summary>
public static class ApkSigningBlock
{
    /// <summary>
    /// Locates the APK Signing Block in the stream (if present) and returns its offset and contents.
    /// <para>在流中定位 APK 签名分块（若存在），返回其偏移与内容。</para>
    /// </summary>
    /// <returns>Block start offset, full block bytes (including size prefixes and magic), or null when absent.</returns>
    /// <returns>分块起始偏移与完整分块字节（含长度前缀与 magic）；不存在时返回 null。</returns>
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
    public static long ComputeEocdCentralDirectoryOffset(long zipContentEndOffset, byte[] signingBlock)
    {
        return zipContentEndOffset + signingBlock.Length;
    }
}