using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs SSA 摘要条目（12 字节）：每主区块对应一条，记录该块所属的父节点 ID、
/// 节点版本与节点内偏移，供 GC 和数据迁移使用。
/// <para>F2FS SSA summary entry (12 bytes): one per main-area block, recording the parent node ID,
/// node version and offset within the node, used by GC and data migration.</para>
/// </summary>
internal sealed record F2fsSummary
{
    /// <summary>摘要条目大小（字节）。Summary entry size in bytes.</summary>
    public const int Size = 12;

    // 字段偏移。Field offsets.
    private const int OffsetNid = 0;
    private const int OffsetVersion = 4;
    private const int OffsetOfsInNode = 5;
    private const int OffsetAllocType = 7;
    private const int OffsetReserved = 8;

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
    /// 分配类型（0 = 热数据，1 = 温数据，2 = 冷数据）。
    /// <para>Allocation type (0 = hot, 1 = warm, 2 = cold).</para>
    /// </summary>
    public byte AllocType { get; init; }

    /// <summary>
    /// 从 12 字节解析摘要条目。
    /// <para>Parses a summary entry from 12 bytes.</para>
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
            AllocType = data[OffsetAllocType],
        };
    }

    /// <summary>
    /// 将摘要条目序列化到缓冲区（至少 12 字节）。
    /// <para>Serializes the summary entry into a buffer of at least 12 bytes.</para>
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
        destination[OffsetAllocType] = AllocType;
    }
}
