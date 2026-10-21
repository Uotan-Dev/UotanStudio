namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Creation form of one inode's metadata record: where the record lives and whether it uses the compact
/// 32-byte or the extended 64-byte width. A preserved rebuild replays the recorded form instead of
/// re-deriving it, because the same inode fields can be written through the plain metadata area, the
/// METABOX container or the separate metadata zone, and in either record width.
/// <para>单个 inode 元数据记录的创建形态：记录所在的位置，以及记录采用 32 字节紧凑宽度还是 64 字节扩展宽度。
/// 保留式重建按记录的形态重放，而不是重新推导，因为相同的 inode 字段既可以写在普通元数据区、METABOX 容器或
/// 独立元数据区，也可以采用两种记录宽度之一。</para>
/// </summary>
internal enum ErofsInodeMetadataCreationType
{
    /// <summary>Record in the plain metadata area using the compact width.
    /// <para>记录位于普通元数据区，采用紧凑宽度。</para></summary>
    PlainCompact = 0,

    /// <summary>Record in the plain metadata area using the extended width.
    /// <para>记录位于普通元数据区，采用扩展宽度。</para></summary>
    PlainExtended = 1,

    /// <summary>Record resident in the METABOX container using the compact width.
    /// <para>记录常驻 METABOX 容器，采用紧凑宽度。</para></summary>
    MetaboxCompact = 2,

    /// <summary>Record resident in the METABOX container using the extended width.
    /// <para>记录常驻 METABOX 容器，采用扩展宽度。</para></summary>
    MetaboxExtended = 3,

    /// <summary>Record moved into the separate metadata zone using the compact width.
    /// <para>记录移入独立元数据区，采用紧凑宽度。</para></summary>
    MetazoneCompact = 4,

    /// <summary>Record moved into the separate metadata zone using the extended width.
    /// <para>记录移入独立元数据区，采用扩展宽度。</para></summary>
    MetazoneExtended = 5,
}

/// <summary>
/// Queries and parses <see cref="ErofsInodeMetadataCreationType"/> values.
/// <para>查询与解析 <see cref="ErofsInodeMetadataCreationType"/> 值。</para>
/// </summary>
internal static class ErofsInodeMetadataCreationTypeExtensions
{
    extension(ErofsInodeMetadataCreationType type)
    {
        /// <summary>Whether the record lives in the METABOX container.
        /// <para>记录是否位于 METABOX 容器。</para></summary>
        /// <value><see langword="true"/> for the two METABOX members.
        /// <para>两个 METABOX 成员为 <see langword="true"/>。</para></value>
        internal bool IsMetabox => type is ErofsInodeMetadataCreationType.MetaboxCompact or ErofsInodeMetadataCreationType.MetaboxExtended;

        /// <summary>Whether the record was moved into the separate metadata zone.
        /// <para>记录是否被移入独立元数据区。</para></summary>
        /// <value><see langword="true"/> for the two metazone members.
        /// <para>两个 metazone 成员为 <see langword="true"/>。</para></value>
        internal bool IsMetazone => type is ErofsInodeMetadataCreationType.MetazoneCompact or ErofsInodeMetadataCreationType.MetazoneExtended;

        /// <summary>Whether the record uses the 64-byte extended width.
        /// <para>记录是否采用 64 字节扩展宽度。</para></summary>
        /// <value><see langword="true"/> for every extended member.
        /// <para>每个扩展成员为 <see langword="true"/>。</para></value>
        internal bool IsExtended => type is ErofsInodeMetadataCreationType.PlainExtended
            or ErofsInodeMetadataCreationType.MetaboxExtended
            or ErofsInodeMetadataCreationType.MetazoneExtended;
    }

    extension(ErofsInodeMetadataCreationType)
    {
        /// <summary>Builds the creation form from the record location and width flags.
        /// <para>由记录位置与宽度标志构造创建形态。</para></summary>
        /// <param name="inMetabox">Whether the record is resident in the METABOX container.
        /// <para>记录是否常驻 METABOX 容器。</para></param>
        /// <param name="metazone">Whether the metadata area was moved into the separate metadata zone; ignored
        /// when <paramref name="inMetabox"/> is set.
        /// <para>元数据区是否被移入独立元数据区；<paramref name="inMetabox"/> 置起时忽略。</para></param>
        /// <param name="extended">Whether the record uses the 64-byte extended width.
        /// <para>记录是否采用 64 字节扩展宽度。</para></param>
        /// <returns>The matching member.<para>对应的成员。</para></returns>
        internal static ErofsInodeMetadataCreationType From(bool inMetabox, bool metazone, bool extended)
        {
            return (inMetabox, metazone, extended) switch
            {
                (true, _, true) => ErofsInodeMetadataCreationType.MetaboxExtended,
                (true, _, false) => ErofsInodeMetadataCreationType.MetaboxCompact,
                (false, true, true) => ErofsInodeMetadataCreationType.MetazoneExtended,
                (false, true, false) => ErofsInodeMetadataCreationType.MetazoneCompact,
                (_, _, true) => ErofsInodeMetadataCreationType.PlainExtended,
                _ => ErofsInodeMetadataCreationType.PlainCompact,
            };
        }

        /// <summary>Parses the stored member name of a creation form.
        /// <para>解析创建形态保存的成员名。</para></summary>
        /// <param name="text">Stored text; <see langword="null"/> or an unknown name is rejected.
        /// <para>保存的文本；<see langword="null"/> 或未知名称被拒绝。</para></param>
        /// <param name="value">Parsed member when the call succeeds, otherwise the default value.
        /// <para>成功时为解析出的成员，否则为默认值。</para></param>
        /// <returns><see langword="true"/> when the text names a defined member.
        /// <para>文本指名已定义成员时为 <see langword="true"/>。</para></returns>
        internal static bool TryParse(string? text, out ErofsInodeMetadataCreationType value)
        {
            return Enum.TryParse<ErofsInodeMetadataCreationType>(text, ignoreCase: false, out value)
                && Enum.IsDefined(value);
        }
    }
}