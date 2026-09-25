using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Formats.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Formats.Tar;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.Archive.Writing;

namespace UotanStudio.Firmware.BoundedContexts.Archive;

/// <summary>
/// Facade that dispatches archive reads and writes to the CPIO or TAR implementation based on format detection.
/// <para>门面类，依据格式检测将归档读写分发给 CPIO 或 TAR 实现。</para>
/// </summary>
/// <param name="extractor">
/// Optional extractor used by the directory-extraction overloads; a default one is created when <see langword="null"/>.
/// <para>目录解包重载使用的可选解压器；<see langword="null"/> 时创建默认实例。</para>
/// </param>
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
    /// <param name="stream">
    /// Readable archive source kept open by the reader; each payload is copied under the 256 MiB materialization cap.
    /// <para>可读的归档源流，读取器不会将其关闭；每个负载在 256 MiB 物化上限内复制。</para>
    /// </param>
    /// <param name="formatHint">
    /// Optional format override; <see langword="null"/> detects the format from header bytes, which requires a seekable stream.
    /// <para>可选的格式覆盖；<see langword="null"/> 表示从头部字节检测格式，这要求流可定位。</para>
    /// </param>
    /// <returns>
    /// A fully materialized list of entries with payloads in <c>Data</c>.
    /// <para>完全物化的条目列表，负载位于 <c>Data</c> 中。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The format is unknown or an entry exceeds the materialization limit.
    /// <para>格式未知，或条目超出物化上限。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable archive source kept open by the reader.
    /// <para>可读的归档源流，读取器不会将其关闭。</para>
    /// </param>
    /// <param name="formatHint">
    /// Optional format override; <see langword="null"/> detects the format from header bytes, which requires a seekable stream.
    /// <para>可选的格式覆盖；<see langword="null"/> 表示从头部字节检测格式，这要求流可定位。</para>
    /// </param>
    /// <returns>
    /// A lazy sequence whose payloads are copied out of the source as enumeration advances, capped at 256 MiB per entry.
    /// <para>惰性序列，其负载在枚举推进时从源中复制，单条目上限 256 MiB。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// An entry exceeds the materialization limit or the archive is malformed.
    /// <para>条目超出物化上限，或归档格式非法。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable archive source kept open by the reader.
    /// <para>可读的归档源流，读取器不会将其关闭。</para>
    /// </param>
    /// <param name="formatHint">
    /// Required when <paramref name="stream"/> cannot seek; <see langword="null"/> on a seekable stream triggers header detection.
    /// <para>当 <paramref name="stream"/> 不可定位时必须提供；可定位流上传入 <see langword="null"/> 将触发头部检测。</para>
    /// </param>
    /// <returns>
    /// Entries routed to the CPIO or TAR reader according to the format; payloads are live views valid only during enumeration.
    /// <para>按格式路由到 CPIO 或 TAR 读取器得到的条目；负载为仅在枚举期间有效的实时视图。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// No format hint was given for a non-seekable stream.
    /// <para>对不可定位的流未提供格式提示。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Readable archive source kept open by the reader; payloads are never copied.
    /// <para>可读的归档源流，读取器不会将其关闭；负载不会被复制。</para>
    /// </param>
    /// <param name="formatHint">
    /// Optional format override; <see langword="null"/> detects the format from header bytes, which requires a seekable stream.
    /// <para>可选的格式覆盖；<see langword="null"/> 表示从头部字节检测格式，这要求流可定位。</para>
    /// </param>
    /// <returns>
    /// A snapshot of names, link targets, declared lengths and metadata for every entry.
    /// <para>包含每个条目的名称、链接目标、声明长度与元数据的快照。</para>
    /// </returns>
    public IReadOnlyList<ArchiveEntrySummary> ListEntries(Stream stream, ArchiveFormat? formatHint = null) => [.. ReadEntriesStreaming(stream, formatHint).Select(ToSummary)];

    /// <summary>
    /// Extracts archive entries to a destination directory.
    /// <para>将归档条目解包到目标目录。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable archive source kept open by the reader.
    /// <para>可读的归档源流，读取器不会将其关闭。</para>
    /// </param>
    /// <param name="destinationDirectory">
    /// Directory that receives the extracted entries.
    /// <para>接收解出条目的目录。</para>
    /// </param>
    /// <param name="options">
    /// Optional extraction settings such as overwrite policy; <see langword="null"/> uses extractor defaults.
    /// <para>可选解包设置（如覆盖策略）；<see langword="null"/> 使用解压器默认值。</para>
    /// </param>
    /// <param name="formatHint">
    /// Optional format override; <see langword="null"/> detects the format from header bytes, which requires a seekable stream.
    /// <para>可选的格式覆盖；<see langword="null"/> 表示从头部字节检测格式，这要求流可定位。</para>
    /// </param>
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
    /// <param name="stream">
    /// Readable archive source kept open by the reader.
    /// <para>可读的归档源流，读取器不会将其关闭。</para>
    /// </param>
    /// <param name="destinationDirectory">
    /// Directory that receives the extracted entries.
    /// <para>接收解出条目的目录。</para>
    /// </param>
    /// <param name="options">
    /// Optional extraction settings such as overwrite policy; <see langword="null"/> uses extractor defaults.
    /// <para>可选解包设置（如覆盖策略）；<see langword="null"/> 使用解压器默认值。</para>
    /// </param>
    /// <param name="formatHint">
    /// Optional format override; <see langword="null"/> detects the format from header bytes, which requires a seekable stream.
    /// <para>可选的格式覆盖；<see langword="null"/> 表示从头部字节检测格式，这要求流可定位。</para>
    /// </param>
    /// <param name="entryNames">
    /// When this method returns, the names of all entries encountered during extraction, in archive order.
    /// <para>方法返回时，为解包过程中按归档顺序遇到的全部条目名。</para>
    /// </param>
    /// <returns>
    /// The same collected name list that is written to <paramref name="entryNames"/>.
    /// <para>与写入 <paramref name="entryNames"/> 相同的条目名列表。</para>
    /// </returns>
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
    /// <param name="stream">
    /// Writable destination kept open by the writer.
    /// <para>可写目标流，写入器不会将其关闭。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to serialize; metadata is normalized first according to <paramref name="options"/>.
    /// <para>待序列化的条目；元数据会先按 <paramref name="options"/> 规范化。</para>
    /// </param>
    /// <param name="format">
    /// Target format selecting the TAR or CPIO writer; <see cref="ArchiveFormat.Unknown"/> writes nothing.
    /// <para>决定使用 TAR 还是 CPIO 写入器的目标格式；<see cref="ArchiveFormat.Unknown"/> 不写入任何内容。</para>
    /// </param>
    /// <param name="options">
    /// Optional write settings (owner override, inode renumbering, trailer); <see langword="null"/> uses <c>WriteOptions</c> defaults.
    /// <para>可选写入设置（属主覆盖、inode 重编号、尾部记录）；<see langword="null"/> 使用 <c>WriteOptions</c> 默认值。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entries"/> is <see langword="null"/>. <para><paramref name="stream"/> 或 <paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="format"/> is a value outside the known archive formats.
    /// <para><paramref name="format"/> 为已知归档格式之外的取值。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Seekable archive source positioned at the start of the archive; the position is restored before returning.
    /// <para>定位在归档起始处的可寻址源流；返回前会恢复其位置。</para>
    /// </param>
    /// <returns>
    /// The detected <see cref="ArchiveFormat"/>: a CPIO ASCII or binary variant, <see cref="ArchiveFormat.Ustar"/> for a ustar marker, or <see cref="ArchiveFormat.Tar"/> for a header whose checksum field validates.
    /// <para>检测得到的 <see cref="ArchiveFormat"/>：CPIO ASCII 或 binary 变体、带 ustar 标记时的 <see cref="ArchiveFormat.Ustar"/>，或校验和字段可通过验证的 <see cref="ArchiveFormat.Tar"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> cannot seek.
    /// <para><paramref name="stream"/> 不可定位。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Fewer than six bytes remain at the current position.
    /// <para>当前位置剩余字节不足六个。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The probed bytes match no known CPIO or TAR layout.
    /// <para>探测字节不匹配任何已知 CPIO 或 TAR 布局。</para>
    /// </exception>
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
        if (expected is null)
        {
            return false;
        }
        uint actual = 0;
        for (var i = 0; i < 512; i++)
        {
            actual += i is >= 148 and < 156 ? (byte)' ' : header[i];
        }

        return expected == actual;
    }

    private static uint? ParseTarOctal(ReadOnlySpan<byte> octal)
    {
        var end = octal.Length;
        while (end > 0 && (octal[end - 1] is (byte)'\0' or (byte)' '))
        {
            end--;
        }

        uint value = 0;
        for (var i = 0; i < end; i++)
        {
            var digit = octal[i] - (byte)'0';
            if (digit is < 0 or > 7 || (value > (uint.MaxValue - digit) / 8))
            {
                return null;
            }

            value = (value * 8) + (uint)digit;
        }

        return value;
    }
}