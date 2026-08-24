namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>表示 DTBO(设备树叠加镜像)的头部结构。</para>
/// Represents the header structure of a DTBO (device tree overlay image).
/// </summary>
public sealed class DtboHeader
{
    /// <summary>魔数(0xD7B7AB1E)。</summary>
    public uint Magic { get; set; }

    /// <summary>头部大小(字节)。</summary>
    public uint HeaderSize { get; set; }

    /// <summary>头部版本。</summary>
    public uint HeaderVersion { get; set; }

    /// <summary>单个 DT 条目大小(字节)。</summary>
    public uint EntrySize { get; set; }

    /// <summary>DT 条目数量。</summary>
    public uint EntryCount { get; set; }

    /// <summary>DT 条目表偏移量。</summary>
    public uint EntriesOffset { get; set; }

    /// <summary>页面大小。</summary>
    public uint PageSize { get; set; }

    /// <summary>DTBO 版本。</summary>
    public uint Version { get; set; }

    /// <summary>魔数是否有效。</summary>
    public bool IsValidMagic => Magic == FdtConstants.DtboMagic;

    /// <summary>
    /// <para>从字节跨度读取 DTBO 头部(小端)。</para>
    /// Reads a DTBO header from a byte span (little-endian).
    /// </summary>
    public static DtboHeader ReadFrom(ReadOnlySpan<byte> data)
    {
        return data.Length < FdtConstants.DtboHeaderSize
            ? throw new InvalidDataException($"Data too small for DTBO header: {data.Length} bytes.")
            : new DtboHeader
            {
                Magic = ReadU32LE(data),
                HeaderSize = ReadU32LE(data[4..]),
                HeaderVersion = ReadU32LE(data[8..]),
                EntrySize = ReadU32LE(data[12..]),
                EntryCount = ReadU32LE(data[16..]),
                EntriesOffset = ReadU32LE(data[20..]),
                PageSize = ReadU32LE(data[24..]),
                Version = ReadU32LE(data[28..])
            };
    }

    /// <summary>
    /// <para>将头部序列化为字节数组(小端)。</para>
    /// Serializes the header to a byte array (little-endian).
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[FdtConstants.DtboHeaderSize];
        WriteU32LE(data, 0, Magic);
        WriteU32LE(data, 4, HeaderSize);
        WriteU32LE(data, 8, HeaderVersion);
        WriteU32LE(data, 12, EntrySize);
        WriteU32LE(data, 16, EntryCount);
        WriteU32LE(data, 20, EntriesOffset);
        WriteU32LE(data, 24, PageSize);
        WriteU32LE(data, 28, Version);
        // Remaining 8 bytes reserved (v2 fields) remain zero.
        return data;
    }

    private static uint ReadU32LE(ReadOnlySpan<byte> data) =>
        (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

    private static void WriteU32LE(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}

/// <summary>
/// <para>提供 DTBO(设备树叠加镜像)格式的读取能力。</para>
/// Provides reading capability for the DTBO (device tree overlay image) format.
/// </summary>
public static class DtboFormat
{
    /// <summary>
    /// <para>解析 DTBO 镜像,返回头部与内部 DTB 列表。</para>
    /// Parses a DTBO image, returning the header and the list of inner DTBs.
    /// </summary>
    public static (DtboHeader Header, List<byte[]> Dtbs) Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var header = DtboHeader.ReadFrom(data);
        if (!header.IsValidMagic)
        {
            throw new InvalidDataException($"Invalid DTBO magic: 0x{header.Magic:X8} (expected 0x{FdtConstants.DtboMagic:X8})");
        }

        var dtbs = new List<byte[]>();
        for (uint i = 0; i < header.EntryCount; i++)
        {
            var entryOffset = (int)(header.EntriesOffset + (i * header.EntrySize));
            if (entryOffset + FdtConstants.DtboHeaderSize > data.Length)
            {
                break;
            }

            // Each entry is itself a DTB starting with an FDT header (big-endian magic 0xD00DFEED).
            var entryMagic = (uint)((data[entryOffset] << 24) | (data[entryOffset + 1] << 16) | (data[entryOffset + 2] << 8) | data[entryOffset + 3]);
            if (entryMagic != FdtConstants.FdtMagic)
            {
                break;
            }

            var totalSize = (data[entryOffset + 4] << 24) | (data[entryOffset + 5] << 16) | (data[entryOffset + 6] << 8) | data[entryOffset + 7];
            if (entryOffset + totalSize > data.Length)
            {
                totalSize = data.Length - entryOffset;
            }

            dtbs.Add(data.AsSpan(entryOffset, totalSize).ToArray());
        }

        return (header, dtbs);
    }

    /// <summary>
    /// <para>将单个 DTB 打包为 DTBO 镜像。</para>
    /// Packs a single DTB into a DTBO image.
    /// </summary>
    public static byte[] Pack(byte[] dtb, uint pageSize = 2048)
    {
        ArgumentNullException.ThrowIfNull(dtb);

        var header = new DtboHeader
        {
            Magic = FdtConstants.DtboMagic,
            HeaderSize = FdtConstants.DtboHeaderSize,
            HeaderVersion = 1,
            EntrySize = (uint)dtb.Length,
            EntryCount = 1,
            EntriesOffset = FdtConstants.DtboHeaderSize,
            PageSize = pageSize,
            Version = 0
        };

        var result = new byte[FdtConstants.DtboHeaderSize + dtb.Length];
        header.ToBytes().CopyTo(result, 0);
        dtb.CopyTo(result, FdtConstants.DtboHeaderSize);
        return result;
    }
}