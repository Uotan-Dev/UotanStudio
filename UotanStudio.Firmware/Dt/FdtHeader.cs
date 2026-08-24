namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>提供扁平设备树(FDT)二进制格式的常量。</para>
/// Provides constants for the Flattened Device Tree (FDT) binary format.
/// </summary>
public static class FdtConstants
{
    /// <summary>标识有效扁平设备树的 FDT 魔数(0xD00DFEED)。</summary>
    public const uint FdtMagic = 0xD00DFEED;

    /// <summary>版本 17 及更高版本的 FDT 头部大小(40 字节)。</summary>
    public const int HeaderSize = 40;

    /// <summary>当前 FDT 格式版本(17)。</summary>
    public const uint FdtVersion = 17;

    /// <summary>最后兼容的 FDT 格式版本(16)。</summary>
    public const uint FdtLastCompVersion = 16;

    /// <summary>结构块数据的字节对齐(4 字节)。</summary>
    public const int Alignment = 4;

    /// <summary>内存保留块对齐(8 字节)。</summary>
    public const int MemoryReservationAlignment = 8;

    /// <summary>单个内存保留条目大小(16 字节)。</summary>
    public const int MemoryReservationEntrySize = 16;

    /// <summary>结构块标记:节点开始(0x00000001)。</summary>
    public const uint FdtBeginNode = 0x00000001;

    /// <summary>结构块标记:节点结束(0x00000002)。</summary>
    public const uint FdtEndNode = 0x00000002;

    /// <summary>结构块标记:属性(0x00000003)。</summary>
    public const uint FdtProp = 0x00000003;

    /// <summary>结构块标记:无操作(0x00000004)。</summary>
    public const uint FdtNop = 0x00000004;

    /// <summary>结构块标记:结束(0x00000009)。</summary>
    public const uint FdtEnd = 0x00000009;

    /// <summary>属性头大小(8 字节:4 长度 + 4 名称偏移)。</summary>
    public const int PropertyHeaderSize = 8;

    /// <summary>FDT 标记字节大小(4 字节)。</summary>
    public const int TokenSize = 4;

    /// <summary>DTBO 镜像魔数(0xD7B7AB1E)。</summary>
    public const uint DtboMagic = 0xD7B7AB1E;

    /// <summary>DTBO 头部固定大小(40 字节)。</summary>
    public const int DtboHeaderSize = 40;
}

/// <summary>
/// <para>表示扁平设备树(FDT)头部结构。</para>
/// Represents the Flattened Device Tree (FDT) header structure.
/// </summary>
public sealed class FdtHeader
{
    /// <summary>魔数。</summary>
    public uint Magic { get; set; }

    /// <summary>设备树 blob 的总大小(字节)。</summary>
    public uint TotalSize { get; set; }

    /// <summary>结构块偏移量。</summary>
    public uint OffsetDtStruct { get; set; }

    /// <summary>字符串块偏移量。</summary>
    public uint OffsetDtStrings { get; set; }

    /// <summary>内存保留映射偏移量。</summary>
    public uint OffsetMemRsvmap { get; set; }

    /// <summary>设备树结构版本。</summary>
    public uint Version { get; set; }

    /// <summary>最后兼容版本。</summary>
    public uint LastCompVersion { get; set; }

    /// <summary>引导 CPU 物理 ID。</summary>
    public uint BootCpuidPhys { get; set; }

    /// <summary>字符串块大小(字节)。</summary>
    public uint SizeDtStrings { get; set; }

    /// <summary>结构块大小(字节)。</summary>
    public uint SizeDtStruct { get; set; }

    /// <summary>魔数是否有效。</summary>
    public bool IsValidMagic => Magic == FdtConstants.FdtMagic;

    /// <summary>
    /// <para>从字节跨度读取 FDT 头部(大端)。</para>
    /// Reads an FDT header from a byte span (big-endian).
    /// </summary>
    public static FdtHeader ReadFrom(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28)
        {
            throw new InvalidDataException($"Data too small for FDT header: {data.Length} bytes.");
        }

        var header = new FdtHeader
        {
            Magic = ReadU32BE(data),
            TotalSize = ReadU32BE(data[4..]),
            OffsetDtStruct = ReadU32BE(data[8..]),
            OffsetDtStrings = ReadU32BE(data[12..]),
            OffsetMemRsvmap = ReadU32BE(data[16..]),
            Version = ReadU32BE(data[20..]),
            LastCompVersion = ReadU32BE(data[24..])
        };

        if (header.Version >= 2 && data.Length >= 32)
        {
            header.BootCpuidPhys = ReadU32BE(data[28..]);
        }
        if (header.Version >= 3 && data.Length >= 36)
        {
            header.SizeDtStrings = ReadU32BE(data[32..]);
        }
        if (header.Version >= 17 && data.Length >= 40)
        {
            header.SizeDtStruct = ReadU32BE(data[36..]);
        }

        return header;
    }

    /// <summary>
    /// <para>将头部序列化为字节数组(大端)。</para>
    /// Serializes the header to a byte array (big-endian).
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[FdtConstants.HeaderSize];
        WriteU32BE(data, 0, Magic);
        WriteU32BE(data, 4, TotalSize);
        WriteU32BE(data, 8, OffsetDtStruct);
        WriteU32BE(data, 12, OffsetDtStrings);
        WriteU32BE(data, 16, OffsetMemRsvmap);
        WriteU32BE(data, 20, Version);
        WriteU32BE(data, 24, LastCompVersion);
        WriteU32BE(data, 28, BootCpuidPhys);
        WriteU32BE(data, 32, SizeDtStrings);
        WriteU32BE(data, 36, SizeDtStruct);
        return data;
    }

    internal static uint ReadU32BE(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);

    internal static void WriteU32BE(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}

/// <summary>
/// <para>表示设备树中的内存保留条目。</para>
/// Represents a memory reservation entry in a device tree.
/// </summary>
public readonly struct MemoryReservation
{
    /// <summary>保留内存起始地址。</summary>
    public ulong Address { get; init; }

    /// <summary>保留内存大小。</summary>
    public ulong Size { get; init; }
}