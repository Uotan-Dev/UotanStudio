namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;

/// <summary>
/// Fixed constants of the flattened device tree (FDT) and DTBO overlay formats: magic values, structure
/// tokens, and on-disk sizes. All multi-byte FDT fields are big-endian.
/// <para>展平设备树（FDT）与 DTBO overlay 格式的固定常量：魔数、结构令牌与磁盘字节大小。FDT 的所有多字节字段均为大端序。</para>
/// </summary>
public static class FdtConstants
{
    /// <summary>Big-endian magic that identifies an FDT blob. <para>标识 FDT blob 的大端魔数。</para></summary>
    public const uint FdtMagic = 0xD00DFEED;

    /// <summary>Byte size of the full FDT header written by <see cref="FdtHeader.ToBytes"/>. <para><see cref="FdtHeader.ToBytes"/> 写出的完整 FDT 头部字节大小。</para></summary>
    public const int HeaderSize = 40;

    /// <summary>Specification version value written into new headers. <para>写入新头部的规范版本值。</para></summary>
    public const uint FdtVersion = 17;

    /// <summary>Highest specification version this library claims compatibility with when writing. <para>本库写出时声称兼容的最高规范版本。</para></summary>
    public const uint FdtLastCompVersion = 16;

    /// <summary>Byte alignment of tokens, property headers and property data. <para>令牌、属性头与属性数据的字节对齐值。</para></summary>
    public const int Alignment = 4;

    /// <summary>Byte alignment of the memory reservation block. <para>内存保留块（rsvmap）的字节对齐值。</para></summary>
    public const int MemoryReservationAlignment = 8;

    /// <summary>Byte size of one memory reservation entry (big-endian 64-bit address plus 64-bit size). <para>单个内存保留条目的字节大小（大端 64 位地址 + 64 位大小）。</para></summary>
    public const int MemoryReservationEntrySize = 16;

    /// <summary>Structure token that opens a node. <para>开始一个节点的结构令牌。</para></summary>
    public const uint FdtBeginNode = 0x00000001;

    /// <summary>Structure token that closes the current node. <para>结束当前节点的结构令牌。</para></summary>
    public const uint FdtEndNode = 0x00000002;

    /// <summary>Structure token that introduces a property. <para>引入一个属性的结构令牌。</para></summary>
    public const uint FdtProp = 0x00000003;

    /// <summary>Structure token that is skipped during parsing. <para>解析时跳过的结构令牌。</para></summary>
    public const uint FdtNop = 0x00000004;

    /// <summary>Structure token that terminates the structure block. <para>终止结构块的结构令牌。</para></summary>
    public const uint FdtEnd = 0x00000009;

    /// <summary>Byte size of a property header (token plus length) before its name and data. <para>属性头（令牌 + 长度）在名称与数据之前的字节大小。</para></summary>
    public const int PropertyHeaderSize = 8;

    /// <summary>Byte size of one structure token. <para>单个结构令牌的字节大小。</para></summary>
    public const int TokenSize = 4;

    /// <summary>Magic that identifies a DTBO overlay image. <para>标识 DTBO overlay 镜像的魔数。</para></summary>
    public const uint DtboMagic = 0xD7B7AB1E;

    /// <summary>Byte size of the DTBO image header. <para>DTBO 镜像头的字节大小。</para></summary>
    public const int DtboHeaderSize = 32;

    /// <summary>Byte size of one DTBO entry descriptor. <para>单个 DTBO 条目描述符的字节大小。</para></summary>
    public const int DtboEntrySize = 32;

    /// <summary>Page size assumed when none is carried by the image. <para>镜像未携带页大小时采用的默认页大小。</para></summary>
    public const uint DtboDefaultPageSize = 2048;
}

/// <summary>
/// The 40-byte big-endian header of a flattened device tree blob. Field values are raw parsed input and
/// are not validated for internal consistency by this type.
/// <para>展平设备树 blob 的 40 字节大端头部。字段值为原始解析输入，本类型不校验其内部一致性。</para>
/// </summary>
public sealed class FdtHeader
{
    /// <summary>Format magic at blob offset 0.
    /// <para>位于 blob 偏移 0 处的格式魔数。</para></summary>
    /// <value><see cref="FdtConstants.FdtMagic"/> for a valid blob; any other value fails <see cref="IsValidMagic"/>.
    /// <para>有效 blob 应为 <see cref="FdtConstants.FdtMagic"/>；其他值会使 <see cref="IsValidMagic"/> 为假。</para></value>
    public uint Magic { get; set; }

    /// <summary>Total size of the whole blob in bytes.
    /// <para>整个 blob 的总字节大小。</para></summary>
    /// <value>Byte count covering header, reservation map, structure and strings blocks.
    /// <para>覆盖头部、保留图、结构块与字符串块的字节总数。</para></value>
    public uint TotalSize { get; set; }

    /// <summary>Start of the structure block.
    /// <para>结构块的起始位置。</para></summary>
    /// <value>Byte offset from the beginning of the blob. <para>相对 blob 起始的字节偏移。</para></value>
    public uint OffsetDtStruct { get; set; }

    /// <summary>Start of the strings block.
    /// <para>字符串块的起始位置。</para></summary>
    /// <value>Byte offset from the beginning of the blob. <para>相对 blob 起始的字节偏移。</para></value>
    public uint OffsetDtStrings { get; set; }

    /// <summary>Start of the memory reservation block.
    /// <para>内存保留块（rsvmap）的起始位置。</para></summary>
    /// <value>Byte offset from the beginning of the blob. <para>相对 blob 起始的字节偏移。</para></value>
    public uint OffsetMemRsvmap { get; set; }

    /// <summary>Specification version the blob conforms to.
    /// <para>blob 遵循的规范版本。</para></summary>
    /// <value>Version number; <see cref="ReadFrom"/> only fills the trailing fields when this reaches 2, 3 or 17.
    /// <para>版本号；<see cref="ReadFrom"/> 仅在该值达到 2、3 或 17 时才读取尾部字段。</para></value>
    public uint Version { get; set; }

    /// <summary>Highest specification version known to be able to parse the blob.
    /// <para>已知能够解析该 blob 的最高规范版本。</para></summary>
    /// <value>Version number written as <see cref="FdtConstants.FdtLastCompVersion"/> by this library.
    /// <para>本库写出的版本值，即 <see cref="FdtConstants.FdtLastCompVersion"/>。</para></value>
    public uint LastCompVersion { get; set; }

    /// <summary>CPU identifier the firmware is intended to boot on.
    /// <para>该固件期望引导到的 CPU 标识。</para></summary>
    /// <value>Boot CPU id; stays <c>0</c> when the blob version is below 2 or the span is shorter than 32 bytes.
    /// <para>引导 CPU id；当 blob 版本低于 2 或跨度不足 32 字节时保持 <c>0</c>。</para></value>
    public uint BootCpuidPhys { get; set; }

    /// <summary>Size of the strings block.
    /// <para>字符串块的大小。</para></summary>
    /// <value>Byte count; stays <c>0</c> when the blob version is below 3 or the span is shorter than 36 bytes.
    /// <para>字节数；当 blob 版本低于 3 或跨度不足 36 字节时保持 <c>0</c>。</para></value>
    public uint SizeDtStrings { get; set; }

    /// <summary>Size of the structure block.
    /// <para>结构块的大小。</para></summary>
    /// <value>Byte count; stays <c>0</c> when the blob version is below 17 or the span is shorter than 40 bytes.
    /// <para>字节数；当 blob 版本低于 17 或跨度不足 40 字节时保持 <c>0</c>。</para></value>
    public uint SizeDtStruct { get; set; }

    /// <summary>Whether the magic field matches the FDT signature.
    /// <para>魔数字段是否与 FDT 签名一致。</para></summary>
    /// <value><see langword="true"/> when <see cref="Magic"/> equals <see cref="FdtConstants.FdtMagic"/>.
    /// <para>当 <see cref="Magic"/> 等于 <see cref="FdtConstants.FdtMagic"/> 时为 <see langword="true"/>。</para></value>
    public bool IsValidMagic => Magic == FdtConstants.FdtMagic;

    /// <summary>
    /// Reads an FDT header from a byte span (big-endian).
    /// <para>从字节跨度读取 FDT 头部(大端)。</para>
    /// </summary>
    /// <param name="data">
    /// Span holding at least the first 28 header bytes; trailing fields are only consumed when the version
    /// and remaining length allow it.
    /// <para>至少包含头部前 28 字节的跨度；尾部字段仅在版本与剩余长度允许时被读取。</para>
    /// </param>
    /// <returns>The parsed header. Magic is not checked; callers verify it via <see cref="IsValidMagic"/>.
    /// <para>解析后的头部；不校验魔数，调用方通过 <see cref="IsValidMagic"/> 验证。</para></returns>
    /// <exception cref="InvalidDataException">The span holds fewer than 28 bytes.
    /// <para>跨度不足 28 字节。</para></exception>
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
    /// Serializes the header to a byte array (big-endian).
    /// <para>将头部序列化为字节数组(大端)。</para>
    /// </summary>
    /// <returns>A fresh array of exactly <see cref="FdtConstants.HeaderSize"/> bytes with all ten fields laid out in header order.
    /// <para>长度恰为 <see cref="FdtConstants.HeaderSize"/> 字节的新数组，按头部顺序排列全部十个字段。</para></returns>
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
/// One entry of the FDT memory reservation block: a physical range the boot program must not overwrite.
/// <para>FDT 内存保留块（rsvmap）的一个条目：引导程序不得覆盖的物理地址区间。</para>
/// </summary>
public readonly struct MemoryReservation
{
    /// <summary>Physical start address of the reserved range.
    /// <para>保留区间的物理起始地址。</para></summary>
    /// <value>64-bit byte address. <para>64 位字节地址。</para></value>
    public ulong Address { get; init; }

    /// <summary>Size of the reserved range.
    /// <para>保留区间的大小。</para></summary>
    /// <value>Byte count starting at <see cref="Address"/>.
    /// <para>自 <see cref="Address"/> 起的字节数。</para></value>
    public ulong Size { get; init; }
}