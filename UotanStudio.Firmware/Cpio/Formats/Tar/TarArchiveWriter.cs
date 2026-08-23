using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.Cpio.Model;

namespace UotanStudio.Firmware.Cpio.Formats.Tar;

/// <summary>
/// Writes TAR-family archives from normalized archive entries.
/// <para>从规范化的归档条目写入 TAR 系归档。</para>
/// </summary>
public sealed class TarArchiveWriter
{
    /// <summary>
    /// Writes all provided entries to a TAR stream.
    /// <para>将提供的全部条目写入 TAR 流。</para>
    /// </summary>
    /// <param name="stream">Destination archive stream.<para>目标归档流。</para></param>
    /// <param name="entries">Entries to write.<para>待写入的条目。</para></param>
    /// <param name="format">Target archive format (Tar for PAX, otherwise USTAR).<para>目标归档格式（Tar 为 PAX，其余为 USTAR）。</para></param>
    public void WriteAll(Stream stream, IEnumerable<ArchiveEntry> entries, ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var tarFormat = format == ArchiveFormat.Tar ? TarEntryFormat.Pax : TarEntryFormat.Ustar;
        using var writer = new TarWriter(stream, tarFormat, leaveOpen: true);
        foreach (var entry in entries)
        {
            var tarEntry = CreateTarEntry(entry, format);
            writer.WriteEntry(tarEntry);
        }
    }

    private static TarEntry CreateTarEntry(ArchiveEntry entry, ArchiveFormat format)
    {
        var entryType = entry.Metadata.FileType switch
        {
            CpioFileType.Directory => TarEntryType.Directory,
            CpioFileType.SymbolicLink => TarEntryType.SymbolicLink,
            CpioFileType.HardLink => TarEntryType.HardLink,
            CpioFileType.CharacterDevice => TarEntryType.CharacterDevice,
            CpioFileType.BlockDevice => TarEntryType.BlockDevice,
            CpioFileType.Fifo => TarEntryType.Fifo,
            CpioFileType.Unknown => throw new NotImplementedException(),
            CpioFileType.Regular => throw new NotImplementedException(),
            CpioFileType.Socket => throw new NotImplementedException(),
            _ => TarEntryType.RegularFile,
        };

        var name = entryType == TarEntryType.Directory && !entry.Name.EndsWith("/", StringComparison.Ordinal)
            ? entry.Name + "/"
            : entry.Name;

        var tarEntry = format == ArchiveFormat.Tar
            ? new PaxTarEntry(entryType, name, BuildPaxExtendedAttributes(entry))
            : (TarEntry)new UstarTarEntry(entryType, name);
        tarEntry.Mode = (UnixFileMode)(entry.Metadata.UnixPermissions & 0x0FFF);
        tarEntry.Uid = checked((int)entry.Metadata.UserId);
        tarEntry.Gid = checked((int)entry.Metadata.GroupId);
        if (entry.Metadata.ModificationTimeUnixSeconds > 0)
        {
            tarEntry.ModificationTime = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.ModificationTimeUnixSeconds);
        }

        if (entryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            tarEntry.LinkName = entry.LinkName ?? string.Empty;
        }

        if (entryType == TarEntryType.RegularFile)
        {
            tarEntry.DataStream = new MemoryStream(entry.Data, writable: false);
        }

        return tarEntry;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildPaxExtendedAttributes(ArchiveEntry entry)
    {
        if (entry.Metadata.ExtendedAttributes.Count == 0)
        {
            return [];
        }

        var result = new List<KeyValuePair<string, string>>(entry.Metadata.ExtendedAttributes.Count);
        foreach (var (key, value) in entry.Metadata.ExtendedAttributes)
        {
            var paxKey = key.StartsWith("SCHILY.xattr.", StringComparison.Ordinal)
                ? key
                : "SCHILY.xattr." + key;
            result.Add(new KeyValuePair<string, string>(paxKey, Encoding.UTF8.GetString(value)));
        }

        return result;
    }
}