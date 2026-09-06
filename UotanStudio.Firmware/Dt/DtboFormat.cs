namespace UotanStudio.Firmware.Dt;

public sealed class DtboHeader
{
    public uint Magic { get; set; }

    public uint TotalSize { get; set; }

    public uint HeaderSize { get; set; }

    public uint EntrySize { get; set; }

    public uint EntryCount { get; set; }

    public uint EntriesOffset { get; set; }

    public uint PageSize { get; set; }

    public uint Version { get; set; }

    public bool IsValidMagic => Magic == FdtConstants.DtboMagic;

    /// <summary>
    /// Reads a DTBO table header from a byte span (big-endian).
    /// <para>从字节跨度读取 DTBO 表头（大端）。</para>
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
    /// Serializes the header to a byte array (big-endian).
    /// <para>将表头序列化为字节数组（大端）。</para>
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

public sealed class DtboEntry
{
    public uint DtSize { get; set; }

    public uint DtOffset { get; set; }

    public uint Id { get; set; }

    public uint Rev { get; set; }

    public uint Custom0 { get; set; }

    public uint Custom1 { get; set; }

    public uint Custom2 { get; set; }

    public uint Custom3 { get; set; }

    /// <summary>
    /// Parses an entry (big-endian, 32 bytes).
    /// <para>解析条目（大端 32 字节）。</para>
    /// </summary>
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

    /// <summary>
    /// Serializes the entry (big-endian, 32 bytes).
    /// <para>序列化条目（大端 32 字节）。</para>
    /// </summary>
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

public static class DtboFormat
{
    public const uint CompressionNone = 0;

    public const uint CompressionZlib = 1;

    public const uint CompressionGzip = 2;

    public const uint CompressionFlagMask = 0xF;

    public const long DefaultMaximumDecompressedBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Parses a DTBO image, returning the header, the entry table and the (decompressed for v1) DTB blobs.
    /// <para>解析 DTBO 镜像，返回表头、条目表与（v1 压缩时解压后的）DTB 列表。</para>
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
    /// Packs multiple DTBs into a DTBO image (contiguous layout: header + entry table + blobs, per mkdtboimg).
    /// <para>将多个 DTB 打包为 DTBO 镜像（连续布局：表头 + 条目表 + 数据块，对齐 mkdtboimg）。</para>
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
    /// Packs a single DTB into a DTBO image.
    /// <para>将单个 DTB 打包为 DTBO 镜像。</para>
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
