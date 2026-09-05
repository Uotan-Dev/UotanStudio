using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs SSA 摘要条目（7 字节，f2fs-tools 1.16.0）：每主区块对应一条，记录该块所属的父节点 ID、
/// 节点版本与节点内偏移，供 GC 和数据迁移使用。
/// <para>F2FS SSA summary entry (7 bytes, f2fs-tools 1.16.0): one per main-area block, recording the
/// parent node ID, node version and offset within the node, used by GC and data migration.</para>
/// </summary>
internal sealed record F2fsSummary
{
    /// <summary>摘要条目大小（字节，f2fs-tools struct f2fs_summary = 7）。
    /// <para>Summary entry size in bytes (f2fs-tools struct f2fs_summary = 7).</para></summary>
    public const int Size = 7;

    // f2fs-tools struct f2fs_summary 字段偏移（__packed，小端）。
    // f2fs-tools struct f2fs_summary field offsets (__packed, little-endian).
    //   __le32 nid;            // offset 0, 4 bytes
    //   __u8   version;        // offset 4, 1 byte
    //   __le16 ofs_in_node;    // offset 5, 2 bytes
    //   Total: 7 bytes (alloc_type 在 f2fs-tools 中属于 reserved union，不单独序列化)
    private const int OffsetNid = 0;
    private const int OffsetVersion = 4;
    private const int OffsetOfsInNode = 5;

    /// <summary>
    /// 父节点 ID（nid）。
    /// <para>Parent node id.</para>
    /// </summary>
    public uint Nid { get; init; }

    /// <summary>
    /// 节点版本号。
    /// <para>Node version number.</para>
    /// </summary>
    public byte Version { get; init; }

    /// <summary>
    /// 块在父节点内的偏移。
    /// <para>Block offset within the parent node.</para>
    /// </summary>
    public ushort OfsInNode { get; init; }

    /// <summary>
    /// 从 7 字节解析摘要条目（f2fs-tools 布局）。
    /// <para>Parses a summary entry from 7 bytes (f2fs-tools layout).</para>
    /// </summary>
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
    /// 将摘要条目序列化到缓冲区（至少 7 字节，f2fs-tools 布局）。
    /// <para>Serializes the summary entry into a buffer of at least 7 bytes (f2fs-tools layout).</para>
    /// </summary>
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
