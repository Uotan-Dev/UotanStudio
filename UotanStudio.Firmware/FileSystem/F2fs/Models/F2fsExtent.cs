using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

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
    /// <param name="logicalBlock">File-relative logical block index tested against the half-open range starting at the extent's first block.
    /// <para>文件内相对逻辑块序号，与从该 extent 首块开始的半开区间比较。</para></param>
    /// <returns><see langword="true"/> only for an extent with a non-zero length that covers the block; a zero-length extent contains nothing.
    /// <para>仅当 extent 长度非零且覆盖该块时为 <see langword="true"/>；长度为 0 的 extent 不含任何块。</para></returns>
    public bool Contains(uint logicalBlock) => IsValid && Fofs <= logicalBlock && logicalBlock < Fofs + Len;

    /// <summary>
    /// Maps a logical block to a physical block (callers must confirm the range with Contains first).
    /// <para>将逻辑块号映射到物理块号（调用方须先经 Contains 确认范围）。</para>
    /// </summary>
    /// <param name="logicalBlock">Logical block already established to lie inside the extent; a value below the extent start wraps the difference and the checked addition then overflows.
    /// <para>已被确认落在该 extent 范围内的逻辑块号；小于起始块时差值会回绕，随后带检查的相加会溢出。</para></param>
    /// <returns>Physical block number of <paramref name="logicalBlock"/>, the extent's start block offset by its distance from <c>Fofs</c>.
    /// <para><paramref name="logicalBlock"/> 对应的物理块号，即该 extent 起始块加上它与 <c>Fofs</c> 的距离。</para></returns>
    public uint Map(uint logicalBlock) => checked(Blk + (logicalBlock - Fofs));

    /// <summary>
    /// Parses an extent from 12 bytes.
    /// <para>从 12 字节解析 extent。</para>
    /// </summary>
    /// <param name="data">Extent bytes, at least <see cref="Size"/> long, holding the little-endian first logical block, start physical block and block count in that order.
    /// <para>extent 字节，长度至少为 <see cref="Size"/>，依次是小端序的起始逻辑块、起始物理块与块数。</para></param>
    /// <returns>Extent record read verbatim from disk; a zero length leaves <c>IsValid</c> false.
    /// <para>按磁盘原样读出的 extent 记录；长度为 0 时 <c>IsValid</c> 为假。</para></returns>
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
    /// <param name="destination">Buffer receiving the three little-endian uint32 fields at offsets 0, 4 and 8; it must offer at least <see cref="Size"/> writable bytes and any bytes past offset 12 are left untouched.
    /// <para>接收三个小端 uint32 字段的缓冲区，分别写在偏移 0、4、8；可写字节至少为 <see cref="Size"/>，偏移 12 之后的内容不会被改动。</para></param>
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