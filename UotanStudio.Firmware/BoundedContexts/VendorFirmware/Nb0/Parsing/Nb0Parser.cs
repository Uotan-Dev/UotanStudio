using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Parsing;

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

    /// <summary>
    /// Parses the NB0 firmware file at the specified path and returns metadata information.
    /// <para>解析指定路径的 NB0 固件文件,返回元数据信息。</para>
    /// </summary>
    public static Nb0Metadata Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return ParseFromStream(stream);
    }

    /// <summary>
    /// Asynchronously parses the NB0 firmware file at the specified path and returns metadata information.
    /// <para>异步解析指定路径的 NB0 固件文件,返回元数据信息。</para>
    /// </summary>
    public static async Task<Nb0Metadata> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ParseFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an NB0 firmware file from a stream and returns metadata information.
    /// <para>从流中解析 NB0 固件文件,返回元数据信息。</para>
    /// </summary>
    public static Nb0Metadata ParseFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        return ParseCore(reader, stream);
    }

    /// <summary>
    /// Asynchronously parses an NB0 firmware file from a stream and returns metadata information.
    /// <para>异步从流中解析 NB0 固件文件,返回元数据信息。</para>
    /// </summary>
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

    internal static Nb0Metadata ParseCore(BinaryReader reader, Stream stream)
    {
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