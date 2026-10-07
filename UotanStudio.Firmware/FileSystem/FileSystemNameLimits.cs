using System.Text;

namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// On-disk representability guard for directory-entry names. Both rules mirror the shape of the
/// upstream NAME_MAX constraint: EXT4_NAME_LEN, EROFS_NAME_LEN and F2FS_NAME_LEN are all 255 bytes,
/// names are stored as UTF-8, and the kernel refuses a longer name, so a builder must fail before
/// writing rather than alias the length field or clip the name; the same kernel contract also keeps an
/// empty component, the <c>'/'</c> separator, a NUL byte and the <c>.</c> and <c>..</c> records every
/// writer synthesises itself out of a directory entry. Any character the target format can encode —
/// CJK, accents and spaces — passes unchanged.
/// <para>目录项名称的磁盘可表示性守卫。两条规则都对应上游 NAME_MAX 的形状：EXT4_NAME_LEN、EROFS_NAME_LEN 与
/// F2FS_NAME_LEN 均为 255 字节，名称按 UTF-8 存放，超限的名字上游内核会拒绝，因此构建器必须在写入前失败，
/// 而不是让长度字段按字节回绕或静默裁剪名称；同一份内核约定还把空组件、<c>'/'</c> 分隔符、NUL 字节，以及由
/// 写入器自行合成的 <c>.</c> 与 <c>..</c> 记录挡在目录项之外。目标格式能编码的字符（中文、重音字母、空格）
/// 一律原样通过。</para>
/// </summary>
internal static class FileSystemNameLimits
{
    /// <summary>
    /// The shared per-name limit of the three packable filesystems, counted in UTF-8 bytes rather
    /// than in UTF-16 characters, which is what a host <see cref="string"/> length reports.
    /// <para>三种可打包文件系统共用的单名称上限，按 UTF-8 字节数计算，而不是宿主 <see cref="string"/>
    /// 长度所给的 UTF-16 字符数。</para>
    /// </summary>
    /// <value>
    /// <para>单个目录项名称允许的最大 UTF-8 字节数。</para>
    /// The largest UTF-8 byte length a single directory-entry name may have.
    /// </value>
    internal const int MaximumNameBytes = 255;

    /// <summary>
    /// Validates one name against <see cref="MaximumNameBytes"/> in its UTF-8 encoding, applying the
    /// length rule only. A directory-entry writer must call <see cref="ValidateEntryName"/> instead;
    /// extended-attribute short names stay on this overload because that namespace is not a path
    /// component, so <c>'/'</c>, a NUL byte and a dot are ordinary characters there.
    /// <para>按 UTF-8 编码校验单个名称是否超出 <see cref="MaximumNameBytes"/>，只应用长度规则。
    /// 目录项写入器应改为调用 <see cref="ValidateEntryName"/>；扩展属性的短名称仍走本重载，
    /// 因为那个命名空间不是路径组件，<c>'/'</c>、NUL 字节与点在里都只是普通字符。</para>
    /// </summary>
    /// <param name="name">
    /// <para>被校验的名称本身，不含父目录路径；可以是目录项名称，也可以是扩展属性短名称。</para>
    /// The name alone, without any parent-directory path; either a directory-entry name or an
    /// extended-attribute short name.
    /// </param>
    /// <param name="formatName">
    /// <para>写入该名称的格式标签，仅用于错误消息。</para>
    /// The format label writing this name, used only in the failure message.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <para><paramref name="name"/> 的 UTF-8 编码超过 <see cref="MaximumNameBytes"/> 字节，
    /// 上游文件系统无法在磁盘上表示它。</para>
    /// <paramref name="name"/> encodes to more than <see cref="MaximumNameBytes"/> bytes, which the
    /// target filesystem cannot represent on disk.
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
    /// component charset rule the upstream EXT4, EROFS and F2FS directory writers enforce. A name may
    /// not be empty, may not contain <c>'/'</c> or a NUL byte, and may not be <c>.</c> or <c>..</c>.
    /// Writers that synthesise their own dot records keep <see cref="Validate"/> for those records.
    /// <para>校验单个目录项子项名称：先执行 <see cref="Validate"/> 的长度规则，再执行上游 EXT4、EROFS 与
    /// F2FS 目录写入器共同强制的组件字符集规则。名称不得为空、不得含 <c>'/'</c> 或 NUL 字节，也不得是
    /// <c>.</c> 或 <c>..</c>。自行合成点条目的写入器对那些条目仍使用 <see cref="Validate"/>。</para>
    /// </summary>
    /// <param name="name">
    /// <para>目录项子项名称本身，不含父目录路径，也不含写入器自己合成的点条目。</para>
    /// The child entry name alone, without any parent-directory path and without the dot records the
    /// writer synthesises.
    /// </param>
    /// <param name="formatName">
    /// <para>写入该名称的格式标签，仅用于错误消息。</para>
    /// The format label writing this name, used only in the failure message.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <para><paramref name="name"/> 超过 <see cref="MaximumNameBytes"/> 字节，或为空、含 <c>'/'</c>、
    /// 含 NUL 字节，或正好是 <c>.</c> 或 <c>..</c>，上游目录项无法表示它。</para>
    /// <paramref name="name"/> exceeds <see cref="MaximumNameBytes"/> UTF-8 bytes, or is empty, or
    /// carries <c>'/'</c> or a NUL byte, or is the <c>.</c> or <c>..</c> name a writer already
    /// synthesises, none of which a directory entry can represent on disk.
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