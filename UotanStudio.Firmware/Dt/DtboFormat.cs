namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>表示 DTBO（设备树叠加镜像）的表头结构。</para>
/// Represents the header structure of a DTBO (device tree overlay image) table.
/// </summary>
/// <remarks>
/// <para>
/// 布局严格对齐 AOSP <c>dt_table.h</c> / <c>mkdtboimg.py</c>：8 个 32 位字段，
/// 全部大端（<c>cpu_to_fdt32</c>），头大小 32 字节。
/// </para>
/// <para>
/// Layout strictly follows AOSP <c>dt_table.h</c> / <c>mkdtboimg.py</c>: 8 uint32 fields,
/// all big-endian (<c>cpu_to_fdt32</c>), 32-byte header.
/// </para>
/// </remarks>
public sealed class DtboHeader
{
    /// <summary>魔数(0xD7B7AB1E)。大端存储。<para>Magic (0xD7B7AB1E), stored big-endian.</para></summary>
    public uint Magic { get; set; }

    /// <summary>镜像总大小（表头 + 全部条目 + 全部 DTB/DTBO）。<para>Total image size (header + all entries + all DTB/DTBO blobs).</para></summary>
    public uint TotalSize { get; set; }

    /// <summary>表头大小(32 字节)。<para>Header size (32 bytes).</para></summary>
    public uint HeaderSize { get; set; }

    /// <summary>单个 DT 条目大小(32 字节)。<para>Single DT entry size (32 bytes).</para></summary>
    public uint EntrySize { get; set; }

    /// <summary>DT 条目数量。<para>DT entry count.</para></summary>
    public uint EntryCount { get; set; }

    /// <summary>条目表偏移量（相对镜像头；无填充时等于 HeaderSize）。<para>Offset of the entry table (relative to the image head; equals HeaderSize when unpadded).</para></summary>
    public uint EntriesOffset { get; set; }

    /// <summary>页大小。<para>Flash page size.</para></summary>
    public uint PageSize { get; set; }

    /// <summary>表头版本(当前为 0；1 引入压缩标志)。<para>Header version (0; 1 introduces compression flags).</para></summary>
    public uint Version { get; set; }

    /// <summary>魔数是否有效。<para>Whether the magic is valid.</para></summary>
    public bool IsValidMagic => Magic == FdtConstants.DtboMagic;

    /// <summary>
    /// <para>从字节跨度读取 DTBO 表头（大端）。</para>
    /// Reads a DTBO table header from a byte span (big-endian).
    /// </summary>
    public static DtboHeader ReadFrom(ReadOnlySpan<byte> data)
    {
        if (data.Length < FdtConstants.DtboHeaderSize)
        {
            throw new InvalidDataException($"Data too small for DTBO header: {data.Length} bytes.");
        }

        var header = new DtboHeader
        {
            Magic = ReadU32BE(data),
            TotalSize = ReadU32BE(data[4..]),
            HeaderSize = ReadU32BE(data[8..]),
            EntrySize = ReadU32BE(data[12..]),
            EntryCount = ReadU32BE(data[16..]),
            EntriesOffset = ReadU32BE(data[20..]),
            PageSize = ReadU32BE(data[24..]),
            Version = ReadU32BE(data[28..])
        };

        if (header.HeaderSize < FdtConstants.DtboHeaderSize)
        {
            throw new InvalidDataException($"Invalid DTBO header size: {header.HeaderSize} (expected >= {FdtConstants.DtboHeaderSize}).");
        }
        if (header.EntrySize != FdtConstants.DtboEntrySize)
        {
            throw new InvalidDataException($"Invalid DTBO entry size: {header.EntrySize} (expected {FdtConstants.DtboEntrySize}).");
        }

        return header;
    }

    /// <summary>
    /// <para>将表头序列化为字节数组（大端）。</para>
    /// Serializes the header to a byte array (big-endian).
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[FdtConstants.DtboHeaderSize];
        WriteU32BE(data, 0, Magic);
        WriteU32BE(data, 4, TotalSize);
        WriteU32BE(data, 8, HeaderSize);
        WriteU32BE(data, 12, EntrySize);
        WriteU32BE(data, 16, EntryCount);
        WriteU32BE(data, 20, EntriesOffset);
        WriteU32BE(data, 24, PageSize);
        WriteU32BE(data, 28, Version);
        return data;
    }

    private static uint ReadU32BE(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];

    private static void WriteU32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}

/// <summary>
/// <para>DTBO 表中的单个条目描述符（32 字节，大端）。</para>
/// A single entry descriptor in a DTBO table (32 bytes, big-endian).
/// </summary>
/// <remarks>
/// <para>
/// 对齐 AOSP <c>dt_table_entry</c>（v0）/ <c>dt_table_entry_v1</c>（v1）：
/// 前四个字段固定，后四个为 custom 字段；v1 的第一个 custom 字段承载压缩标志
/// （低 4 位，<c>dt_compression_info</c>：0=无/1=zlib/2=gzip）。
/// </para>
/// <para>
/// Mirrors AOSP <c>dt_table_entry</c> (v0) / <c>dt_table_entry_v1</c> (v1): four fixed
/// fields followed by four custom fields; for v1 the first custom field carries the
/// compression flags (low 4 bits per <c>dt_compression_info</c>: 0=none/1=zlib/2=gzip).
/// </para>
/// </remarks>
public sealed class DtboEntry
{
    /// <summary>DTB/DTBO 数据大小(字节)。<para>Blob size in bytes.</para></summary>
    public uint DtSize { get; set; }

    /// <summary>数据在镜像中的偏移量（相对镜像头）。<para>Blob offset (relative to the image head).</para></summary>
    public uint DtOffset { get; set; }

    /// <summary>可选镜像 ID。<para>Optional image ID.</para></summary>
    public uint Id { get; set; }

    /// <summary>可选修订号。<para>Optional revision.</para></summary>
    public uint Rev { get; set; }

    /// <summary>Custom 字段 0（v1 中为压缩标志）。<para>Custom field 0 (compression flags in v1).</para></summary>
    public uint Custom0 { get; set; }

    /// <summary>Custom 字段 1。<para>Custom field 1.</para></summary>
    public uint Custom1 { get; set; }

    /// <summary>Custom 字段 2。<para>Custom field 2.</para></summary>
    public uint Custom2 { get; set; }

    /// <summary>Custom 字段 3。<para>Custom field 3.</para></summary>
    public uint Custom3 { get; set; }

    /// <summary>解析条目（大端 32 字节）。<para>Parses an entry (big-endian, 32 bytes).</para></summary>
    public static DtboEntry ReadFrom(ReadOnlySpan<byte> data)
    {
        if (data.Length < FdtConstants.DtboEntrySize)
        {
            throw new InvalidDataException($"Data too small for DTBO entry: {data.Length} bytes.");
        }

        return new DtboEntry
        {
            DtSize = ReadU32BE(data),
            DtOffset = ReadU32BE(data[4..]),
            Id = ReadU32BE(data[8..]),
            Rev = ReadU32BE(data[12..]),
            Custom0 = ReadU32BE(data[16..]),
            Custom1 = ReadU32BE(data[20..]),
            Custom2 = ReadU32BE(data[24..]),
            Custom3 = ReadU32BE(data[28..])
        };
    }

    /// <summary>序列化条目（大端 32 字节）。<para>Serializes the entry (big-endian, 32 bytes).</para></summary>
    public byte[] ToBytes()
    {
        var data = new byte[FdtConstants.DtboEntrySize];
        WriteU32BE(data, 0, DtSize);
        WriteU32BE(data, 4, DtOffset);
        WriteU32BE(data, 8, Id);
        WriteU32BE(data, 12, Rev);
        WriteU32BE(data, 16, Custom0);
        WriteU32BE(data, 20, Custom1);
        WriteU32BE(data, 24, Custom2);
        WriteU32BE(data, 28, Custom3);
        return data;
    }

    private static uint ReadU32BE(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];

    private static void WriteU32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}

/// <summary>
/// <para>提供 DTBO（Android DT 表镜像）的解析与打包，布局对齐 AOSP mkdtboimg。</para>
/// Provides parsing and packing for DTBO (Android DT table images), aligned with AOSP mkdtboimg.
/// </summary>
public static class DtboFormat
{
    /// <summary>dt_compression_info：无压缩。<para>dt_compression_info: no compression.</para></summary>
    public const uint CompressionNone = 0;

    /// <summary>dt_compression_info：zlib。<para>dt_compression_info: zlib.</para></summary>
    public const uint CompressionZlib = 1;

    /// <summary>dt_compression_info：gzip。<para>dt_compression_info: gzip.</para></summary>
    public const uint CompressionGzip = 2;

    /// <summary>压缩标志掩码（v1 flags 低 4 位）。<para>Compression flag mask (low 4 bits of v1 flags).</para></summary>
    public const uint CompressionFlagMask = 0xF;

    /// <summary>解压输出的默认上限（DTB/DTBO 通常远小于此值）。<para>Default decompressed-output cap (DTB/DTBO blobs are far smaller in practice).</para></summary>
    public const long DefaultMaximumDecompressedBytes = 256 * 1024 * 1024;

    /// <summary>
    /// <para>解析 DTBO 镜像，返回表头、条目表与（v1 压缩时解压后的）DTB 列表。</para>
    /// Parses a DTBO image, returning the header, the entry table and the (decompressed for v1) DTB blobs.
    /// </summary>
    public static (DtboHeader Header, List<DtboEntry> Entries, List<byte[]> Dtbs) Read(byte[] data, long maximumDecompressedBytes = DefaultMaximumDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);

        var header = DtboHeader.ReadFrom(data);
        if (!header.IsValidMagic)
        {
            throw new InvalidDataException($"Invalid DTBO magic: 0x{header.Magic:X8} (expected 0x{FdtConstants.DtboMagic:X8})");
        }

        var entries = new List<DtboEntry>();
        for (uint i = 0; i < header.EntryCount; i++)
        {
            var entryOffset = checked((int)(header.EntriesOffset + (i * header.EntrySize)));
            if (entryOffset < 0 || (long)entryOffset + FdtConstants.DtboEntrySize > data.Length)
            {
                throw new InvalidDataException($"DTBO entry table out of bounds: entry {i} at offset {entryOffset}.");
            }
            entries.Add(DtboEntry.ReadFrom(data.AsSpan(entryOffset)));
        }

        var dtbs = new List<byte[]>();
        foreach (var entry in entries)
        {
            var blobOffset = entry.DtOffset;
            var blobSize = entry.DtSize;
            if (blobSize == 0)
            {
                dtbs.Add([]);
                continue;
            }
            if (blobOffset > (ulong)data.Length || blobSize > (ulong)data.Length - blobOffset)
            {
                throw new InvalidDataException($"DTBO entry blob out of bounds: offset {blobOffset}, size {blobSize}.");
            }

            var blob = data.AsSpan((int)blobOffset, (int)blobSize).ToArray();
            var compression = header.Version >= 1 ? (entry.Custom0 & CompressionFlagMask) : CompressionNone;
            dtbs.Add(compression switch
            {
                CompressionNone => blob,
                CompressionZlib => DecompressBounded(blob, zlib: true, maximumDecompressedBytes),
                CompressionGzip => DecompressBounded(blob, zlib: false, maximumDecompressedBytes),
                _ => throw new InvalidDataException($"Unknown DTBO compression flags: 0x{compression:X}.")
            });
        }

        return (header, entries, dtbs);
    }

    /// <summary>
    /// <para>将多个 DTB 打包为 DTBO 镜像（连续布局：表头 + 条目表 + 数据块，对齐 mkdtboimg）。</para>
    /// Packs multiple DTBs into a DTBO image (contiguous layout: header + entry table + blobs, per mkdtboimg).
    /// </summary>
    public static byte[] Pack(IReadOnlyList<byte[]> dtbs, uint pageSize = FdtConstants.DtboDefaultPageSize, uint version = 0)
    {
        ArgumentNullException.ThrowIfNull(dtbs);
        if (dtbs.Count == 0)
        {
            throw new ArgumentException("At least one DTB is required.", nameof(dtbs));
        }
        if (dtbs.Any(d => d is null))
        {
            throw new ArgumentException("DTB blobs must not be null.", nameof(dtbs));
        }
        if (version > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Only DTBO table versions 0 and 1 are supported.");
        }

        var headerSize = (uint)FdtConstants.DtboHeaderSize;
        var entrySize = (uint)FdtConstants.DtboEntrySize;
        // mkdtboimg 布局：条目表紧跟表头（dt_entries_offset = header_size），
        // 数据块从 header_size + count*entry_size 起连续存放。
        // mkdtboimg layout: the entry table directly follows the header
        // (dt_entries_offset = header_size); blobs start contiguously after the table.
        var blobOffset = checked(headerSize + (uint)dtbs.Count * entrySize);
        uint totalSize = blobOffset;
        foreach (var dtb in dtbs)
        {
            totalSize = checked(totalSize + (uint)dtb.Length);
        }

        var header = new DtboHeader
        {
            Magic = FdtConstants.DtboMagic,
            TotalSize = totalSize,
            HeaderSize = headerSize,
            EntrySize = entrySize,
            EntryCount = (uint)dtbs.Count,
            EntriesOffset = headerSize,
            PageSize = pageSize,
            Version = version
        };

        using var ms = new MemoryStream();
        ms.Write(header.ToBytes());

        var offset = blobOffset;
        foreach (var dtb in dtbs)
        {
            ms.Write(new DtboEntry
            {
                DtSize = (uint)dtb.Length,
                DtOffset = offset,
            }.ToBytes());
            offset = checked(offset + (uint)dtb.Length);
        }

        foreach (var dtb in dtbs)
        {
            ms.Write(dtb);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// <para>将单个 DTB 打包为 DTBO 镜像。</para>
    /// Packs a single DTB into a DTBO image.
    /// </summary>
    public static byte[] Pack(byte[] dtb, uint pageSize = FdtConstants.DtboDefaultPageSize, uint version = 0)
    {
        ArgumentNullException.ThrowIfNull(dtb);
        return Pack([dtb], pageSize, version);
    }

    private static byte[] DecompressBounded(byte[] blob, bool zlib, long maximumDecompressedBytes)
    {
        using var input = new MemoryStream(blob, writable: false);
        using Stream decompressor = zlib
            ? new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress)
            : new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maximumDecompressedBytes)
            {
                throw new InvalidDataException($"Decompressed DTBO blob exceeds the limit of {maximumDecompressedBytes} bytes.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
