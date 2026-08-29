using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs extent（struct f2fs_extent，12 字节）：文件内偏移 fofs、起始物理块 blk 与长度 len，
/// 位于 inode 内偏移 348 处，作为最大连续区间的缓存。
/// <para>F2FS extent (struct f2fs_extent, 12 bytes): in-file offset fofs, start block blk and length
/// len, stored at inode offset 348 as a cache of the largest contiguous run.</para>
/// </summary>
internal sealed record F2fsExtent
{
    /// <summary>extent 大小（字节）。Size of an extent in bytes.</summary>
    public const int Size = 12;

    /// <summary>extent 在 inode 内的字节偏移。Byte offset of the extent within the inode.</summary>
    public const int InodeOffset = 348;

    /// <summary>文件内起始逻辑块号（fofs）。Start logical block offset in the file (fofs).</summary>
    public uint Fofs { get; init; }

    /// <summary>extent 起始物理块号（blk）。Start physical block address (blk).</summary>
    public uint Blk { get; init; }

    /// <summary>extent 长度（块数，len）。Length of the extent in blocks (len).</summary>
    public uint Len { get; init; }

    /// <summary>extent 是否有效（len 非零）。Whether the extent is valid (len != 0).</summary>
    public bool IsValid => Len != 0;

    /// <summary>逻辑块号是否落在本 extent 内。Whether a logical block falls inside this extent.</summary>
    public bool Contains(uint logicalBlock) => IsValid && Fofs <= logicalBlock && logicalBlock < Fofs + Len;

    /// <summary>
    /// 将逻辑块号映射到物理块号（调用方须先经 <see cref="Contains"/> 确认范围）。
    /// <para>Maps a logical block to a physical block (callers must confirm the range with
    /// <see cref="Contains"/> first).</para>
    /// </summary>
    public uint Map(uint logicalBlock) => checked(Blk + (logicalBlock - Fofs));

    /// <summary>
    /// 从 12 字节解析 extent。
    /// <para>Parses an extent from 12 bytes.</para>
    /// </summary>
    public static F2fsExtent FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException("F2FS extent region too short.");
        }

        return new F2fsExtent
        {
            Fofs = BinaryPrimitives.ReadUInt32LittleEndian(data),
            Blk = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)),
            Len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)),
        };
    }

    /// <summary>
    /// 将 extent 序列化到缓冲区（长度至少 12 字节）。
    /// <para>Serializes the extent into a buffer of at least 12 bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Extent destination buffer is too small.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Fofs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Blk);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Len);
    }
}