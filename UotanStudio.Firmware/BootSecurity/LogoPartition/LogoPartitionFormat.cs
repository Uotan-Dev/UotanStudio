using System.Buffers.Binary;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.LogoPartition;

/// <summary>
/// Container family of boot logo and splash partitions.
/// <para>开机 Logo 与 splash 分区的容器家族。</para>
/// </summary>
public enum LogoPartitionKind
{
    /// <summary>No known logo container signature. <para>未发现已知的 Logo 容器魔数。</para></summary>
    Unknown,

    /// <summary>LOGO!!!! block-table container holding complete BMP files. <para>LOGO!!!! 块表容器，内含完整 BMP 文件。</para></summary>
    LogoBlockTable,

    /// <summary>SPLASH!! frame sequence holding raw or RLE24 BGR pixels. <para>SPLASH!! 帧序列，内含裸 BGR 或 RLE24 像素。</para></summary>
    QualcommSplash,

    /// <summary>GuoKe LOGO_TABLE header followed by one JPEG image. <para>果壳 LOGO_TABLE 头，后随单张 JPEG 图片。</para></summary>
    GuoKeLogo
}

/// <summary>
/// Pixel encoding of one logo partition entry.
/// <para>Logo 分区中单个条目的像素编码。</para>
/// </summary>
public enum LogoEntryEncoding
{
    /// <summary>A complete BMP file with its own file header. <para>带文件头的完整 BMP 文件。</para></summary>
    BmpFile,

    /// <summary>RLE24-compressed 24-bit BGR pixels without any file header. <para>无文件头的 RLE24 压缩 24 位 BGR 像素。</para></summary>
    Rle24Bgr,

    /// <summary>Uncompressed 24-bit BGR pixels without any file header. <para>无文件头的未压缩 24 位 BGR 像素。</para></summary>
    RawBgr,

    /// <summary>A complete JPEG file. <para>完整的 JPEG 文件。</para></summary>
    Jpeg
}

/// <summary>
/// One bounded image entry inside a logo partition.
/// <para>Logo 分区中一个经过边界校验的图片条目。</para>
/// </summary>
/// <param name="Index">Zero-based table position. <para>从零开始的表格序号。</para></param>
/// <param name="Name">Display name of the entry. <para>条目的显示名称。</para></param>
/// <param name="FileName">Collision-safe output file name. <para>防碰撞的输出文件名。</para></param>
/// <param name="Encoding">Stored pixel encoding. <para>存储的像素编码。</para></param>
/// <param name="DataOffset">Absolute offset of the stored payload in the container. <para>负载在容器中的绝对偏移。</para></param>
/// <param name="DataSize">Stored payload length in bytes. <para>负载的存储字节长度。</para></param>
/// <param name="RawSize">Declared decoded length in bytes, or zero when unknown. <para>声明的解码后字节数；未知时为 0。</para></param>
/// <param name="Width">Stored pixel width. <para>存储的像素宽度。</para></param>
/// <param name="Height">Stored pixel height. <para>存储的像素高度。</para></param>
/// <param name="BitsPerPixel">Bits per pixel of the decoded image. <para>解码后图片的每像素位数。</para></param>
public sealed record LogoPartitionEntry(
    int Index,
    string Name,
    string FileName,
    LogoEntryEncoding Encoding,
    long DataOffset,
    long DataSize,
    long RawSize,
    int Width,
    int Height,
    int BitsPerPixel);

/// <summary>
/// Validated header, entry table and capability metadata of one logo partition.
/// <para>单个 Logo 分区经过校验的头部、条目表与能力元数据。</para>
/// </summary>
/// <param name="Kind">Detected container family. <para>识别出的容器家族。</para></param>
/// <param name="FormatName">Stable human-readable format name. <para>稳定可读的格式名。</para></param>
/// <param name="HeaderOffset">Offset where the container magic or frame table starts. <para>容器魔数或帧表的起始偏移。</para></param>
/// <param name="SourceLength">Container length in bytes. <para>容器的字节长度。</para></param>
/// <param name="Entries">Validated entries in table order. <para>按表格顺序排列的已校验条目。</para></param>
/// <param name="Metadata">Header facts reported to the properties panel. <para>上报给属性面板的头部信息。</para></param>
public sealed record LogoPartitionImage(
    LogoPartitionKind Kind,
    string FormatName,
    int HeaderOffset,
    long SourceLength,
    IReadOnlyList<LogoPartitionEntry> Entries,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Bounded reader for boot logo and splash partitions: a LOGO!!!! block table of complete BMP files,
/// a SPLASH!! sequence of raw or RLE24 BGR frames and a GuoKe LOGO_TABLE header followed by a JPEG.
/// OPPO SPLASH LOGO! images keep their dedicated handler. Every offset, count and decoded size is
/// validated before any payload is materialized.
/// <para>开机 Logo 与 splash 分区的有界读取器：LOGO!!!! 完整 BMP 块表、SPLASH!! 裸 BGR/RLE24 帧序列，
/// 以及果壳 LOGO_TABLE 头加单张 JPEG；OPPO SPLASH LOGO! 镜像仍由专用处理器负责。任何负载在实体化之前
/// 都先校验偏移、数量与解码后大小。</para>
/// </summary>
public static class LogoPartitionFormat
{
    /// <summary>Maximum container length accepted by the reader. <para>读取器接受的容器最大长度。</para></summary>
    public const long MaximumFileBytes = 256L * 1024 * 1024;

    /// <summary>Maximum number of entries or frames accepted. <para>接受的条目或帧数上限。</para></summary>
    public const int MaximumEntries = 1024;

    /// <summary>Maximum decoded pixel count per entry. <para>单条目解码像素数上限。</para></summary>
    public const long MaximumPixels = 32L * 1024 * 1024;

    /// <summary>Maximum decoded byte length per entry. <para>单条目解码后字节长度上限。</para></summary>
    public const long MaximumEntryBytes = 64L * 1024 * 1024;

    private const int BlockSize = 4096;
    private const int SplashHeaderSize = 512;
    private const int SplashBlockSize = 512;
    private const int BmpHeaderSize = 54;
    private const int GuoKeJpegOffset = 0x2000;
    private const int GuoKeHeaderSize = 128;
    private const int LogoTableOffset = 0x4000;
    private const int QualcommPrefixOffset = 1024;
    private const int MioFormatProbeOffset = 4000;

    private static readonly int[] LogoMagicOffsets = [0, LogoTableOffset, MioFormatProbeOffset];
    private static readonly int[] SplashMagicOffsets = [QualcommPrefixOffset, 0];

    /// <summary>
    /// Detects the logo container family from a leading byte prefix, scanning every known magic offset.
    /// <para>从文件头前缀扫描所有已知魔数偏移，识别 Logo 容器家族。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the candidate file; at least 64 KiB is needed for every offset.
    /// <para>候选文件的头部字节；要覆盖全部偏移至少需要 64 KiB。</para></param>
    /// <returns>The detected family, or <see cref="LogoPartitionKind.Unknown"/>.
    /// <para>识别出的容器家族；未命中时为 <see cref="LogoPartitionKind.Unknown"/>。</para></returns>
    public static LogoPartitionKind Detect(ReadOnlySpan<byte> header)
    {
        foreach (var offset in LogoMagicOffsets)
        {
            if (MatchesAt(header, offset, "LOGO!!!!"u8))
            {
                return LogoPartitionKind.LogoBlockTable;
            }
        }

        foreach (var offset in SplashMagicOffsets)
        {
            if (MatchesAt(header, offset, "SPLASH!!"u8))
            {
                return LogoPartitionKind.QualcommSplash;
            }
        }

        return IsGuoKeHeader(header) ? LogoPartitionKind.GuoKeLogo : LogoPartitionKind.Unknown;
    }

    /// <summary>
    /// Reports whether the header belongs to a container this reader can unpack.
    /// <para>判断文件头是否属于本读取器可解包的容器。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the candidate file. <para>候选文件的头部字节。</para></param>
    /// <returns>True for a supported logo container. <para>受支持的 Logo 容器返回 true。</para></returns>
    public static bool IsLogoPartitionHeader(ReadOnlySpan<byte> header)
        => Detect(header) != LogoPartitionKind.Unknown;

    /// <summary>
    /// Reads and validates the container header and entry table without decoding payloads.
    /// <para>读取并校验容器头与条目表，不解码负载。</para>
    /// </summary>
    /// <param name="sourcePath">Path of the logo or splash partition. <para>Logo 或 splash 分区路径。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The validated image metadata. <para>经过校验的镜像元数据。</para></returns>
    /// <exception cref="ArgumentException">The path is blank. <para>路径为空。</para></exception>
    /// <exception cref="InvalidDataException">The container is unknown, truncated, or declares an out-of-range entry.
    /// <para>容器未识别、被截断，或声明了越界条目。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static LogoPartitionImage Read(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        var length = input.Length;
        if (length > MaximumFileBytes)
        {
            throw new InvalidDataException("Logo partition exceeds the supported file-size limit.");
        }

        var headerLength = (int)Math.Min(length, 64 * 1024);
        var header = new byte[headerLength];
        input.Position = 0;
        input.ReadExactly(header);
        var kind = Detect(header);
        if (kind == LogoPartitionKind.Unknown)
        {
            throw new InvalidDataException("No supported logo partition magic was found.");
        }

        return kind switch
        {
            LogoPartitionKind.LogoBlockTable => ReadLogoBlockTable(input, header, length, cancellationToken),
            LogoPartitionKind.QualcommSplash => ReadQualcommSplash(input, header, length, cancellationToken),
            LogoPartitionKind.GuoKeLogo => ReadGuoKeLogo(input, length),
            _ => throw new InvalidDataException("Unsupported logo partition family.")
        };
    }

    /// <summary>
    /// Decodes one entry into a standard image file (BMP or JPEG) ready for preview or extraction.
    /// <para>把单个条目解码为标准图片文件（BMP 或 JPEG），可直接预览或提取。</para>
    /// </summary>
    /// <param name="input">Readable, seekable container stream; its position is restored on return.
    /// <para>可读、可寻址的容器流；返回时恢复位置。</para></param>
    /// <param name="image">Metadata produced by <see cref="Read(string, CancellationToken)"/>.
    /// <para>由 <see cref="Read(string, CancellationToken)"/> 生成的元数据。</para></param>
    /// <param name="entry">Entry to decode. <para>待解码的条目。</para></param>
    /// <param name="cancellationToken">Cancels the decode. <para>取消解码。</para></param>
    /// <returns>Complete BMP or JPEG bytes. <para>完整的 BMP 或 JPEG 字节。</para></returns>
    /// <exception cref="InvalidDataException">The payload is malformed or exceeds a declared or configured bound.
    /// <para>负载格式错误，或超出声明值/配置上限。</para></exception>
    public static byte[] ReadEntryImage(Stream input, LogoPartitionImage image, LogoPartitionEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        FileExtraction.ValidateRange(entry.DataOffset, entry.DataSize, image.SourceLength, "Logo entry payload");

        var original = input.Position;
        try
        {
            return entry.Encoding switch
            {
                LogoEntryEncoding.BmpFile or LogoEntryEncoding.Jpeg => ReadStoredImage(input, entry, cancellationToken),
                LogoEntryEncoding.Rle24Bgr or LogoEntryEncoding.RawBgr => DecodeBgrFrame(input, entry, cancellationToken),
                _ => throw new InvalidDataException("Unsupported logo entry encoding.")
            };
        }
        finally
        {
            input.Position = original;
        }
    }

    /// <summary>
    /// Extracts the selected entries (or all of them) to a directory, publishing each file atomically.
    /// <para>把选中的条目（或全部条目）提取到目录，每个文件以原子方式发布。</para>
    /// </summary>
    /// <param name="sourcePath">Container path. <para>容器路径。</para></param>
    /// <param name="outputDirectory">Destination directory. <para>目标目录。</para></param>
    /// <param name="selectedEntries">Case-insensitive entry-name filter; null extracts all entries.
    /// <para>不区分大小写的条目名过滤器；为 null 时提取全部条目。</para></param>
    /// <param name="overwrite">Whether existing destination files may be replaced. <para>是否允许替换已存在的目标文件。</para></param>
    /// <param name="cancellationToken">Cancels the extraction. <para>取消提取。</para></param>
    /// <returns>The written file names. <para>已写出的文件名列表。</para></returns>
    public static IReadOnlyList<string> Extract(string sourcePath, string outputDirectory,
        IReadOnlySet<string>? selectedEntries = null, bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var image = Read(sourcePath, cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        var written = new List<string>(image.Entries.Count);
        foreach (var entry in image.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selectedEntries is not null && !selectedEntries.Contains(entry.FileName))
            {
                continue;
            }

            var data = ReadEntryImage(input, image, entry, cancellationToken);
            var target = FileExtraction.ResolvePath(outputDirectory, entry.FileName);
            FileExtraction.WriteAtomic(target, output => output.Write(data), overwrite, cancellationToken);
            written.Add(entry.FileName);
        }

        return written;
    }

    private static LogoPartitionImage ReadLogoBlockTable(Stream input, byte[] header, long length,
        CancellationToken cancellationToken)
    {
        var tableOffset = FindMagicOffset(header, LogoMagicOffsets, "LOGO!!!!"u8);
        var entries = new List<LogoPartitionEntry>();
        var cursor = tableOffset + 8;
        var metadata = new Dictionary<string, string>
        {
            ["Container"] = "LOGO!!!! block table",
            ["BlockSize"] = BlockSize.ToString(),
            ["TableOffset"] = tableOffset.ToString()
        };

        while (cursor + 8 <= length && entries.Count < MaximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new byte[8];
            input.Position = cursor;
            input.ReadExactly(record);
            var blockIndex = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0, 4));
            var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4));
            if (blockIndex == 0)
            {
                break;
            }

            var dataOffset = checked((long)blockIndex * BlockSize);
            var blockBytes = checked((long)blockCount * BlockSize);
            FileExtraction.ValidateRange(dataOffset, blockBytes, length, "LOGO!!!! block");
            var bmp = ReadBmpDescriptor(input, dataOffset, blockBytes, entries.Count, cancellationToken);
            var index = entries.Count;
            entries.Add(new LogoPartitionEntry(index, $"logo_{index:00}",
                $"logo_{index:00}.bmp", LogoEntryEncoding.BmpFile, dataOffset, bmp.Length,
                bmp.Length, bmp.Width, bmp.Height, bmp.BitsPerPixel));
            cursor += 8;
        }

        metadata["EntryCount"] = entries.Count.ToString();
        return new LogoPartitionImage(LogoPartitionKind.LogoBlockTable, "LOGO!!!! Logo Partition",
            tableOffset, length, entries, metadata);
    }

    private static LogoPartitionImage ReadQualcommSplash(Stream input, byte[] header, long length,
        CancellationToken cancellationToken)
    {
        var prefix = FindMagicOffset(header, SplashMagicOffsets, "SPLASH!!"u8);
        var entries = new List<LogoPartitionEntry>();
        var cursor = (long)prefix;
        var metadata = new Dictionary<string, string>
        {
            ["Container"] = "SPLASH!! frame sequence",
            ["FrameHeaderSize"] = SplashHeaderSize.ToString(),
            ["PrefixOffset"] = prefix.ToString()
        };

        while (cursor + SplashHeaderSize <= length && entries.Count < MaximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameHeader = new byte[SplashHeaderSize];
            input.Position = cursor;
            input.ReadExactly(frameHeader);
            if (!frameHeader.AsSpan(0, 8).SequenceEqual("SPLASH!!"u8))
            {
                break;
            }

            var width = ReadUInt32(frameHeader, 8);
            var height = ReadUInt32(frameHeader, 12);
            var type = ReadUInt32(frameHeader, 16);
            var blocks = ReadUInt32(frameHeader, 20);
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
                (long)width * height > MaximumPixels || type > 1)
            {
                throw new InvalidDataException("SPLASH!! frame declares unsupported dimensions or encoding.");
            }

            var payloadOffset = cursor + SplashHeaderSize;
            var payloadSize = checked((long)blocks * SplashBlockSize);
            FileExtraction.ValidateRange(payloadOffset, payloadSize, length, "SPLASH!! payload");
            if (payloadSize > MaximumEntryBytes)
            {
                throw new InvalidDataException("SPLASH!! payload exceeds the decode budget.");
            }

            var index = entries.Count;
            var encoding = type == 1 ? LogoEntryEncoding.Rle24Bgr : LogoEntryEncoding.RawBgr;
            entries.Add(new LogoPartitionEntry(index, $"splash_{index + 1:00}",
                $"splash_{index + 1:00}.bmp", encoding, payloadOffset, payloadSize,
                checked((long)width * height * 3), (int)width, (int)height, 24));

            cursor = payloadOffset + payloadSize;
            cursor = SkipZeroPadding(input, cursor, length);
        }

        metadata["EntryCount"] = entries.Count.ToString();
        return new LogoPartitionImage(LogoPartitionKind.QualcommSplash, "Qualcomm SPLASH!! Partition",
            prefix, length, entries, metadata);
    }

    private static LogoPartitionImage ReadGuoKeLogo(Stream input, long length)
    {
        if (length <= GuoKeJpegOffset)
        {
            throw new InvalidDataException("GuoKe logo container has no JPEG payload.");
        }

        var jpegSize = length - GuoKeJpegOffset;
        if (jpegSize > MaximumEntryBytes)
        {
            throw new InvalidDataException("GuoKe JPEG payload exceeds the decode budget.");
        }

        var metadata = new Dictionary<string, string>
        {
            ["Container"] = "GuoKe LOGO_TABLE",
            ["HeaderSize"] = GuoKeHeaderSize.ToString(),
            ["JpegOffset"] = GuoKeJpegOffset.ToString(),
            ["EntryCount"] = "1"
        };
        var entry = new LogoPartitionEntry(0, "image", "image.jpg", LogoEntryEncoding.Jpeg,
            GuoKeJpegOffset, jpegSize, jpegSize, 0, 0, 24);
        return new LogoPartitionImage(LogoPartitionKind.GuoKeLogo, "GuoKe LOGO_TABLE Partition",
            0, length, [entry], metadata);
    }

    private static byte[] ReadStoredImage(Stream input, LogoPartitionEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.DataSize > MaximumEntryBytes || entry.DataSize > int.MaxValue)
        {
            throw new InvalidDataException("Logo entry exceeds the decode budget.");
        }

        var data = new byte[(int)entry.DataSize];
        input.Position = entry.DataOffset;
        input.ReadExactly(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Encoding == LogoEntryEncoding.BmpFile && !HasBmpMagic(data))
        {
            throw new InvalidDataException("Logo block does not start with a BMP file header.");
        }

        return data;
    }

    private static byte[] DecodeBgrFrame(Stream input, LogoPartitionEntry entry,
        CancellationToken cancellationToken)
    {
        var payload = ReadStoredImage(input, entry, cancellationToken);
        var pixels = checked((long)entry.Width * entry.Height);
        var expected = checked(pixels * 3);
        if (expected > MaximumEntryBytes || expected > int.MaxValue)
        {
            throw new InvalidDataException("SPLASH!! frame exceeds the decode budget.");
        }

        var bgr = new byte[(int)expected];
        if (entry.Encoding == LogoEntryEncoding.RawBgr)
        {
            if (payload.Length < bgr.Length)
            {
                throw new InvalidDataException("Raw SPLASH!! payload is shorter than its declared raster.");
            }

            payload.AsSpan(0, bgr.Length).CopyTo(bgr);
        }
        else
        {
            DecodeRle24(payload, bgr, cancellationToken);
        }

        return BuildBmp(bgr, entry.Width, entry.Height);
    }

    private static void DecodeRle24(ReadOnlySpan<byte> payload, Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var source = 0;
        var target = 0;
        while (source < payload.Length && target < destination.Length)
        {
            if ((target & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = payload[source++] + 1;
            if (count > 128)
            {
                if (source + 3 > payload.Length)
                {
                    throw new InvalidDataException("Truncated RLE24 repeat segment.");
                }

                var repeat = count - 128;
                for (var i = 0; i < repeat && target < destination.Length; i++)
                {
                    payload.Slice(source, 3).CopyTo(destination[target..]);
                    target += 3;
                }

                source += 3;
            }
            else
            {
                var literal = checked(count * 3);
                if (source + literal > payload.Length)
                {
                    throw new InvalidDataException("Truncated RLE24 literal segment.");
                }

                var copy = Math.Min(literal, destination.Length - target);
                payload.Slice(source, copy).CopyTo(destination[target..]);
                source += literal;
                target += copy;
            }
        }
    }

    private static byte[] BuildBmp(ReadOnlySpan<byte> bgr, int width, int height)
    {
        var rowSize = checked(((width * 3) + 3) / 4 * 4);
        var pixelBytes = checked(rowSize * height);
        var fileSize = checked(BmpHeaderSize + pixelBytes);
        if (fileSize > MaximumEntryBytes || fileSize > int.MaxValue)
        {
            throw new InvalidDataException("Decoded BMP exceeds the output budget.");
        }

        var result = new byte[fileSize];
        "BM"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), BmpHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34), (uint)pixelBytes);

        for (var y = 0; y < height; y++)
        {
            var sourceRow = checked(y * width * 3);
            var targetRow = checked(BmpHeaderSize + (height - 1 - y) * rowSize);
            bgr.Slice(sourceRow, width * 3).CopyTo(result.AsSpan(targetRow, width * 3));
        }

        return result;
    }

    private static (int Length, int Width, int Height, int BitsPerPixel) ReadBmpDescriptor(
        Stream input, long offset, long blockBytes, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[BmpHeaderSize];
        input.Position = offset;
        input.ReadExactly(header);
        if (!HasBmpMagic(header))
        {
            throw new InvalidDataException($"Logo block {index} does not start with a BMP file header.");
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10, 4));
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22, 4));
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        if (declared < BmpHeaderSize || pixelOffset < BmpHeaderSize || dibSize < 40 ||
            declared > blockBytes || pixelOffset > declared || declared > MaximumEntryBytes ||
            width <= 0 || height == 0 ||
            (long)Math.Abs((long)width) * Math.Abs((long)height) > MaximumPixels)
        {
            throw new InvalidDataException($"Logo block {index} has an invalid BMP header.");
        }

        return ((int)declared, width, Math.Abs(height), bpp);
    }

    private static long SkipZeroPadding(Stream input, long cursor, long length)
    {
        var buffer = new byte[4096];
        while (cursor < length)
        {
            var count = (int)Math.Min(buffer.Length, length - cursor);
            input.Position = cursor;
            input.ReadExactly(buffer.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != 0)
                {
                    return cursor + i;
                }
            }

            cursor += count;
        }

        return cursor;
    }

    private static bool HasBmpMagic(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M';

    private static bool IsGuoKeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < GuoKeHeaderSize || !header[..3].SequenceEqual("###"u8))
        {
            return false;
        }

        return header[..GuoKeHeaderSize].IndexOf("LOGO_TABLE"u8) >= 0;
    }

    private static bool MatchesAt(ReadOnlySpan<byte> header, int offset, ReadOnlySpan<byte> magic) =>
        offset >= 0 && offset <= header.Length - magic.Length
        && header.Slice(offset, magic.Length).SequenceEqual(magic);

    private static int FindMagicOffset(ReadOnlySpan<byte> header, IReadOnlyList<int> offsets,
        ReadOnlySpan<byte> magic)
    {
        foreach (var offset in offsets)
        {
            if (MatchesAt(header, offset, magic))
            {
                return offset;
            }
        }

        throw new InvalidDataException("The detected logo magic is no longer present in the header.");
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}