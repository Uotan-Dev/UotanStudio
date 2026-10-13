using System.Text;

namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// On-disk representability guard for directory-entry names: a 255 UTF-8-byte length limit and a
/// component charset rule that rejects empty names, <c>'/'</c>, NUL and the synthesised dot records.
/// <para>目录项名称的磁盘可表示性守卫：255 字节 UTF-8 长度上限，以及拒绝空名称、<c>'/'</c>、NUL 与
/// 合成点记录的组件字符集规则。</para>
/// </summary>
internal static class FileSystemNameLimits
{
    /// <summary>
    /// The shared per-name limit of the three packable filesystems, counted in UTF-8 bytes rather
    /// than in UTF-16 characters, which is what a host <see cref="string"/> length reports.
    /// <para>三种可打包文件系统共用的单名称上限，按 UTF-8 字节数计算，而不是宿主 <see cref="string"/>
    /// 长度所给的 UTF-16 字符数。</para>
    /// </summary>
    /// <value>The largest UTF-8 byte length a single directory-entry name may have.
    /// <para>单个目录项名称允许的最大 UTF-8 字节数。</para>
    /// </value>
    internal const int MaximumNameBytes = 255;

    /// <summary>
    /// Validates one name against <see cref="MaximumNameBytes"/> in its UTF-8 encoding, applying the
    /// length rule only; xattr short names stay on this overload because they are not path components.
    /// <para>按 UTF-8 编码校验单个名称是否超出 <see cref="MaximumNameBytes"/>，只应用长度规则；
    /// 扩展属性短名称不是路径组件，仍走本重载。</para>
    /// </summary>
    /// <param name="name">The name alone, without any parent-directory path; either a directory-entry
    /// name or an extended-attribute short name.
    /// <para>被校验的名称本身，不含父目录路径；可以是目录项名称，也可以是扩展属性短名称。</para>
    /// </param>
    /// <param name="formatName">
    /// The format label writing this name, used only in the failure message.
    /// <para>写入该名称的格式标签，仅用于错误消息。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> encodes to more than <see cref="MaximumNameBytes"/> bytes, which the
    /// target filesystem cannot represent on disk.
    /// <para><paramref name="name"/> 的 UTF-8 编码超过 <see cref="MaximumNameBytes"/> 字节，
    /// 目标文件系统无法在磁盘上表示它。</para>
    /// </exception>
    internal static void Validate(string name, string formatName)
    {
        var bytes = Encoding.UTF8.GetByteCount(name);
        if (bytes <= MaximumNameBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{formatName} file name is not representable: {bytes} UTF-8 bytes exceed the {MaximumNameBytes}-byte name limit: '{name}'.");
    }

    /// <summary>
    /// Validates one directory-entry child name: the <see cref="Validate"/> length rule plus the
    /// component charset rule — the name may not be empty, contain <c>'/'</c> or a NUL byte, or be
    /// <c>.</c> or <c>..</c>.
    /// <para>校验单个目录项子项名称：<see cref="Validate"/> 的长度规则加组件字符集规则——名称不得为空、
    /// 不得含 <c>'/'</c> 或 NUL 字节，也不得是 <c>.</c> 或 <c>..</c>。</para>
    /// </summary>
    /// <param name="name">The child entry name alone, without any parent-directory path and without
    /// the dot records the writer synthesises.
    /// <para>目录项子项名称本身，不含父目录路径，也不含写入器自己合成的点条目。</para>
    /// </param>
    /// <param name="formatName">
    /// The format label writing this name, used only in the failure message.
    /// <para>写入该名称的格式标签，仅用于错误消息。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> exceeds <see cref="MaximumNameBytes"/> UTF-8 bytes, or is empty, or
    /// carries <c>'/'</c> or a NUL byte, or is the <c>.</c> or <c>..</c> name a writer already
    /// synthesises, none of which a directory entry can represent on disk.
    /// <para><paramref name="name"/> 超过 <see cref="MaximumNameBytes"/> 字节，或为空、含 <c>'/'</c>、
    /// 含 NUL 字节，或正好是 <c>.</c> 或 <c>..</c>，目录项无法在磁盘上表示它。</para>
    /// </exception>
    internal static void ValidateEntryName(string name, string formatName)
    {
        Validate(name, formatName);

        if (name.Length == 0)
        {
            throw new InvalidOperationException(
                $"{formatName} file name is not representable: a directory entry name cannot be empty: '{name}'.");
        }

        if (name.Contains('/'))
        {
            throw new InvalidOperationException(
                $"{formatName} file name is not representable: a directory entry name cannot contain the path separator '/': '{name}'.");
        }

        if (name.Contains('\0'))
        {
            throw new InvalidOperationException(
                $"{formatName} file name is not representable: a directory entry name cannot contain the NUL character: '{name}'.");
        }

        if (name is "." or "..")
        {
            throw new InvalidOperationException(
                $"{formatName} file name is not representable: '{name}' would duplicate the '.' and '..' entries the writer synthesises for every directory.");
        }
    }
}