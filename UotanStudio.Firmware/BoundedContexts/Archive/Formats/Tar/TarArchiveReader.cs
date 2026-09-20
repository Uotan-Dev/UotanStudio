using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Formats.Tar;

public sealed class TarArchiveReader
{
    /// <summary>
    /// Reads all TAR entries from a stream into memory.
    /// <para>从流中读取全部 TAR 条目到内存。</para>
    /// </summary>
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var entries = new List<ArchiveEntry>();
        foreach (var entry in ReadEntries(stream))
        {
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Reads TAR entries with independently usable payload data.
    /// <para>读取 TAR 条目，每个条目的数据可独立使用。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var entry in ReadEntriesStreaming(stream))
        {
            yield return Materialize(entry);
        }
    }

    /// <summary>
    /// Reads TAR entries lazily; consume each payload stream before requesting the next entry.
    /// <para>延迟读取 TAR 条目；获取下一条目前须读完当前数据流。</para>
    /// </summary>
    public IEnumerable<ArchiveEntry> ReadEntriesStreaming(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } tarEntry)
        {
            var fileType = tarEntry.EntryType switch
            {
                TarEntryType.Directory => CpioFileType.Directory,
                TarEntryType.SymbolicLink => CpioFileType.SymbolicLink,
                TarEntryType.HardLink => CpioFileType.HardLink,
                TarEntryType.BlockDevice => CpioFileType.BlockDevice,
                TarEntryType.CharacterDevice => CpioFileType.CharacterDevice,
                TarEntryType.Fifo => CpioFileType.Fifo,


                TarEntryType.V7RegularFile => CpioFileType.Regular,
                TarEntryType.RegularFile => CpioFileType.Regular,
                TarEntryType.ContiguousFile => CpioFileType.Regular,
                TarEntryType.SparseFile => CpioFileType.Regular,
                TarEntryType.MultiVolume => CpioFileType.Regular,
                TarEntryType.RenamedOrSymlinked => CpioFileType.Regular,
                TarEntryType.DirectoryList => CpioFileType.Directory,


                TarEntryType.LongLink => CpioFileType.Unknown,
                TarEntryType.LongPath => CpioFileType.Unknown,
                TarEntryType.TapeVolume => CpioFileType.Unknown,
                TarEntryType.GlobalExtendedAttributes => CpioFileType.Unknown,
                TarEntryType.ExtendedAttributes => CpioFileType.Unknown,
                _ => CpioFileType.Regular,
            };

            var mode = tarEntry.Mode;
            var uid = tarEntry.Uid;
            var gid = tarEntry.Gid;
            var xattrs = ExtractExtendedAttributes(tarEntry);

            var isRegular = fileType == CpioFileType.Regular;
            yield return new ArchiveEntry
            {
                Name = tarEntry.Name,
                LinkName = tarEntry.LinkName,


                Data = [],
                DataStream = isRegular ? tarEntry.DataStream : null,
                DeclaredDataLength = isRegular ? tarEntry.Length : 0,
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = checked((uint)(int)mode),
                    UserId = checked((uint)uid),
                    GroupId = checked((uint)gid),
                    ModificationTimeUnixSeconds = tarEntry.ModificationTime.ToUnixTimeSeconds(),
                    FileType = fileType,
                    UnixPermissions = (int)mode,
                    ExtendedAttributes = xattrs,
                },
            };
        }
    }

    /// <summary>
    /// Materializes a streaming entry's payload into ArchiveEntry.Data.
    /// <para>将流式条目的负载物化到 ArchiveEntry.Data。</para>
    /// </summary>
    private static ArchiveEntry Materialize(ArchiveEntry entry)
    {
        if (entry.DataStream is null)
        {
            return entry;
        }

        using var ms = new MemoryStream();
        var source = entry.DataStream;
        if (source.CanSeek)
        {
            source.Position = 0;
        }
        source.CopyTo(ms);
        return new ArchiveEntry
        {
            Name = entry.Name,
            LinkName = entry.LinkName,
            Data = ms.ToArray(),
            Metadata = entry.Metadata,
        };
    }

    private static IReadOnlyDictionary<string, byte[]> ExtractExtendedAttributes(TarEntry tarEntry)
    {
        if (tarEntry is not PaxTarEntry paxEntry)
        {
            return new Dictionary<string, byte[]>();
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, value) in paxEntry.ExtendedAttributes)
        {
            const string schilyPrefix = "SCHILY.xattr.";
            const string libarchivePrefix = "LIBARCHIVE.xattr.";

            if (key.StartsWith(schilyPrefix, StringComparison.Ordinal))
            {
                result[key[schilyPrefix.Length..]] = Encoding.UTF8.GetBytes(value);
                continue;
            }

            if (key.StartsWith(libarchivePrefix, StringComparison.Ordinal))
            {
                result[key[libarchivePrefix.Length..]] = Encoding.UTF8.GetBytes(value);
            }
        }

        return result;
    }
}