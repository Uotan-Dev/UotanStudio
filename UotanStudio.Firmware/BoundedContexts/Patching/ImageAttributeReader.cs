using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Reads literal inode ownership and mode without following symbolic links.</summary>
public static class ImageAttributeReader
{
    /// <summary>Reads raw EXT4/EROFS attributes, including root, under entry/path budgets; leaves the stream open.</summary>
    public static ImageFileAttributes Read(Stream image, string format, string path, CancellationToken token)
        => ReadCore(image, format, path, null, token).Attributes;

    /// <summary>Returns a copy of a named xattr up to 64 KiB, or null when absent; empty values remain distinct.</summary>
    public static byte[]? ReadXattr(Stream image, string format, string path, string name, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > 255 || name.Any(character => character is < '!' or > '~') || !name.Contains('.'))
        {
            throw new InvalidDataException("Invalid extended attribute name.");
        }
        return ReadCore(image, format, path, name, token).Value;
    }

    private static (ImageFileAttributes Attributes, byte[]? Value) ReadCore(Stream image, string format, string path,
        string? attribute, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(path);
        token.ThrowIfCancellationRequested();
        var selected = path.TrimStart('/');
        if (path.Length is 0 or > 4096 || selected.Contains('\\') || selected.Contains('\0')
            || selected.Length > 0 && selected.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Invalid literal image attribute path.");
        }
        using var source = new BoundedReadStream(image, 0, image.Length, leaveOpen: true);
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(source, 100000, 16 * 1024 * 1024, token);
            var entry = parser.ListEntriesWithRoot().SingleOrDefault(entry => NormalizeEntry(entry.Path) == selected)
                ?? throw new FileNotFoundException("Image attribute target is missing.", path);
            return (new(entry.Uid, entry.Gid, entry.Mode), attribute is null ? null : CopyValue(entry.Xattrs?.GetValueOrDefault(attribute)));
        }
        if (format == "EROFS")
        {
            using var parser = new ErofsParser(source, 100000, 16 * 1024 * 1024, token);
            var entry = parser.EnumerateEntries().SingleOrDefault(entry => NormalizeEntry(entry.Path) == selected)
                ?? throw new FileNotFoundException("Image attribute target is missing.", path);
            return (new(entry.Uid, entry.Gid, entry.Mode), attribute is null ? null
                : CopyValue(entry.Xattrs?.SingleOrDefault(item => item.Name == attribute)?.Value));
        }
        throw new InvalidDataException("Only raw EXT4 and EROFS attributes are supported.");
    }

    private static string NormalizeEntry(string path) => path == "." ? "" : path.Trim('/');

    private static byte[]? CopyValue(byte[]? value)
    {
        if (value is null) { return null; }
        if (value.Length > 65536) { throw new InvalidDataException("Extended attribute value exceeds 64 KiB."); }
        return value.ToArray();
    }
}

/// <summary>Linux inode ownership and complete mode, including the file-type bits.</summary>
public sealed record ImageFileAttributes(uint Uid, uint Gid, ushort Mode);