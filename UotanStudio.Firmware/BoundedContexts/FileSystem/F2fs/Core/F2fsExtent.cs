using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

internal sealed record F2fsExtent
{
    public const int Size = 12;

    public const int InodeOffset = 348;

    public uint Fofs { get; init; }

    public uint Blk { get; init; }

    public uint Len { get; init; }

    public bool IsValid => Len != 0;

    /// <summary>
    /// Whether a logical block falls inside this extent.
    /// <para>逻辑块号是否落在本 extent 内。</para>
    /// </summary>
    public bool Contains(uint logicalBlock) => IsValid && Fofs <= logicalBlock && logicalBlock < Fofs + Len;

    /// <summary>
    /// Maps a logical block to a physical block (callers must confirm the range with Contains first).
    /// <para>将逻辑块号映射到物理块号（调用方须先经 Contains 确认范围）。</para>
    /// </summary>
    public uint Map(uint logicalBlock) => checked(Blk + (logicalBlock - Fofs));

    /// <summary>
    /// Parses an extent from 12 bytes.
    /// <para>从 12 字节解析 extent。</para>
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
    /// Serializes the extent into a buffer of at least 12 bytes.
    /// <para>将 extent 序列化到缓冲区（长度至少 12 字节）。</para>
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