using System.Text;
using UotanStudio.Firmware.Cpio.Extraction;
using UotanStudio.Firmware.Cpio.Formats.Cpio;
using UotanStudio.Firmware.Cpio.Formats.Tar;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Cpio.Writing;

namespace UotanStudio.Firmware.Cpio;

/// <summary>
/// High-level facade for reading, listing, extracting and saving CPIO/TAR archives.
/// <para>用于读取、列表、解包与保存 CPIO/TAR 归档的高层门面。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CpioArchive"/> class.
/// <para>初始化 <see cref="CpioArchive"/> 类的新实例。</para>
/// </remarks>
/// <param name="extractor">Optional extractor used by extraction-related APIs. When null, a default extractor is created.<para>可选：解包相关 API 使用的解包器；为 null 时创建默认解包器。</para></param>
public sealed class CpioArchive(CpioExtractor? extractor = null)
{
    private readonly CpioArchiveReader _cpioReader = new();
    private readonly CpioArchiveWriter _cpioWriter = new();
    private readonly TarArchiveReader _tarReader = new();
    private readonly TarArchiveWriter _tarWriter = new();
    private readonly CpioExtractor _extractor = extractor ?? new CpioExtractor();

    /// <summary>
    /// Reads all entries from an archive stream into memory.
    /// <para>从归档流中读取全部条目到内存。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format auto-detection.<para>可选的格式提示，用于跳过格式自动识别。</para></param>
    /// <returns>A list containing all archive entries.<para>包含全部归档条目的列表。</para></returns>
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntries(stream, formatHint)];

    /// <summary>
    /// Reads entries from an archive stream as a lazy sequence.
    /// <para>以惰性序列从归档流中读取条目。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format auto-detection.<para>可选的格式提示，用于跳过格式自动识别。</para></param>
    /// <returns>An enumerable that yields archive entries as they are read.<para>读取时逐个产出归档条目的可枚举序列。</para></returns>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream, ArchiveFormat? formatHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (formatHint is null && !stream.CanSeek)
        {
            throw new NotSupportedException("Format hint is required when reading from a non-seekable stream.");
        }

        var format = formatHint ?? DetectFormat(stream);
        return format switch
        {
            ArchiveFormat.Tar or ArchiveFormat.Ustar => _tarReader.ReadEntries(stream),
            // 所有 CPIO 二进制/ASCII 变体由 CPIO reader 读取。
            // All CPIO binary/ASCII variants are handled by the CPIO reader.
            _ => _cpioReader.ReadEntries(stream, format),
        };
    }

    /// <summary>
    /// Returns lightweight archive entries suitable for listing and inspection.
    /// This is the closest library-level equivalent to GNU cpio's <c>-t</c> / <c>--list</c> mode.
    /// <para>返回适合列表与检查的轻量级归档条目；这是 GNU cpio 的 <c>-t</c> / <c>--list</c> 模式在库层面的最近等价物。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format auto-detection.<para>可选的格式提示，用于跳过格式自动识别。</para></param>
    /// <returns>A list containing lightweight archive entry summaries.<para>包含轻量级归档条目摘要的列表。</para></returns>
    public IReadOnlyList<ArchiveEntrySummary> ListEntries(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntries(stream, formatHint).Select(ToSummary)];

    /// <summary>
    /// Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <param name="destinationDirectory">Target directory path.<para>目标目录路径。</para></param>
    /// <param name="options">Optional extraction behavior options.<para>可选的解包行为选项。</para></param>
    /// <param name="formatHint">Optional format hint used to skip format auto-detection.<para>可选的格式提示，用于跳过格式自动识别。</para></param>
    public void ExtractToDirectory(Stream stream, string destinationDirectory, ExtractOptions? options = null, ArchiveFormat? formatHint = null)
    {
        var entries = ReadEntries(stream, formatHint);
        _extractor.ExtractToDirectory(entries, destinationDirectory, options);
    }

    /// <summary>
    /// Saves all entries to an archive stream.
    /// <para>将全部条目保存到归档流。</para>
    /// </summary>
    /// <param name="stream">Destination archive stream.<para>目标归档流。</para></param>
    /// <param name="entries">Entries to write.<para>待写入的条目。</para></param>
    /// <param name="format">Archive format to write.<para>要写入的归档格式。</para></param>
    /// <param name="options">Optional write behavior options.<para>可选的写入行为选项。</para></param>
    public void SaveAll(Stream stream, IEnumerable<ArchiveEntry> entries, ArchiveFormat format, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var opts = options ?? new WriteOptions();
        var preparedEntries = PrepareEntriesForWrite(entries, opts);

        switch (format)
        {
            case ArchiveFormat.Tar:
            case ArchiveFormat.Ustar:
                _tarWriter.WriteAll(stream, preparedEntries, format);
                break;
            case ArchiveFormat.NewAscii:
            case ArchiveFormat.CrcAscii:
            case ArchiveFormat.OldAscii:
            case ArchiveFormat.HpOldAscii:
            case ArchiveFormat.Binary:
            case ArchiveFormat.HpBinary:
                _cpioWriter.WriteAll(stream, preparedEntries, format, opts);
                break;
            case ArchiveFormat.Unknown:
                break;
            default:
                throw new NotSupportedException($"Writing archive format '{format}' is not supported.");
        }
    }

    /// <summary>
    /// Detects the archive format by probing header bytes from a seekable stream.
    /// <para>通过探测可寻址流的头部字节检测归档格式。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <returns>The detected archive format.<para>检测到的归档格式。</para></returns>
    public ArchiveFormat DetectFormat(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Format detection requires a seekable stream.");
        }

        var origin = stream.Position;
        try
        {
            Span<byte> probe = stackalloc byte[512];
            var read = stream.Read(probe);
            if (read < 6)
            {
                throw new EndOfStreamException("Unexpected end of stream while detecting format.");
            }

            var cpioMagic = Encoding.ASCII.GetString(probe[..6]);
            if (cpioMagic == "070701")
            {
                return ArchiveFormat.NewAscii;
            }

            if (cpioMagic == "070702")
            {
                return ArchiveFormat.CrcAscii;
            }

            if (cpioMagic == "070707")
            {
                return ArchiveFormat.OldAscii;
            }

            if (read >= 2)
            {
                var binaryMagic = (ushort)((probe[0] << 8) | probe[1]);
                var swapped = (ushort)((probe[1] << 8) | probe[0]);
                if (binaryMagic == 0x71C7 || swapped == 0x71C7)
                {
                    return ArchiveFormat.Binary;
                }
            }

            if (read >= 265)
            {
                // POSIX ustar marker at offset 257 with "ustar" prefix.
                var marker = Encoding.ASCII.GetString(probe.Slice(257, 5));
                if (marker == "ustar")
                {
                    return ArchiveFormat.Ustar;
                }
            }

            return read >= 512 && IsLikelyTarHeader(probe[..512])
                ? ArchiveFormat.Tar
                : throw new InvalidDataException("Unknown archive format.");
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static IReadOnlyList<ArchiveEntry> PrepareEntriesForWrite(IEnumerable<ArchiveEntry> entries, WriteOptions options)
    {
        var transformed = new List<ArchiveEntry>();
        foreach (var entry in entries)
        {
            transformed.Add(new ArchiveEntry
            {
                Name = entry.Name,
                LinkName = entry.LinkName,
                Data = entry.Data,
                Metadata = ApplyOwnerOverride(entry.Metadata, options),
            });
        }

        if (options.IgnoreDeviceNumbers || options.RenumberInodes)
        {
            ApplyMetadataNormalization(transformed, options);
        }

        return transformed;
    }

    private static void ApplyMetadataNormalization(List<ArchiveEntry> entries, WriteOptions options)
    {
        ulong nextInode = 1;
        var inodeMap = new Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), ulong>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var metadata = entry.Metadata;

            var deviceMajor = options.IgnoreDeviceNumbers ? 0U : metadata.DeviceMajor;
            var deviceMinor = options.IgnoreDeviceNumbers ? 0U : metadata.DeviceMinor;

            var inode = metadata.Inode;
            if (options.RenumberInodes && inode != 0)
            {
                var key = (metadata.Inode, deviceMajor, deviceMinor);
                if (!inodeMap.TryGetValue(key, out inode))
                {
                    inode = nextInode++;
                    inodeMap[key] = inode;
                }
            }

            entries[i] = new ArchiveEntry
            {
                Name = entry.Name,
                LinkName = entry.LinkName,
                Data = entry.Data,
                Metadata = new ArchiveEntryMetadata
                {
                    Inode = inode,
                    Mode = metadata.Mode,
                    UserId = metadata.UserId,
                    GroupId = metadata.GroupId,
                    DeviceMajor = deviceMajor,
                    DeviceMinor = deviceMinor,
                    RDeviceMajor = metadata.RDeviceMajor,
                    RDeviceMinor = metadata.RDeviceMinor,
                    LinkCount = metadata.LinkCount,
                    ModificationTimeUnixSeconds = metadata.ModificationTimeUnixSeconds,
                    Checksum = metadata.Checksum,
                    FileType = metadata.FileType,
                    UnixPermissions = metadata.UnixPermissions,
                    ExtendedAttributes = metadata.ExtendedAttributes,
                },
            };
        }
    }

    private static ArchiveEntryMetadata ApplyOwnerOverride(ArchiveEntryMetadata metadata, WriteOptions options)
    {
        return !options.OwnerUserId.HasValue && !options.OwnerGroupId.HasValue
            ? metadata
            : new ArchiveEntryMetadata
            {
                Inode = metadata.Inode,
                Mode = metadata.Mode,
                UserId = options.OwnerUserId ?? metadata.UserId,
                GroupId = options.OwnerGroupId ?? metadata.GroupId,
                DeviceMajor = metadata.DeviceMajor,
                DeviceMinor = metadata.DeviceMinor,
                RDeviceMajor = metadata.RDeviceMajor,
                RDeviceMinor = metadata.RDeviceMinor,
                LinkCount = metadata.LinkCount,
                ModificationTimeUnixSeconds = metadata.ModificationTimeUnixSeconds,
                Checksum = metadata.Checksum,
                FileType = metadata.FileType,
                UnixPermissions = metadata.UnixPermissions,
                ExtendedAttributes = metadata.ExtendedAttributes,
            };
    }

    private static ArchiveEntrySummary ToSummary(ArchiveEntry entry)
    {
        return new ArchiveEntrySummary
        {
            Name = entry.Name,
            LinkName = entry.LinkName,
            DataLength = entry.Data.LongLength,
            Metadata = entry.Metadata,
        };
    }

    private static bool IsLikelyTarHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 512)
        {
            return false;
        }

        var expected = ParseTarOctal(header.Slice(148, 8));
        uint actual = 0;
        for (var i = 0; i < 512; i++)
        {
            actual += i is >= 148 and < 156 ? (byte)' ' : header[i];
        }

        return expected == actual;
    }

    private static uint ParseTarOctal(ReadOnlySpan<byte> octal)
    {
        var text = Encoding.ASCII.GetString(octal).TrimEnd('\0', ' ');
        return text.Length == 0 ? 0U : Convert.ToUInt32(text, 8);
    }
}