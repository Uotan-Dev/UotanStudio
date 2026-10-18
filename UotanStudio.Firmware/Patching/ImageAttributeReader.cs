using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Reads literal inode ownership and mode without following symbolic links.
/// <para>读取字面的 inode 属主与权限，不跟随符号链接。</para>
/// </summary>
public static class ImageAttributeReader
{
    /// <summary>Reads raw EXT4/EROFS attributes, including root, under entry/path budgets; leaves the stream open.
    /// <para>在条目数与路径长度预算内读取原始 EXT4/EROFS 属性，包含根目录；调用结束后流保持打开。</para></summary>
    /// <param name="image">Readable, seekable raw image positioned at its start; the parser sees only its current length.
    /// <para>定位在镜像起始处、可读且可寻址的原始镜像；解析器只可见其当前长度。</para></param>
    /// <param name="format"><c>"EXT4"</c> or <c>"EROFS"</c>, matched case-sensitively.
    /// <para>区分大小写的 <c>"EXT4"</c> 或 <c>"EROFS"</c>。</para></param>
    /// <param name="path">Image-relative path of 1 to 4096 characters; leading slashes are ignored and empty, <c>.</c> and <c>..</c> segments as well as backslashes and NUL are forbidden. A single slash addresses the root inode.
    /// <para>长度为 1 到 4096 个字符的镜像内路径；忽略前导斜杠，禁止空段、<c>.</c>、<c>..</c>、反斜杠与 NUL。 单个斜杠表示根 inode。</para></param>
    /// <param name="token">Checked before parsing and while enumerating entries.
    /// <para>在解析之前以及枚举条目时都会检查。</para></param>
    /// <returns>The uid, gid and complete mode of the single matching entry.
    /// <para>唯一匹配条目的 uid、gid 以及完整 mode。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.
    /// <para><paramref name="path"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The path is malformed or the format is unsupported.
    /// <para>路径格式非法或格式不受支持。</para></exception>
    /// <exception cref="InvalidOperationException">More than one entry normalizes to the same path.
    /// <para>有多个条目规整后指向同一路径。</para></exception>
    /// <exception cref="FileNotFoundException">No entry in the image has the normalized path.
    /// <para>镜像中不存在规整后路径对应的条目。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static ImageFileAttributes Read(Stream image, string format, string path, CancellationToken token)
        => ReadCore(image, format, path, null, token).Attributes;

    /// <summary>Returns a copy of a named xattr up to 64 KiB, or null when absent; empty values remain distinct.
    /// <para>返回指定名称扩展属性的副本，最大 64 KiB；属性不存在时返回 <see langword="null"/>；空值与不存在保持区分。</para></summary>
    /// <param name="image">Readable, seekable raw image positioned at its start.
    /// <para>定位在镜像起始处、可读且可寻址的原始镜像。</para></param>
    /// <param name="format"><c>"EXT4"</c> or <c>"EROFS"</c>, matched case-sensitively.
    /// <para>区分大小写的 <c>"EXT4"</c> 或 <c>"EROFS"</c>。</para></param>
    /// <param name="path">Image-relative path validated by the same rules as in <see cref="Read"/>.
    /// <para>遵循与 <see cref="Read"/> 相同校验规则的镜像内路径。</para></param>
    /// <param name="name">Attribute name of 1 to 255 printable ASCII characters that must contain a dot, for example <c>security.selinux</c>.
    /// <para>长度为 1 到 255 个可见 ASCII 字符、且必须包含句点的属性名，例如 <c>security.selinux</c>。</para></param>
    /// <param name="token">Checked before parsing and while enumerating entries.
    /// <para>在解析之前以及枚举条目时都会检查。</para></param>
    /// <returns>A defensive copy of the stored value; an empty array when the attribute exists with a zero-length value and <see langword="null"/> when the target has no such attribute.
    /// <para>所存储值的安全副本；属性存在但值为空时返回空数组， 目标没有该属性时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="name"/> is <see langword="null"/>.
    /// <para><paramref name="path"/> 或 <paramref name="name"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The attribute name or path is invalid, the format is unsupported, or the stored value is larger than 64 KiB.
    /// <para>属性名或路径非法、格式不受支持，或存储的值大于 64 KiB。</para></exception>
    /// <exception cref="InvalidOperationException">More than one entry normalizes to the same path.
    /// <para>有多个条目规整后指向同一路径。</para></exception>
    /// <exception cref="FileNotFoundException">No entry in the image has the normalized path.
    /// <para>镜像中不存在规整后路径对应的条目。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
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
        throw new PatchingUnsupportedFeatureException("Only raw EXT4 and EROFS attributes are supported.");
    }

    private static string NormalizeEntry(string path) => path == "." ? "" : path.Trim('/');

    private static byte[]? CopyValue(byte[]? value)
    {
        if (value is null) { return null; }
        if (value.Length > 65536) { throw new PatchingCorruptedException("Extended attribute value exceeds 64 KiB."); }
        return [.. value];
    }
}

/// <summary>
/// Linux inode ownership and complete mode, including the file-type bits.
/// <para>Linux inode 的属主与完整 mode，包含文件类型位。</para>
/// </summary>
/// <param name="Uid">Owner user identifier recorded in the inode. <para>inode 中记录的所有者用户标识。</para></param>
/// <param name="Gid">Owner group identifier recorded in the inode. <para>inode 中记录的所有者组标识。</para></param>
/// <param name="Mode">Full mode value: the low twelve permission and special bits together with the file-type bits.
/// <para>完整 mode 值：低十二位权限与特殊位，以及文件类型位。</para></param>
public sealed record ImageFileAttributes(uint Uid, uint Gid, ushort Mode);