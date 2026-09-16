using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio;

/// <summary>
/// Mutable operations for ramdisk CPIO archives, backed by the shared archive model.
/// <para>基于共享归档模型提供 ramdisk CPIO 的可变操作。</para>
/// </summary>
public sealed class CpioArchiveEditor
{
    private readonly CpioArchive _archive = new();
    private readonly List<ArchiveEntry> _entries = [];

    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public void Load(Stream stream, ArchiveFormat? formatHint = ArchiveFormat.NewAscii)
    {
        _entries.Clear();
        _entries.AddRange(_archive.ReadAll(stream, formatHint));
    }

    public bool Exists(string path) => Find(path) is not null;

    public ArchiveEntry? Find(string path)
    {
        var normalized = Normalize(path);
        return _entries.FirstOrDefault(entry => Normalize(entry.Name) == normalized);
    }

    public bool Remove(string path)
    {
        var normalized = Normalize(path);
        return _entries.RemoveAll(entry => Normalize(entry.Name) == normalized) > 0;
    }

    public bool Rename(string sourcePath, string destinationPath)
    {
        var source = Find(sourcePath);
        if (source is null || Exists(destinationPath))
            return false;

        var index = _entries.IndexOf(source);
        _entries[index] = new ArchiveEntry
        {
            Name = Normalize(destinationPath),
            LinkName = source.LinkName,
            Data = source.Data,
            DataStream = source.DataStream,
            DataStreamFactory = source.DataStreamFactory,
            DeclaredDataLength = source.DeclaredDataLength,
            Metadata = source.Metadata
        };
        return true;
    }

    public void AddOrReplace(string path, byte[] data, ArchiveEntryMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Remove(path);
        _entries.Add(new ArchiveEntry
        {
            Name = Normalize(path),
            Data = data,
            Metadata = metadata ?? new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                UnixPermissions = 0x81A4,
                LinkCount = 1
            }
        });
    }

    public void ExtractToDirectory(string destinationDirectory, ExtractOptions? options = null)
        => new CpioExtractor().ExtractToDirectory(_entries, destinationDirectory, options);

    public void Save(Stream output, ArchiveFormat format = ArchiveFormat.NewAscii)
        => _archive.SaveAll(output, _entries, format);

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }
}