using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtbo;

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

public static partial class DtboFormat
{
    public const uint CompressionNone = 0;

    public const uint CompressionZlib = 1;

    public const uint CompressionGzip = 2;

    public const uint CompressionFlagMask = 0xF;

    public const long DefaultMaximumDecompressedBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Parses a bounded in-memory image; the output budget applies to all decoded blobs combined.
    /// <para>解析有界内存镜像，输出预算限制全部解码数据块的总大小。</para>
    /// </summary>
    public static (DtboHeader Header, List<DtboEntry> Entries, List<byte[]> Dtbs) Read(byte[] data, long maximumDecompressedBytes = DefaultMaximumDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var input = new MemoryStream(data, writable: false);
        var (header, entries) = ReadTable(input);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecompressedBytes);

        var dtbs = new List<byte[]>();
        var remaining = maximumDecompressedBytes;
        foreach (var entry in entries)
        {
            using var output = new MemoryStream();
            CopyBlob(input, header.Version, entry, output, remaining, CancellationToken.None);
            remaining -= output.Length;
            dtbs.Add(output.ToArray());
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
        if (dtbs.Count > MaximumEntries)
            throw new ArgumentException("DTBO entry count exceeds 65536.", nameof(dtbs));
        return Pack(dtbs, dtbs.Select(_ => new DtboEntry()).ToArray(), pageSize, version);
    }

    /// <summary>
    /// Packs bounded in-memory DTBs with entry metadata and v1 compression flags; offsets and sizes are recalculated.
    /// <para>将有界内存 DTB 按条目元数据及 v1 压缩标志打包，重新计算偏移和大小。</para>
    /// </summary>
    public static byte[] Pack(IReadOnlyList<byte[]> dtbs, IReadOnlyList<DtboEntry> entries,
        uint pageSize = FdtConstants.DtboDefaultPageSize, uint version = 0)
    {
        ArgumentNullException.ThrowIfNull(dtbs);
        ArgumentNullException.ThrowIfNull(entries);
        if (dtbs.Count == 0 || dtbs.Count > MaximumEntries || entries.Count != dtbs.Count || entries.Any(e => e is null))
        {
            throw new ArgumentException("DTBO requires 1..65536 DTBs and matching metadata entries.", nameof(dtbs));
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
        if (dtbs.Sum(d => (long)d.Length) > DefaultMaximumDecompressedBytes)
            throw new InvalidDataException("In-memory DTBO packing is limited to 256 MiB; use PackDirectory for large images.");

        var header = new DtboHeader
        {
            Magic = FdtConstants.DtboMagic,
            TotalSize = 0,
            HeaderSize = headerSize,
            EntrySize = entrySize,
            EntryCount = (uint)dtbs.Count,
            EntriesOffset = headerSize,
            PageSize = pageSize,
            Version = version
        };

        using var ms = new MemoryStream();
        ms.SetLength(blobOffset);
        ms.Position = blobOffset;
        var packedEntries = new List<DtboEntry>(dtbs.Count);
        for (var i = 0; i < dtbs.Count; i++)
        {
            var entry = DtboEntry.ReadFrom(entries[i].ToBytes());
            entry.DtOffset = checked((uint)ms.Position);
            using (var encoder = CreateEncoder(ms, CompressionOf(version, entry)))
                (encoder ?? ms).Write(dtbs[i]);
            entry.DtSize = checked((uint)(ms.Position - entry.DtOffset));
            packedEntries.Add(entry);
        }
        header.TotalSize = checked((uint)ms.Length);
        ms.Position = 0;
        ms.Write(header.ToBytes());
        foreach (var entry in packedEntries)
            ms.Write(entry.ToBytes());
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

}