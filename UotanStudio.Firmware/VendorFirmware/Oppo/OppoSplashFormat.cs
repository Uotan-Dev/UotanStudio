using System.Buffers.Binary;
using System.Text;
using UotanStudio.Compress;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// One image record inside an OPPO/OnePlus splash partition: display name, data offset relative to
/// the payload area, raw (decompressed) size and stored (gzip) size.
/// <para>OPPO/OnePlus splash 分区中的一个镜像记录：显示名、相对负载区的数据偏移、原始（解压）大小与存储（gzip）大小。</para>
/// </summary>
/// <param name="Name">Stored entry name, control characters removed. <para>存储的条目名，已去除控制字符。</para></param>
/// <param name="Offset">Payload offset relative to the fixed data area start.
/// <para>相对固定数据区起点的数据偏移。</para></param>
/// <param name="RealSize">Declared decompressed size in bytes. <para>声明的解压字节大小。</para></param>
/// <param name="CompressedSize">Declared stored (gzip) size in bytes. <para>声明的存储（gzip）字节大小。</para></param>
public sealed record OppoSplashEntry(string Name, long Offset, long RealSize, long CompressedSize);

/// <summary>Validated metadata of an OPPO/OnePlus splash image.
/// <para>已校验的 OPPO/OnePlus splash 镜像元数据。</para></summary>
/// <param name="HasDdphHeader">Whether the file starts with the optional <c>DDPH</c> descriptor header.
/// <para>文件是否以可选的 <c>DDPH</c> 描述头开始。</para></param>
/// <param name="DdphFlag">Raw flag word following the DDPH magic; zero when absent.
/// <para><c>DDPH</c> 魔数之后的原始标志字；不存在时为 0。</para></param>
/// <param name="Metadata">The four 0x40-byte ASCII metadata strings following the splash magic.
/// <para>splash 魔数之后的四个 0x40 字节 ASCII 元数据字符串。</para></param>
/// <param name="Version">Declared format version word.<para>声明的格式版本字。</para></param>
/// <param name="Width">Declared panel width.<para>声明的面板宽度。</para></param>
/// <param name="Height">Declared panel height.<para>声明的面板高度。</para></param>
/// <param name="Special">Declared special flag word, uninterpreted.
/// <para>声明的特殊标志字，未作解释。</para></param>
/// <param name="Entries">Validated image records in table order.
/// <para>按表顺序排列的已校验镜像记录。</para></param>
public sealed record OppoSplashImage(bool HasDdphHeader, uint DdphFlag, IReadOnlyList<string> Metadata,
    uint Version, uint Width, uint Height, uint Special, IReadOnlyList<OppoSplashEntry> Entries);

/// <summary>
/// Bounded reader for OPPO/OnePlus splash images, following the layout proven by the reference
/// editor: an optional 8-byte <c>DDPH</c> descriptor header at offset 0; the 12-byte ASCII magic
/// <c>SPLASH LOGO!</c> at 0x4000; four 0x40-byte metadata strings at 0x400C; the count/version/
/// width/height/special words at 0x410C; 0x80-byte image records (u32 payload offset relative to
/// 0x8000, u32 raw size, u32 gzip size, 0x74-byte name) starting at 0x4120; and gzip payload data
/// based at 0x8000.
/// <para>OPPO/OnePlus splash 镜像的有界读取器，布局依据参考编辑器证实：偏移 0 处可有 8 字节 <c>DDPH</c>
/// 描述头；0x4000 处为 12 字节 ASCII 魔数 <c>SPLASH LOGO!</c>；0x400C 处为四个 0x40 字节元数据字符串；
/// 0x410C 处为数量/版本/宽/高/特殊字；0x4120 起为 0x80 字节的镜像记录（u32 相对 0x8000 的偏移、u32 原始大小、
/// u32 gzip 大小、0x74 字节名称）；gzip 负载数据基于 0x8000。</para>
/// </summary>
internal static class OppoSplashFormat
{
    /// <summary>Fixed start of the gzip payload data area. <para>gzip 负载数据区的固定起点。</para></summary>
    public const long DataOffset = 0x8000;

    /// <summary>Offset of the 12-byte <c>SPLASH LOGO!</c> magic. <para>12 字节 <c>SPLASH LOGO!</c> 魔数的偏移。</para></summary>
    public const long SplashHeaderOffset = 0x4000;

    private const int MetadataOffset = (int)SplashHeaderOffset + 12;
    private const int MetadataStringSize = 0x40;
    private const int CountersOffset = MetadataOffset + 4 * MetadataStringSize;
    private const int RecordsOffset = (int)SplashHeaderOffset + 0x120;
    private const int RecordSize = 0x80;
    private const int MaximumEntries = (int)((DataOffset - RecordsOffset) / RecordSize);

    /// <summary>
    /// Reports whether the bytes at <see cref="SplashHeaderOffset"/> hold the splash magic.
    /// <para>判断 <see cref="SplashHeaderOffset"/> 处的字节是否为 splash 魔数。</para>
    /// </summary>
    /// <param name="header">Bytes read starting at the splash header offset; may be short.
    /// <para>自 splash 头偏移起读取的字节；可以不足 12 字节。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasSplashMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..12].SequenceEqual("SPLASH LOGO!"u8);

    /// <summary>Reads and validates the splash header, metadata strings and image record table.
    /// <para>读取并校验 splash 头、元数据字符串与镜像记录表。</para></summary>
    /// <param name="input">Readable, seekable splash stream; position is restored on return.
    /// <para>可读、可寻址的 splash 流；返回时恢复位置。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消本次读取。</para></param>
    /// <returns>The validated image metadata.<para>已校验的镜像元数据。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The splash magic is missing, the declared entry count exceeds the metadata area, or a payload range escapes the file.
    /// <para>splash 魔数缺失、声明条目数超出元数据区，或负载区间超出文件。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static OppoSplashImage Read(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Splash reading requires a readable, seekable stream.", nameof(input));
        }

        var original = input.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Length < DataOffset)
            {
                throw new InvalidDataException("The splash image is smaller than its fixed header area.");
            }

            Span<byte> prefix = stackalloc byte[8];
            input.Position = 0;
            input.ReadExactly(prefix);
            var hasDdph = prefix[..4].SequenceEqual("DDPH"u8);
            var ddphFlag = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);

            var header = new byte[RecordsOffset - (int)SplashHeaderOffset];
            input.Position = SplashHeaderOffset;
            input.ReadExactly(header);
            if (!HasSplashMagic(header))
            {
                throw new InvalidDataException("The SPLASH LOGO! magic is absent.");
            }

            var metadata = new List<string>(4);
            for (var index = 0; index < 4; index++)
            {
                metadata.Add(DecodeAscii(header, 12 + index * MetadataStringSize, MetadataStringSize));
            }

            var counters = header.AsSpan(CountersOffset - (int)SplashHeaderOffset);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(counters);
            if (count > MaximumEntries)
            {
                throw new InvalidDataException($"The splash declares {count} images, above the metadata-area ceiling {MaximumEntries}.");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(counters[4..]);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(counters[8..]);
            var height = BinaryPrimitives.ReadUInt32LittleEndian(counters[12..]);
            var special = BinaryPrimitives.ReadUInt32LittleEndian(counters[16..]);

            var records = new byte[(int)count * RecordSize];
            input.Position = RecordsOffset;
            input.ReadExactly(records);
            var entries = new List<OppoSplashEntry>((int)count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = records.AsSpan(index * RecordSize, RecordSize);
                var offset = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(record));
                var realSize = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
                var compressedSize = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(record[8..]));
                if (compressedSize > 0 && (offset > input.Length - DataOffset
                        || compressedSize > input.Length - DataOffset - offset))
                {
                    throw new InvalidDataException($"Splash entry {index} points outside the payload area.");
                }

                entries.Add(new OppoSplashEntry(
                    DecodeAscii(record, 12, RecordSize - 12),
                    offset,
                    realSize,
                    compressedSize));
            }

            return new OppoSplashImage(hasDdph, ddphFlag, metadata, version, width, height, special, entries);
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Copies the stored gzip bytes of one entry into the destination stream without decompressing.
    /// <para>将单个条目的存储 gzip 字节原样复制到目标流，不做解压。</para>
    /// </summary>
    /// <param name="input">Source splash stream. <para>源 splash 流。</para></param>
    /// <param name="entry">Entry produced by <see cref="Read"/>. <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream. <para>目标流。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    public static void CopyStoredPayload(Stream input, OppoSplashEntry entry, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        using var range = new BoundedReadStream(input, checked(DataOffset + entry.Offset),
            entry.CompressedSize, leaveOpen: true);
        StreamCopy.CopyExactly(range, output, entry.CompressedSize, cancellationToken);
    }

    /// <summary>Decompresses one entry's gzip payload into the destination stream and verifies the produced byte count against the declared raw size when that size is nonzero.
    /// <para>解压单个条目的 gzip 负载到目标流，并在声明的原始大小非零时与其核对产出的字节数。</para></summary>
    /// <param name="input">Source splash stream.<para>源 splash 流。</para></param>
    /// <param name="entry">Entry produced by <see cref="Read"/>.
    /// <para>由 <see cref="Read"/> 产生的条目。</para></param>
    /// <param name="output">Destination stream receiving the raw image bytes.
    /// <para>接收原始镜像字节的目标流。</para></param>
    /// <param name="maximumOutputBytes">Decompression ceiling for this entry.
    /// <para>本条目的解压上限。</para></param>
    /// <param name="cancellationToken">Cancels the decompression.<para>取消本次解压。</para></param>
    /// <exception cref="ArgumentNullException">Any argument is null.
    /// <para>任一参数为 null。</para></exception>
    /// <exception cref="InvalidDataException">The gzip stream is malformed, exceeds the ceiling, or disagrees with the declared raw size.
    /// <para>gzip 流格式错误、超出上限，或与声明的原始大小不一致。压缩门面报告的失败也会以此类型呈现， 原始原因保留在 <see cref="Exception.InnerException"/> 中。</para></exception>
    public static void DecompressEntry(Stream input, OppoSplashEntry entry, Stream output,
        long maximumOutputBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.CompressedSize == 0)
        {
            return;
        }

        using var range = new BoundedReadStream(input, checked(DataOffset + entry.Offset),
            entry.CompressedSize, leaveOpen: true);
        var buffer = new byte[81920];
        long total = 0;
        try
        {
            using var gzip = CompressionService.CreateDecompressionStream(range, CompressionFormat.Gzip);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maximumOutputBytes || (entry.RealSize > 0 && total > entry.RealSize))
                {
                    throw new InvalidDataException("The splash payload exceeds its declared size or the configured budget.");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (CompressionException exception)
        {
            throw new InvalidDataException(
                $"Splash entry {entry.Name} failed to decompress.", exception);
        }

        if (entry.RealSize > 0 && total != entry.RealSize)
        {
            throw new InvalidDataException($"Splash entry {entry.Name} decompressed to {total} bytes, declared {entry.RealSize}.");
        }
    }

    /// <summary>
    /// Builds a collision-safe local file name for a splash entry, keeping only the base name.
    /// <para>为 splash 条目构建防碰撞的本地文件名，仅保留基础名。</para></summary>
    /// <param name="entry">The splash entry. <para>splash 条目。</para></param>
    /// <param name="index">Zero-based table position used for empty names. <para>名称为空时使用的零基表序号。</para></param>
    /// <returns>A sanitized file name, suffixed with <c>.bin</c> when the stored name has no extension.
    /// <para>已净化的文件名；存储名无扩展名时追加 <c>.bin</c>。</para></returns>
    public static string SafeEntryFileName(OppoSplashEntry entry, int index)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry.Name.Replace('\\', '/');
        var baseName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var characters = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. baseName.Where(c => !characters.Contains(c) && c > 0x1F)]).TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            cleaned = $"image_{index}.bin";
        }
        else if (!cleaned.Contains('.', StringComparison.Ordinal))
        {
            cleaned += ".bin";
        }

        return cleaned;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var builder = new StringBuilder(length);
        foreach (var value in slice)
        {
            if (value != 0)
            {
                builder.Append((char)value);
            }
        }

        return builder.ToString();
    }
}