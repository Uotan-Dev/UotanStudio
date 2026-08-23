using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.Cpio.Model;

namespace UotanStudio.Firmware.Cpio.Formats.Tar;

/// <summary>
/// Reads TAR-family archives into normalized archive entries.
/// <para>将 TAR 系归档读取为规范化的归档条目。</para>
/// </summary>
public sealed class TarArchiveReader
{
    /// <summary>
    /// Reads all TAR entries from a stream into memory.
    /// <para>从流中读取全部 TAR 条目到内存。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <returns>A list containing all archive entries.<para>包含全部归档条目的列表。</para></returns>
    public IReadOnlyList<ArchiveEntry> ReadAll(Stream stream) => [.. ReadEntries(stream)];

    /// <summary>
    /// Reads TAR entries from a stream as a lazy sequence.
    /// <para>以惰性序列从流中读取 TAR 条目。</para>
    /// </summary>
    /// <param name="stream">Source archive stream.<para>源归档流。</para></param>
    /// <returns>An enumerable that yields archive entries as they are read.<para>读取时逐个产出归档条目的可枚举序列。</para></returns>
    public IEnumerable<ArchiveEntry> ReadEntries(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry(copyData: true) is { } tarEntry)
        {
            var fileType = tarEntry.EntryType switch
            {
                TarEntryType.Directory => CpioFileType.Directory,
                TarEntryType.SymbolicLink => CpioFileType.SymbolicLink,
                TarEntryType.HardLink => CpioFileType.HardLink,
                TarEntryType.BlockDevice => CpioFileType.BlockDevice,
                TarEntryType.CharacterDevice => CpioFileType.CharacterDevice,
                TarEntryType.Fifo => CpioFileType.Fifo,
                TarEntryType.V7RegularFile => throw new NotImplementedException(),
                TarEntryType.RegularFile => throw new NotImplementedException(),
                TarEntryType.ContiguousFile => throw new NotImplementedException(),
                TarEntryType.DirectoryList => throw new NotImplementedException(),
                TarEntryType.LongLink => throw new NotImplementedException(),
                TarEntryType.LongPath => throw new NotImplementedException(),
                TarEntryType.MultiVolume => throw new NotImplementedException(),
                TarEntryType.RenamedOrSymlinked => throw new NotImplementedException(),
                TarEntryType.SparseFile => throw new NotImplementedException(),
                TarEntryType.TapeVolume => throw new NotImplementedException(),
                TarEntryType.GlobalExtendedAttributes => throw new NotImplementedException(),
                TarEntryType.ExtendedAttributes => throw new NotImplementedException(),
                _ => CpioFileType.Regular,
            };

            var data = Array.Empty<byte>();
            if (tarEntry.DataStream is not null)
            {
                using var ms = new MemoryStream();
                tarEntry.DataStream.CopyTo(ms);
                data = ms.ToArray();
            }

            var mode = tarEntry.Mode;
            var uid = tarEntry.Uid;
            var gid = tarEntry.Gid;
            var xattrs = ExtractExtendedAttributes(tarEntry);

            yield return new ArchiveEntry
            {
                Name = tarEntry.Name,
                LinkName = tarEntry.LinkName,
                Data = data,
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