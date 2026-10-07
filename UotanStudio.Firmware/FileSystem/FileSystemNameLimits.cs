using System.Text;

namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// On-disk representability guard for directory-entry names: EXT4_NAME_LEN, EROFS_NAME_LEN and
/// F2FS_NAME_LEN are all 255 bytes, names are stored as UTF-8, and the upstream kernel refuses a
/// longer name, so a builder must fail before writing rather than alias the length field or clip
/// the name. Any character the target format can encode — CJK included — passes unchanged.
/// <para>目录项名称的磁盘可表示性守卫：EXT4_NAME_LEN、EROFS_NAME_LEN 与 F2FS_NAME_LEN 均为 255 字节，
/// 名称按 UTF-8 存放，超限的名字上游内核会拒绝，因此构建器必须在写入前失败，而不是让长度字段按字节回绕
/// 或静默裁剪名称。目标格式能编码的字符（含中文）一律原样通过。</para>
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
    /// Validates one directory-entry name against <see cref="MaximumNameBytes"/> in its UTF-8
    /// encoding.
    /// <para>按 UTF-8 编码校验单个目录项名称是否超出 <see cref="MaximumNameBytes"/>。</para>
    /// </summary>
    /// <param name="name">
    /// <para>目录项名称本身，不含父目录路径。</para>
    /// The entry name alone, without any parent-directory path.
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
}