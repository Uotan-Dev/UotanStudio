using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal sealed record F2fsSummary
{
    public const int Size = 7;


    private const int OffsetNid = 0;
    private const int OffsetVersion = 4;
    private const int OffsetOfsInNode = 5;

    public uint Nid { get; init; }

    public byte Version { get; init; }

    public ushort OfsInNode { get; init; }

    /// <summary>
    /// Parses a summary entry from 7 bytes (f2fs-tools layout).
    /// <para>从 7 字节解析摘要条目（f2fs-tools 布局）。</para>
    /// </summary>
    /// <param name="data">Summary entry bytes, at least <see cref="Size"/> long, holding the little-endian node id at 0, the separate version byte at 4 and the little-endian offset within the node at 5.
    /// <para>摘要条目字节，长度至少为 <see cref="Size"/>：偏移 0 是小端节点号，偏移 4 是独立的版本字节，偏移 5 是小端的节点内偏移。</para></param>
    /// <returns>Decoded entry; no bound is placed on the version or the offset, so both mirror the stored bytes.
    /// <para>已解码的条目；版本与偏移都不做取值限制，两者与存储的字节一致。</para></returns>
    public static F2fsSummary FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException($"F2FS summary region too short: {data.Length} bytes.");
        }

        return new F2fsSummary
        {
            Nid = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNid..]),
            Version = data[OffsetVersion],
            OfsInNode = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetOfsInNode..]),
        };
    }

    /// <summary>
    /// Serializes the summary entry into a buffer of at least 7 bytes (f2fs-tools layout).
    /// <para>将摘要条目序列化到缓冲区（至少 7 字节，f2fs-tools 布局）。</para>
    /// </summary>
    /// <param name="destination">Buffer offering at least <see cref="Size"/> writable bytes; its first <see cref="Size"/> bytes are cleared, then the node id, version byte and node offset are written little-endian and bytes beyond the entry are left untouched.
    /// <para>至少提供 <see cref="Size"/> 个可写字节的缓冲区；前 <see cref="Size"/> 字节先清零，再按小端写入节点号、版本字节与节点内偏移，条目之外的字节保持不变。</para></param>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Summary destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetNid..], Nid);
        destination[OffsetVersion] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetOfsInNode..], OfsInNode);
    }
}