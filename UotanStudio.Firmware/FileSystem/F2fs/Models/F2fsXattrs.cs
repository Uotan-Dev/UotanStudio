using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

/// <summary>
/// Reads and writes the Linux F2FS aligned xattr header and entry layout.
/// <para>读写 Linux F2FS 对齐的 xattr 头与条目布局。</para>
/// </summary>
/// <remarks>
/// The encoded block starts with the kernel's <c>struct f2fs_xattr_header</c>, which is 24 bytes:
/// <c>h_magic</c> (<see cref="Magic"/>) at offset 0, <c>h_refcount</c> at offset 4 and
/// <c>h_reserved[4]</c> at offsets 8 to 23. Kernel-written reference images leave the reserved
/// words zero and set <c>h_refcount</c> to one, so no entry count, total size, block address or
/// version field exists in this header; entry counting walks <c>f2fs_xattr_entry</c> records until
/// the terminating zero word. The block is placed at <c>i_addr[923 - i_inline_xattr_size]</c> inside
/// an inode node, and any overflow continues at offset 0 of the external xattr node block, whose
/// usable size is the node size minus the 24-byte node footer.
/// <para>编码块以内核的 <c>struct f2fs_xattr_header</c> 开头，长度为 24 字节：偏移 0 处是
/// <c>h_magic</c>（<see cref="Magic"/>），偏移 4 处是 <c>h_refcount</c>，偏移 8 到 23 是
/// <c>h_reserved[4]</c>。内核写入的参照镜像把保留字清零并把 <c>h_refcount</c> 置 1，因此该头部
/// 不存在条目计数、总大小、块地址或版本字段；条目计数靠遍历 <c>f2fs_xattr_entry</c> 直到终止零字。
/// 该块放在 inode 节点内的 <c>i_addr[923 - i_inline_xattr_size]</c>，溢出部分续写到外部 xattr
/// 节点块的偏移 0，其可用大小为节点大小减去 24 字节的节点页脚。</para>
/// </remarks>
internal static class F2fsXattrs
{
    internal const uint Magic = 0xF2F52011;

    internal const int HeaderSize = 24;

    private static readonly FrozenDictionary<byte, string> Prefixes = new Dictionary<byte, string>
    {
        [1] = "user.",
        [2] = "system.posix_acl_access",
        [3] = "system.posix_acl_default",
        [4] = "trusted.",
        [6] = "security.",
        [9] = "encryption."
    }.ToFrozenDictionary();

    /// <summary>
    /// Decodes an inline plus external xattr block into namespace-qualified names and raw values.
    /// <para>把内联加外部 xattr 块解码为带命名空间的名称与原始值。</para>
    /// </summary>
    /// <param name="data">The concatenated inline and external xattr bytes, starting at the 24-byte header; an all-zero region yields no attributes and a header whose magic is missing but whose bytes are not empty is reported as corruption.
    /// <para>内联与外部 xattr 字节的拼接结果，以 24 字节头部起始；全零区域不产生属性，而幻数缺失且内容非空的头部按损坏上报。</para></param>
    /// <returns>Attributes keyed by their prefixed names in stored order; a truncated entry that reaches past the buffer is reported as corruption.
    /// <para>按存储顺序以带前缀的名称为键返回属性；条目越过缓冲区末尾时按损坏上报。</para></returns>
    internal static Dictionary<string, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (data.IsEmpty || data[..Math.Min(4, data.Length)].IndexOfAnyExcept((byte)0) < 0) return result;
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new FileSystemCorruptedException("Invalid F2FS xattr header.");
        var offset = HeaderSize;
        while (offset + 4 <= data.Length && BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != 0)
        {
            var index = data[offset];
            var nameLength = data[offset + 1];
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            var length = checked(4 + nameLength + valueLength);
            if (length > data.Length - offset) throw new FileSystemCorruptedException("Truncated F2FS xattr.");
            var prefix = Prefixes.GetValueOrDefault(index, $"f2fs.index{index}.");
            var name = prefix + Encoding.UTF8.GetString(data.Slice(offset + 4, nameLength));
            result.Add(name, data.Slice(offset + 4 + nameLength, valueLength).ToArray());
            offset += (length + 3) & ~3;
        }
        return result;
    }

    /// <summary>
    /// Encodes attributes into the kernel's aligned xattr block, ready to split between the inode's inline region and one external node.
    /// <para>把属性编码为内核的对齐 xattr 块，可直接切分到 inode 内联区与一个外部节点。</para>
    /// </summary>
    /// <param name="values">Attributes keyed by their prefixed names; null or empty produces an empty block, and a key outside the known namespace prefixes is rejected.
    /// <para>以带前缀名称为键的属性；null 或空值产生空块，超出已知命名空间前缀的键会被拒绝。</para></param>
    /// <returns>The 24-byte header followed by the aligned entries and the terminating zero word; a name longer than 255 bytes, a value longer than 65535 bytes or a block past the inline plus external capacity is reported as corruption.
    /// <para>返回 24 字节头部、对齐条目与终止零字；名称超过 255 字节、值超过 65535 字节或整块超过内联加外部容量时按损坏上报。</para></returns>
    internal static byte[] Encode(Dictionary<string, byte[]>? values)
    {
        if (values is not { Count: > 0 }) return [];
        using var output = new MemoryStream();
        output.Write(new byte[HeaderSize]);
        foreach (var pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var match = Prefixes.FirstOrDefault(p => pair.Key.StartsWith(p.Value, StringComparison.Ordinal));
            if (match.Key == 0 && pair.Key.StartsWith("f2fs.index", StringComparison.Ordinal))
            {
                var end = pair.Key.IndexOf('.', "f2fs.index".Length);
                if (end > 0 && byte.TryParse(pair.Key.AsSpan("f2fs.index".Length, end - "f2fs.index".Length), out var index) && index != 0)
                    match = new(index, pair.Key[..(end + 1)]);
            }
            if (match.Key == 0) throw new FileSystemUnsupportedFeatureException($"Unsupported F2FS xattr namespace: {pair.Key}");
            var name = Encoding.UTF8.GetBytes(pair.Key[match.Value.Length..]);
            if (name.Length > 255 || pair.Value.Length > ushort.MaxValue) throw new FileSystemCorruptedException("F2FS xattr exceeds its entry limit.");
            output.WriteByte(match.Key);
            output.WriteByte((byte)name.Length);
            output.WriteByte((byte)pair.Value.Length);
            output.WriteByte((byte)(pair.Value.Length >> 8));
            output.Write(name);
            output.Write(pair.Value);
            while (output.Position % 4 != 0) output.WriteByte(0);
        }
        output.Write(new byte[4]);
        var capacity = F2fsInode.MaxInlineXattrWords * 4 + F2fsNode.NodeSize - F2fsNodeFooter.FooterSize;
        if (output.Length > capacity) throw new FileSystemCorruptedException($"F2FS xattrs exceed the inline and external node capacity ({capacity} bytes); no attributes were discarded.");
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 1);
        return result;
    }
}