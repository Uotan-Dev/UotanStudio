namespace UotanStudio.Firmware.DeviceTree.Dt;

/// <summary>
/// Fixed-layout DTBO table header describing entry geometry, blob offsets and format version.
/// <para>固定布局的 DTBO 表头，描述条目几何、数据块偏移与格式版本。</para>
/// </summary>
public sealed class DtboHeader
{
    /// <summary>Big-endian table magic read from the first four bytes.
    /// <para>从前四字节读取的大端序表魔数。</para></summary>
    /// <value>Valid tables match <see cref="FdtConstants.DtboMagic"/>; <see cref="IsValidMagic"/> reports the check.
    /// <para>有效表需与 <see cref="FdtConstants.DtboMagic"/> 一致；<see cref="IsValidMagic"/> 给出该判断结果。</para></value>
    public uint Magic { get; set; }

    /// <summary>Declared total size in bytes of the whole DTBO image, including trailing padding.
    /// <para>整个 DTBO 镜像声明的总大小（字节），含尾部填充。</para></summary>
    /// <value>Upper bound used when validating the entry table and blob ranges.
    /// <para>校验条目表和数据块范围时使用的上界。</para></value>
    public uint TotalSize { get; set; }

    /// <summary>Size in bytes of the header region at the start of the image.
    /// <para>镜像起始处表头区域的大小（字节）。</para></summary>
    /// <value>At least 32; larger values mark an extended header preserved on repack.
    /// <para>至少为 32；更大的值表示扩展表头，重打包时会予以保留。</para></value>
    public uint HeaderSize { get; set; }

    /// <summary>Recorded byte size of one table entry.
    /// <para>单个表项的字节大小记录值。</para></summary>
    /// <value>Must equal <see cref="FdtConstants.DtboEntrySize"/>; other values are rejected on read.
    /// <para>必须等于 <see cref="FdtConstants.DtboEntrySize"/>；读取时拒绝其他值。</para></value>
    public uint EntrySize { get; set; }

    /// <summary>Number of overlay entries described by the table.
    /// <para>表所描述的 overlay 条目数量。</para></summary>
    /// <value>Table reading rejects values above 65536. <para>读取表条目时会拒绝大于 65536 的值。</para></value>
    public uint EntryCount { get; set; }

    /// <summary>Absolute offset from the start of the image to the entry table.
    /// <para>自镜像起始到条目表的绝对偏移。</para></summary>
    /// <value>Never smaller than <see cref="HeaderSize"/>; blobs must start after the table ends.
    /// <para>不得小于 <see cref="HeaderSize"/>；数据块必须在表结束之后开始。</para></value>
    public uint EntriesOffset { get; set; }

    /// <summary>Partition page size recorded by the producing tool.
    /// <para>由生成工具记录的分区页大小。</para></summary>
    /// <value>Metadata in bytes; the contiguous layout does not pad blobs to it.
    /// <para>以字节为单位的元数据；连续布局不会按此值填充数据块。</para></value>
    public uint PageSize { get; set; }

    /// <summary>DTBO table format version.
    /// <para>DTBO 表格式版本。</para></summary>
    /// <value><c>0</c> or <c>1</c>; version <c>1</c> carries compression flags in <see cref="DtboEntry.Custom0"/>.
    /// <para><c>0</c> 或 <c>1</c>；版本 <c>1</c> 在 <see cref="DtboEntry.Custom0"/> 中携带压缩标志。</para></value>
    public uint Version { get; set; }

    /// <summary>Whether <see cref="Magic"/> carries the DTBO table magic.
    /// <para>判断 <see cref="Magic"/> 是否为 DTBO 表魔数。</para></summary>
    /// <value><see langword="true"/> when the magic matches <see cref="FdtConstants.DtboMagic"/>.
    /// <para>当魔数与 <see cref="FdtConstants.DtboMagic"/> 一致时为 <see langword="true"/>。</para></value>
    public bool IsValidMagic => Magic == FdtConstants.DtboMagic;

    /// <summary>
    /// Reads a DTBO table header from a byte span (big-endian).
    /// <para>从字节跨度读取 DTBO 表头（大端）。</para>
    /// </summary>
    /// <param name="data">Span holding at least the fixed 32-byte header. <para>至少包含固定 32 字节表头的跨度。</para></param>
    /// <returns>The parsed header. <para>解析后的表头。</para></returns>
    /// <exception cref="InvalidDataException">The span is shorter than the header, or header_size/entry_size are invalid.
    /// <para>跨度短于表头，或 header_size/entry_size 无效。</para></exception>
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
    /// <returns>Exactly <see cref="FdtConstants.DtboHeaderSize"/> bytes with all fields written in layout order.
    /// <para>恰好 <see cref="FdtConstants.DtboHeaderSize"/> 字节，按布局顺序写入全部字段。</para></returns>
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
/// Fixed 32-byte DTBO table entry locating one overlay DTB blob and carrying its metadata.
/// <para>固定 32 字节的 DTBO 表项，定位一个 overlay DTB 数据块并携带其元数据。</para>
/// </summary>
public sealed class DtboEntry
{
    /// <summary>Byte size of the stored blob, compressed or not.
    /// <para>所存数据块的字节大小，无论是否压缩。</para></summary>
    /// <value>Range length read from <see cref="DtOffset"/>; recomputed when blobs are re-encoded.
    /// <para>从 <see cref="DtOffset"/> 读取的长度范围；重新编码数据块时会重新计算。</para></value>
    public uint DtSize { get; set; }

    /// <summary>Absolute offset from the start of the image to this blob.
    /// <para>自镜像起始到该数据块的绝对偏移。</para></summary>
    /// <value>Must lie after the entry table and inside <see cref="DtboHeader.TotalSize"/>.
    /// <para>必须位于条目表之后且在 <see cref="DtboHeader.TotalSize"/> 之内。</para></value>
    public uint DtOffset { get; set; }

    /// <summary>Overlay identifier chosen by the producing tool.
    /// <para>由生成工具选择的 overlay 标识符。</para></summary>
    /// <value>Opaque on read; preserved verbatim unless rebuilt by packing.
    /// <para>读取时不解释含义；除非由打包重建，否则原样保留。</para></value>
    public uint Id { get; set; }

    /// <summary>Overlay revision chosen by the producing tool.
    /// <para>由生成工具选择的 overlay 修订号。</para></summary>
    /// <value>Opaque on read; preserved verbatim unless rebuilt by packing.
    /// <para>读取时不解释含义；除非由打包重建，否则原样保留。</para></value>
    public uint Rev { get; set; }

    /// <summary>First custom field.
    /// <para>第一个自定义字段。</para></summary>
    /// <value>In table version <c>1</c>, the low nibble (<see cref="DtboFormat.CompressionFlagMask"/>) holds the
    /// compression code; other bits and version <c>0</c> leave the field opaque.
    /// <para>在表版本 <c>1</c> 中，低半字节（<see cref="DtboFormat.CompressionFlagMask"/>）保存压缩编码；
    /// 其余位以及版本 <c>0</c> 下该字段不解释。</para></value>
    public uint Custom0 { get; set; }

    /// <summary>Second custom field, opaque to this library.
    /// <para>第二个自定义字段，本库不解释其含义。</para></summary>
    /// <value>Round-tripped byte for byte through read and serialize.
    /// <para>在读取与序列化之间逐字节原样往返。</para></value>
    public uint Custom1 { get; set; }

    /// <summary>Third custom field, opaque to this library.
    /// <para>第三个自定义字段，本库不解释其含义。</para></summary>
    /// <value>Round-tripped byte for byte through read and serialize.
    /// <para>在读取与序列化之间逐字节原样往返。</para></value>
    public uint Custom2 { get; set; }

    /// <summary>Fourth custom field, opaque to this library.
    /// <para>第四个自定义字段，本库不解释其含义。</para></summary>
    /// <value>Round-tripped byte for byte through read and serialize.
    /// <para>在读取与序列化之间逐字节原样往返。</para></value>
    public uint Custom3 { get; set; }

    /// <summary>
    /// Parses an entry (big-endian, 32 bytes).
    /// <para>解析条目（大端 32 字节）。</para>
    /// </summary>
    /// <param name="data">Span holding at least the 32-byte entry. <para>至少包含 32 字节条目的跨度。</para></param>
    /// <returns>The parsed entry. <para>解析后的条目。</para></returns>
    /// <exception cref="InvalidDataException">The span is shorter than the fixed entry size.
    /// <para>跨度短于固定条目大小。</para></exception>
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
    /// <returns>Exactly <c>32</c> bytes with all fields written in layout order.
    /// <para>恰好 <c>32</c> 字节，按布局顺序写入全部字段。</para></returns>
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
    /// <summary>Compression code for a stored, uncompressed blob. <para>存储、未压缩数据块的压缩编码值。</para></summary>
    public const uint CompressionNone = 0;

    /// <summary>Compression code for a zlib-wrapped blob. <para>zlib 封装数据块的压缩编码值。</para></summary>
    public const uint CompressionZlib = 1;

    /// <summary>Compression code for a gzip-wrapped blob. <para>gzip 封装数据块的压缩编码值。</para></summary>
    public const uint CompressionGzip = 2;

    /// <summary>Bit mask applied to <see cref="DtboEntry.Custom0"/> to extract the compression code in table version 1.
    /// <para>在表版本 1 中应用于 <see cref="DtboEntry.Custom0"/> 以提取压缩编码的位掩码。</para></summary>
    public const uint CompressionFlagMask = 0xF;

    /// <summary>Default combined output budget, in bytes, for all decoded blobs of one image.
    /// <para>单镜像全部解码数据块合计输出预算的默认值（字节）。</para></summary>
    public const long DefaultMaximumDecompressedBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Parses a bounded in-memory image; the output budget applies to all decoded blobs combined.
    /// <para>解析有界内存镜像，输出预算限制全部解码数据块的总大小。</para>
    /// </summary>
    /// <param name="data">Complete DTBO image bytes. <para>完整的 DTBO 镜像字节。</para></param>
    /// <param name="maximumDecompressedBytes">Combined decoded-size limit across all blobs.
    /// <para>全部数据块合计的解码大小上限。</para></param>
    /// <returns>Header, validated entries and decoded DTB blobs in entry order.
    /// <para>表头、已校验条目以及按条目顺序解码的 DTB 数据块。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. <para><paramref name="data"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The table is malformed or a decoded blob exceeds the budget.
    /// <para>表结构无效，或某个解码数据块超出预算。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumDecompressedBytes"/> is negative.
    /// <para><paramref name="maximumDecompressedBytes"/> 为负数。</para></exception>
    /// <example>
    /// <code>
    /// var (header, entries, dtbs) = DtboFormat.Read(File.ReadAllBytes("dtbo.img"));
    /// </code>
    /// </example>
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
    /// <param name="dtbs">DTB blobs to store, in entry order. <para>按条目顺序存储的 DTB 数据块。</para></param>
    /// <param name="pageSize">Page size recorded in the header; blobs are not padded to it.
    /// <para>记录在表头中的页大小；数据块不会按此值填充。</para></param>
    /// <param name="version">Table version, <c>0</c> or <c>1</c>; fresh entries carry no compression.
    /// <para>表版本，取 <c>0</c> 或 <c>1</c>；新生成的条目不携带压缩。</para></param>
    /// <returns>The complete DTBO image bytes. <para>完整的 DTBO 镜像字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dtbs"/> is null. <para><paramref name="dtbs"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">More than 65536 DTBs were supplied. <para>提供的 DTB 超过 65536 个。</para></exception>
    /// <exception cref="InvalidDataException">The supplied blobs total more than 256 MiB.
    /// <para>提供的数据块合计超过 256 MiB。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is greater than <c>1</c>.
    /// <para><paramref name="version"/> 大于 <c>1</c>。</para></exception>
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
    /// <param name="dtbs">DTB blobs to store, in entry order. <para>按条目顺序存储的 DTB 数据块。</para></param>
    /// <param name="entries">Per-blob metadata; <see cref="DtboEntry.Custom0"/> selects compression when
    /// <paramref name="version"/> is <c>1</c>. <para>逐数据块元数据；当 <paramref name="version"/> 为 <c>1</c> 时，
    /// 由 <see cref="DtboEntry.Custom0"/> 选择压缩方式。</para></param>
    /// <param name="pageSize">Page size recorded in the header; blobs are not padded to it.
    /// <para>记录在表头中的页大小；数据块不会按此值填充。</para></param>
    /// <param name="version">Table version, <c>0</c> or <c>1</c>. <para>表版本，取 <c>0</c> 或 <c>1</c>。</para></param>
    /// <returns>The complete DTBO image bytes with recalculated entry offsets and sizes.
    /// <para>重新计算条目偏移和大小后的完整 DTBO 镜像字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dtbs"/> or <paramref name="entries"/> is null.
    /// <para><paramref name="dtbs"/> 或 <paramref name="entries"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">Counts mismatch, an element is null, or the count exceeds 65536.
    /// <para>数量不匹配、存在 null 元素，或数量超过 65536。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is greater than <c>1</c>.
    /// <para><paramref name="version"/> 大于 <c>1</c>。</para></exception>
    /// <exception cref="InvalidDataException">The blobs total more than 256 MiB, or a compression code is unknown.
    /// <para>数据块合计超过 256 MiB，或压缩编码未知。</para></exception>
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
    /// <param name="dtb">DTB blob to store uncompressed. <para>要存储的 DTB 数据块，不压缩。</para></param>
    /// <param name="pageSize">Page size recorded in the header. <para>记录在表头中的页大小。</para></param>
    /// <param name="version">Table version, <c>0</c> or <c>1</c>. <para>表版本，取 <c>0</c> 或 <c>1</c>。</para></param>
    /// <returns>The complete single-entry DTBO image bytes. <para>完整的单条目 DTBO 镜像字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dtb"/> is null. <para><paramref name="dtb"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is greater than <c>1</c>.
    /// <para><paramref name="version"/> 大于 <c>1</c>。</para></exception>
    public static byte[] Pack(byte[] dtb, uint pageSize = FdtConstants.DtboDefaultPageSize, uint version = 0)
    {
        ArgumentNullException.ThrowIfNull(dtb);
        return Pack([dtb], pageSize, version);
    }

}