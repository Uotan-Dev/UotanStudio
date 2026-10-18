using System.Text;
using UotanStudio.Firmware.VendorFirmware.Nb0.Models;

namespace UotanStudio.Firmware.VendorFirmware.Nb0;

/// <summary>
/// Parses NB0 firmware packages: a little-endian entry count, a fixed-size entry header table and a data section. Entry types are inferred from magic signatures, an optional trailing .md5 entry becomes verification records, and structural problems are reported rather than thrown.
/// <para>解析 NB0 固件包：小端条目计数、定长条目头表与数据区。条目类型按魔数签名推断，可选的尾部 .md5 条目解码为校验记录，结构问题以结果回报而非抛出异常。</para>
/// </summary>
public static class Nb0Parser
{
    private static readonly (byte[] Magic, string TypeName)[] KnownMagicSignatures =
    [
        ([0x1F, 0x8B], "gzip"),
        ([0x50, 0x4B, 0x03, 0x04], "zip"),
        ([0x41, 0x4E, 0x44, 0x52, 0x4F, 0x49, 0x44], "boot"),
        ([0xED, 0x26, 0xFF, 0x3A], "sparse"),
        ([0xD0, 0x0D, 0xFE, 0xED], "dtb"),
        ([0x53, 0xEF], "ext4"),
        ([0x7F, 0x45, 0x4C, 0x46], "ELF"),
        ([0x41, 0x56, 0x42, 0x30], "AVB"),
        ([0x73, 0x71, 0x73, 0x68], "squashfs"),
    ];

    /// <summary>Parses the NB0 firmware file at the specified path and returns metadata information.
    /// <para>解析指定路径的 NB0 固件文件,返回元数据信息。</para></summary>
    /// <param name="filePath">Path to an existing NB0 file; it is opened read-only with <see cref="FileShare.Read"/> sharing, sequential-access hints and a 64 KiB buffer, and is closed before this call returns.
    /// <para>已存在的 NB0 文件路径；文件以只读、<see cref="FileShare.Read"/> 共享、顺序访问提示 和 64 KiB 缓冲打开，并在本调用返回前关闭。</para></param>
    /// <returns>Parsed metadata with entries, inferred types, firmware inference and warnings.
    /// <para>解析得到的元数据，包含条目、推断类型、固件推断结果与警告。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty, or the file is truncated below the 4-byte entry-count header.
    /// <para><paramref name="filePath"/> 为空，或文件在 4 字节条目数头部之前被截断。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.<para>文件不存在。</para></exception>
    /// <exception cref="IOException">The file could not be opened or read.
    /// <para>文件无法打开或读取。</para></exception>
    /// <exception cref="EndOfStreamException">The file ends before the entry count or a declared entry header is fully read.
    /// <para>文件在条目数或某个声明的条目头读完之前结束。</para></exception>
    /// <exception cref="Nb0CorruptedException">The declared entry count exceeds <see cref="int.MaxValue"/>, or the file is too small to hold the declared entry table.
    /// <para>声明的条目数超过 <see cref="int.MaxValue"/>，或文件小到不足以容纳声明的条目表。</para></exception>
    /// <example>
    /// <code>
    /// Nb0Metadata metadata = Nb0Parser.Parse(@"D:\fw\update.nb0");
    /// string? inferredType = metadata.Entries.Count &gt; 0 ? metadata.Entries[0].InferredType : null;
    /// </code>
    /// </example>
    public static Nb0Metadata Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return ParseFromStream(stream);
    }

    /// <summary>Asynchronously parses the NB0 firmware file at the specified path and returns metadata information.
    /// <para>异步解析指定路径的 NB0 固件文件,返回元数据信息。</para></summary>
    /// <param name="filePath">Path to an existing NB0 file; it is opened read-only with <see cref="FileShare.Read"/> sharing, asynchronous sequential-access hints and a 64 KiB buffer, and is closed when the returned task completes.
    /// <para>已存在的 NB0 文件路径；文件以只读、<see cref="FileShare.Read"/> 共享、异步顺序访问 提示和 64 KiB 缓冲打开，并在返回的任务完成时关闭。</para></param>
    /// <param name="cancellationToken">Cancels file reads between entry headers; checked once per entry.
    /// <para>取消条目之间的文件读取；每处理一个条目检查一次。</para></param>
    /// <returns>A task producing parsed metadata with entries, inferred types and warnings.
    /// <para>产生已解析元数据（含条目、推断类型与警告）的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty.
    /// <para><paramref name="filePath"/> 为空。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.<para>文件不存在。</para></exception>
    /// <exception cref="IOException">The file could not be opened or read.
    /// <para>文件无法打开或读取。</para></exception>
    /// <exception cref="EndOfStreamException">The file ends before the entry count or a declared entry header is fully read.
    /// <para>文件在条目数或某个声明的条目头读完之前结束。</para></exception>
    /// <exception cref="Nb0CorruptedException">The declared entry count exceeds <see cref="int.MaxValue"/>, or the file is too small to hold the declared entry table.
    /// <para>声明的条目数超过 <see cref="int.MaxValue"/>，或文件小到不足以容纳声明的条目表。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested while reading.
    /// <para>读取过程中请求了取消。</para></exception>
    public static async Task<Nb0Metadata> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ParseFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses an NB0 firmware file from a stream and returns metadata information.
    /// <para>从流中解析 NB0 固件文件,返回元数据信息。</para></summary>
    /// <param name="stream">Readable stream positioned at the start of the NB0 metadata (the little-endian 4-byte entry count). It is left open after the call and must not be disposed by the reader until parsing completes; seekable streams enable the entry-table size check, magic detection and exact <see cref="Nb0Metadata.TotalSize"/>, while non-seekable streams skip those steps and estimate the total size.
    /// <para>定位在 NB0 元数据开头（小端 4 字节条目数）的可读流。调用结束后流保持打开， 解析完成前不得释放；可寻址流可进行条目表大小核对、魔数检测并得到精确的 <see cref="Nb0Metadata.TotalSize"/>，不可寻址流则跳过这些步骤并估算总大小。</para></param>
    /// <returns>Parsed metadata with entries, inferred types, firmware inference and warnings.
    /// <para>解析得到的元数据，包含条目、推断类型、固件推断结果与警告。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The stream yields fewer than 4 bytes for the entry count.
    /// <para>流中可读的条目数字节不足 4 字节。</para></exception>
    /// <exception cref="IOException">A read from the stream failed.<para>读取流失败。</para></exception>
    /// <exception cref="EndOfStreamException">The stream ends before a declared entry header is fully read.
    /// <para>流在某个声明的条目头读完之前结束。</para></exception>
    /// <exception cref="Nb0CorruptedException">The declared entry count exceeds <see cref="int.MaxValue"/>, or a seekable stream is too small to hold the declared entry table.
    /// <para>声明的条目数超过 <see cref="int.MaxValue"/>，或可寻址流小到不足以容纳声明的 条目表。</para></exception>
    public static Nb0Metadata ParseFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        return ParseCore(reader);
    }

    /// <summary>Asynchronously parses an NB0 firmware file from a stream and returns metadata information.
    /// <para>异步从流中解析 NB0 固件文件,返回元数据信息。</para></summary>
    /// <param name="stream">Readable stream positioned at the start of the NB0 metadata (the little-endian 4-byte entry count). It is left open after the call; seekable streams enable the entry-table size check, magic detection and exact <see cref="Nb0Metadata.TotalSize"/>, while non-seekable streams skip those steps and estimate the total size.
    /// <para>定位在 NB0 元数据开头（小端 4 字节条目数）的可读流。调用结束后流保持打开； 可寻址流可进行条目表大小核对、魔数检测并得到精确的 <see cref="Nb0Metadata.TotalSize"/>， 不可寻址流则跳过这些步骤并估算总大小。</para></param>
    /// <param name="cancellationToken">Cancels header and entry reads; checked once per entry. The returned task then faults with <see cref="OperationCanceledException"/>.
    /// <para>取消头部与条目的读取；每个条目检查一次。届时返回的任务将以 <see cref="OperationCanceledException"/> 失败。</para></param>
    /// <returns>A task producing parsed metadata with entries, inferred types and warnings.
    /// <para>产生已解析元数据（含条目、推断类型与警告）的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="EndOfStreamException">The stream ends before the entry count or a declared entry header is fully read.
    /// <para>流在条目数或某个声明的条目头读完之前结束。</para></exception>
    /// <exception cref="Nb0CorruptedException">The declared entry count exceeds <see cref="int.MaxValue"/>, or a seekable stream is too small to hold the declared entry table.
    /// <para>声明的条目数超过 <see cref="int.MaxValue"/>，或可寻址流小到不足以容纳声明的 条目表。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested while reading.
    /// <para>读取过程中请求了取消。</para></exception>
    public static async Task<Nb0Metadata> ParseFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[Nb0EntryHeader.StructSize];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        var rawCount = BitConverter.ToUInt32(buffer, 0);
        if (rawCount > int.MaxValue)
        {
            throw new Nb0CorruptedException($"Entry count exceeds maximum supported value: {rawCount}");
        }
        var entryCount = (int)rawCount;

        if (stream.CanSeek)
        {
            var requiredSize = 4L + (entryCount * Nb0EntryHeader.StructSize);
            if (requiredSize > stream.Length)
            {
                throw new Nb0CorruptedException($"File too small to contain declared entry count: {entryCount} entries require at least {requiredSize} bytes, but file is only {stream.Length} bytes");
            }
        }

        var metadata = new Nb0Metadata
        {
            EntryCount = entryCount,
            DataSectionOffset = 4L + (entryCount * Nb0EntryHeader.StructSize)
        };

        for (var i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            var header = Nb0EntryHeader.ReadFromBuffer(buffer, 0);

            var fileEntry = Nb0FileEntry.FromHeader(header, metadata.DataSectionOffset);
            fileEntry.InferredType = DetectTypeFromMagic(stream, metadata.DataSectionOffset + header.DataOffset);
            metadata.Entries.Add(fileEntry);
        }

        TryParseMd5Entry(stream, metadata);

        InferFirmwareInfo(metadata);
        ValidateMetadata(metadata, stream.CanSeek ? stream.Length : 0);

        metadata.TotalSize = stream.CanSeek ? stream.Length : ComputeEstimatedTotalSize(metadata);

        return metadata;
    }

    internal static Nb0Metadata ParseCore(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        var countBytes = reader.ReadBytes(4);
        var rawCount = BitConverter.ToUInt32(countBytes, 0);
        if (rawCount > int.MaxValue)
        {
            throw new Nb0CorruptedException($"Entry count exceeds maximum supported value: {rawCount}");
        }
        var entryCount = (int)rawCount;

        if (stream.CanSeek)
        {
            var requiredSize = 4L + (entryCount * Nb0EntryHeader.StructSize);
            if (requiredSize > stream.Length)
            {
                throw new Nb0CorruptedException($"File too small to contain declared entry count: {entryCount} entries require at least {requiredSize} bytes, but file is only {stream.Length} bytes");
            }
        }

        var metadata = new Nb0Metadata
        {
            EntryCount = entryCount,
            DataSectionOffset = 4L + (entryCount * Nb0EntryHeader.StructSize)
        };

        for (var i = 0; i < entryCount; i++)
        {
            var header = Nb0EntryHeader.Read(reader);
            var fileEntry = Nb0FileEntry.FromHeader(header, metadata.DataSectionOffset);
            fileEntry.InferredType = DetectTypeFromMagic(stream, metadata.DataSectionOffset + header.DataOffset);
            metadata.Entries.Add(fileEntry);
        }

        TryParseMd5Entry(stream, metadata);

        InferFirmwareInfo(metadata);
        ValidateMetadata(metadata, stream.CanSeek ? stream.Length : 0);

        metadata.TotalSize = stream.CanSeek ? stream.Length : ComputeEstimatedTotalSize(metadata);

        return metadata;
    }

    private static long ComputeEstimatedTotalSize(Nb0Metadata metadata)
    {
        if (metadata.Entries.Count > 0)
        {
            var lastEntry = metadata.Entries[^1];
            var lastEntryEnd = lastEntry.Offset + lastEntry.Size;
            var remainder = lastEntryEnd % 4;
            if (remainder != 0)
            {
                lastEntryEnd += 4 - remainder;
            }
            return metadata.DataSectionOffset + lastEntryEnd;
        }
        return metadata.DataSectionOffset;
    }

    /// <summary>
    /// Detects magic number signatures at the specified offset in the data stream to infer the entry data type.
    /// <para>从数据流中检测指定偏移量处的魔数签名,推断条目数据类型。</para>
    /// </summary>
    internal static string? DetectTypeFromMagic(Stream stream, long offset)
    {
        long originalPosition = 0;
        try
        {
            originalPosition = stream.Position;
            var magic = new byte[8];

            stream.Seek(offset, SeekOrigin.Begin);
            var read = stream.Read(magic, 0, magic.Length);

            if (read >= 2)
            {
                foreach (var (sig, typeName) in KnownMagicSignatures)
                {
                    if (read >= sig.Length)
                    {
                        var match = true;
                        for (var i = 0; i < sig.Length; i++)
                        {
                            if (magic[i] != sig[i])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                        {
                            stream.Seek(originalPosition, SeekOrigin.Begin);
                            return typeName;
                        }
                    }
                }
            }

            if (read >= 8 && offset + 0x438 < stream.Length)
            {
                stream.Seek(offset + 0x438, SeekOrigin.Begin);
                var ext4Read = stream.Read(magic, 0, 2);
                if (ext4Read >= 2 && magic[0] == 0x53 && magic[1] == 0xEF)
                {
                    stream.Seek(originalPosition, SeekOrigin.Begin);
                    return "ext4";
                }
            }

            stream.Seek(originalPosition, SeekOrigin.Begin);
            return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            try
            {
                stream.Seek(originalPosition, SeekOrigin.Begin);
            }
            catch
            {

            }
            return null;
        }
    }

    /// <summary>
    /// Infers firmware type, device model, and version information based on entry names.
    /// <para>根据条目名称推断固件类型、设备型号和版本信息。</para>
    /// </summary>
    internal static void InferFirmwareInfo(Nb0Metadata metadata)
    {
        foreach (var entry in metadata.Entries)
        {
            var name = entry.Name;
            if (name.Contains("spreadtrum", StringComparison.OrdinalIgnoreCase)
                || name.Contains("unisoc", StringComparison.OrdinalIgnoreCase)
                || (name.Contains("sc", StringComparison.OrdinalIgnoreCase) && name.Contains("88", StringComparison.OrdinalIgnoreCase)))
            {
                metadata.InferredFirmwareType = "Spreadtrum/UNISOC";
            }
            if (name.Contains("uwe", StringComparison.OrdinalIgnoreCase)
                || name.Contains("udf", StringComparison.OrdinalIgnoreCase)
                || name.Contains("fdl", StringComparison.OrdinalIgnoreCase))
            {
                metadata.InferredFirmwareType ??= "Spreadtrum/UNISOC";
            }
            if (name.Contains("boot", StringComparison.OrdinalIgnoreCase) && !name.Contains("bootloader", StringComparison.OrdinalIgnoreCase))
            {
                metadata.InferredFirmwareType ??= "Android Boot";
            }
            if (name.Contains("modem", StringComparison.OrdinalIgnoreCase) || name.Contains("nvitem", StringComparison.OrdinalIgnoreCase))
            {
                metadata.InferredFirmwareType ??= "Spreadtrum/UNISOC";
            }
        }
    }

    /// <summary>
    /// Validates metadata integrity, checking entry count, name uniqueness, and data validity, adding issues to the warnings list.
    /// <para>验证元数据的完整性,检查条目数量、名称唯一性和数据有效性,并将问题添加到警告列表。</para>
    /// </summary>
    internal static void ValidateMetadata(Nb0Metadata metadata, long streamLength = 0)
    {
        if (metadata.EntryCount < 0)
        {
            throw new Nb0CorruptedException($"Invalid entry count: {metadata.EntryCount}");
        }

        if (metadata.EntryCount > 1024)
        {
            metadata.Warnings.Add($"Unusually high entry count: {metadata.EntryCount}");
        }

        var names = new HashSet<string>();
        foreach (var entry in metadata.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                metadata.Warnings.Add($"Entry at offset 0x{entry.Offset:X16} has an empty name");
            }
            if (!names.Add(entry.Name))
            {
                metadata.Warnings.Add($"Duplicate entry name: '{entry.Name}'");
            }
            if (entry.Size == 0)
            {
                metadata.Warnings.Add($"Entry '{entry.Name}' has zero size");
            }
            if (entry.Offset < 0)
            {
                metadata.Warnings.Add($"Entry '{entry.Name}' has negative offset");
            }
        }

        if (metadata.Entries.Count > 1)
        {
            var sorted = new List<(long Offset, long End, string Name)>(metadata.Entries.Count);
            foreach (var entry in metadata.Entries)
            {
                if (entry.Size > 0)
                {
                    sorted.Add((entry.Offset, entry.Offset + entry.Size, entry.Name));
                }
            }
            if (sorted.Count > 1)
            {
                sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                for (var i = 0; i < sorted.Count - 1; i++)
                {
                    if (sorted[i].End > sorted[i + 1].Offset)
                    {
                        metadata.Warnings.Add($"Overlapping data between '{sorted[i].Name}' and '{sorted[i + 1].Name}'");
                    }
                }
            }
        }

        for (var i = 1; i < metadata.Entries.Count; i++)
        {
            if (metadata.Entries[i].Offset < metadata.Entries[i - 1].Offset)
            {
                metadata.Warnings.Add($"Entry '{metadata.Entries[i].Name}' offset (0x{metadata.Entries[i].Offset:X}) is less than previous entry '{metadata.Entries[i - 1].Name}' offset (0x{metadata.Entries[i - 1].Offset:X})");
            }
        }

        if (streamLength > 0)
        {
            foreach (var entry in metadata.Entries)
            {
                var entryEnd = metadata.DataSectionOffset + entry.Offset + entry.Size;
                if (entryEnd > streamLength)
                {
                    metadata.Warnings.Add($"Entry '{entry.Name}' data extends beyond file end (offset 0x{entry.Offset:X}, size {entry.Size}, file size {streamLength})");
                }
            }
        }
    }

    private static void TryParseMd5Entry(Stream stream, Nb0Metadata metadata)
    {
        if (metadata.Entries.Count == 0)
        {
            return;
        }

        var lastEntry = metadata.Entries[^1];
        if (!lastEntry.Name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            stream.Seek(metadata.DataSectionOffset + lastEntry.Offset, SeekOrigin.Begin);
            metadata.Md5Records = ParseMd5Records(stream, lastEntry);
            metadata.BuildMd5RecordIndex();

            var nonMd5EntryCount = metadata.Entries.Count - 1;
            if (metadata.Md5Records.Count != nonMd5EntryCount)
            {
                metadata.Warnings.Add($"MD5 record count ({metadata.Md5Records.Count}) does not match non-md5 entry count ({nonMd5EntryCount})");
            }
        }
        catch (Exception ex)
        {
            metadata.Warnings.Add($"Failed to parse MD5 records from entry '{lastEntry.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Parses MD5 verification records from the data stream for the specified MD5 entry, each record is 24 bytes.
    /// <para>从数据流中解析指定 MD5 条目的校验记录,每条记录为 24 字节。</para>
    /// </summary>
    internal static List<Nb0Md5Record> ParseMd5Records(Stream stream, Nb0FileEntry md5Entry)
    {
        var records = new List<Nb0Md5Record>();
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var recordCount = (int)(md5Entry.Size / Nb0Md5Record.StructSize);
        for (var i = 0; i < recordCount; i++)
        {
            records.Add(Nb0Md5Record.Read(reader));
        }

        return records;
    }
}