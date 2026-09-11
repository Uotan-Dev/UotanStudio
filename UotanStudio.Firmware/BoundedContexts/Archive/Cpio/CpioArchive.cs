using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Tar;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Writing;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio;

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
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream, ArchiveFormat? formatHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var entries = new List<ArchiveEntry>();
        foreach (var entry in ReadEntries(stream, formatHint))
        {
            entries.Add(Materialize(entry));
        }
        return entries;
    }

    /// <summary>
    /// Reads entries lazily while giving every returned entry independent, materialized data.
    /// <para>惰性读取条目，同时为每个返回条目提供独立物化的数据。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream, ArchiveFormat? formatHint = null)
    {
        foreach (var entry in ReadEntriesStreaming(stream, formatHint))
        {
            yield return Materialize(entry);
        }
    }

    /// <summary>
    /// Reads entries as a single-pass streaming sequence.
    /// <para>以单遍流式序列读取条目；请求下一项前必须消费当前负载。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntriesStreaming(Stream stream, ArchiveFormat? formatHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (formatHint is null && !stream.CanSeek)
        {
            throw new NotSupportedException("Format hint is required when reading from a non-seekable stream.");
        }

        var format = formatHint ?? DetectFormat(stream);
        return format switch
        {
            ArchiveFormat.Tar or ArchiveFormat.Ustar => _tarReader.ReadEntriesStreaming(stream),

            _ => _cpioReader.ReadEntriesStreaming(stream, format),
        };
    }

    /// <summary>
    /// Returns lightweight archive entries suitable for listing and inspection.
    /// <para>返回适合列表与检查的轻量级归档条目；这是 GNU cpio 的 -t / --list 模式在库层面的最近等价物。</para>
    /// </summary>
    public IReadOnlyList<ArchiveEntrySummary> ListEntries(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntriesStreaming(stream, formatHint).Select(ToSummary)];

    /// <summary>
    /// Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para>
    /// </summary>
    public void ExtractToDirectory(Stream stream, string destinationDirectory, ExtractOptions? options = null, ArchiveFormat? formatHint = null)
    {
        var entries = ReadEntriesStreaming(stream, formatHint);
        _extractor.ExtractToDirectory(entries, destinationDirectory, options);
    }

    /// <summary>
    /// Extracts archive entries to a destination directory and collects the entry
    /// names during the same single pass, so callers do not need to re-read the
    /// archive just to list its contents.
    /// <para>将归档条目解包到目标目录，并在同一次遍历中收集条目名，调用方无需
    /// 为获取清单而重新读取归档。</para>
    /// </summary>
    public IReadOnlyList<string> ExtractToDirectory(Stream stream, string destinationDirectory, ExtractOptions? options, ArchiveFormat? formatHint, out IReadOnlyList<string> entryNames)
    {
        var names = new List<string>();
        IEnumerable<ArchiveEntry> Tap()
        {
            foreach (var entry in ReadEntriesStreaming(stream, formatHint))
            {
                names.Add(entry.Name);
                yield return entry;
            }
        }

        _extractor.ExtractToDirectory(Tap(), destinationDirectory, options);
        entryNames = names;
        return names;
    }

    /// <summary>
    /// Saves all entries to an archive stream.
    /// <para>将全部条目保存到归档流。</para>
    /// </summary>
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
            var read = stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
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

    private static IEnumerable<ArchiveEntry> PrepareEntriesForWrite(IEnumerable<ArchiveEntry> entries, WriteOptions options)
    {
        ulong nextInode = 1;
        var inodeMap = new Dictionary<(ulong Inode, uint DevMajor, uint DevMinor), ulong>();
        foreach (var entry in entries)
        {
            var metadata = ApplyOwnerOverride(entry.Metadata, options);
            var deviceMajor = options.IgnoreDeviceNumbers ? 0U : metadata.DeviceMajor;
            var deviceMinor = options.IgnoreDeviceNumbers ? 0U : metadata.DeviceMinor;
            var inode = metadata.Inode;
            if (options.RenumberInodes && inode != 0)
            {
                var key = (metadata.Inode, deviceMajor, deviceMinor);
                if (!inodeMap.TryGetValue(key, out inode)) inodeMap[key] = inode = nextInode++;
            }

            yield return new ArchiveEntry
            {
                Name = entry.Name,
                LinkName = entry.LinkName,
                Data = entry.Data,
                DataStream = entry.DataStream,
                DataStreamFactory = entry.DataStreamFactory,
                DeclaredDataLength = entry.DeclaredDataLength,
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
                DataStream = entry.DataStream,
                DataStreamFactory = entry.DataStreamFactory,
                DeclaredDataLength = entry.DeclaredDataLength,
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
            DataLength = entry.DataLength,
            Metadata = entry.Metadata,
        };
    }

    /// <summary>
    /// Materializes a streaming entry's payload into ArchiveEntry.Data.
    /// <para>将流式条目的负载物化到 ArchiveEntry.Data。</para>
    /// </summary>
    private static ArchiveEntry Materialize(ArchiveEntry entry)
    {
        if (entry.DataStream is null && entry.DataStreamFactory is null)
        {
            return entry;
        }

        if (entry.DataLength > 256 * 1024 * 1024)
            throw new InvalidDataException("Archive entry exceeds the 256 MiB materialization limit; use streaming access.");
        var data = new byte[checked((int)entry.DataLength)];
        using var opened = entry.DataStream is null
            ? entry.DataStreamFactory?.Invoke() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.")
            : null;
        var source = entry.DataStream ?? opened!;
        if (source.CanSeek)
        {
            source.Position = 0;
        }
        source.ReadExactly(data);
        return new ArchiveEntry
        {
            Name = entry.Name,
            LinkName = entry.LinkName,
            Data = data,
            DataStream = null,
            DataStreamFactory = null,
            DeclaredDataLength = null,
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