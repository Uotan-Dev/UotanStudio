namespace UotanStudio.Firmware.VendorFirmware.Oppo.Models;

/// <summary>
/// One file record inside a parsed Oppo firmware package.
/// <para>已解析 Oppo 固件包内的一条文件记录。</para>
/// </summary>
public sealed class OppEntry
{
    /// <summary>
    /// Entry name as recorded in the package.
    /// <para>归档中记录的条目名。</para>
    /// </summary>
    /// <value>
    /// Selection in <see cref="OppExtractOptions"/> matches against it, and extraction derives the output file name from it.
    /// <para><see cref="OppExtractOptions"/> 的条目选择按此匹配，提取时输出文件名也由此派生。</para>
    /// </value>
    public string Name { get; init; } = string.Empty;

    internal long Offset { get; init; }

    /// <summary>
    /// Declared uncompressed size of the entry in bytes.
    /// <para>条目声明的解压后大小（字节）。</para>
    /// </summary>
    /// <value>
    /// Entries with a size of zero or less are skipped during extraction.
    /// <para>大小为零或负值的条目在提取时被跳过。</para>
    /// </value>
    public long Size { get; init; }

    internal long CompressedSize { get; init; }

    /// <summary>
    /// Size of the encrypted payload in bytes.
    /// <para>加密载荷的字节大小。</para>
    /// </summary>
    /// <value>
    /// <c>0</c> means the package stores this entry without encryption.
    /// <para><c>0</c> 表示归档中该条目未加密存储。</para>
    /// </value>
    public long EncryptedSize { get; init; }

    /// <summary>
    /// Whether the package records an encrypted payload for this entry.
    /// <para>归档是否为该条目记录了加密载荷。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="EncryptedSize"/> is greater than zero.
    /// <para>当 <see cref="EncryptedSize"/> 大于零时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsEncrypted => EncryptedSize > 0;

    /// <summary>
    /// Whether the package records this entry's payload as compressed.
    /// <para>归档是否将该条目的载荷记录为压缩存储。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the source format flags the payload as compressed.
    /// <para>来源格式将载荷标记为压缩时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsCompressed { get; init; }

    /// <summary>
    /// Partition or role label recorded for this entry by the package parser.
    /// <para>包解析器为该条目记录的分区或角色标签。</para>
    /// </summary>
    /// <value>
    /// For example <c>program</c>, <c>sahara</c> or <c>ozip</c>; <see langword="null"/> when the source format records none.
    /// <para>例如 <c>program</c>、<c>sahara</c>、<c>ozip</c>；来源格式未记录时为 <see langword="null"/>。</para>
    /// </value>
    public string? PartitionType { get; init; }

    /// <summary>
    /// SHA-256 digest string recorded for this entry in the package.
    /// <para>归档中为该条目记录的 SHA-256 摘要字符串。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> when the package stores no SHA-256 digest for the entry.
    /// <para>归档未为该条目记录 SHA-256 摘要时为 <see langword="null"/>。</para>
    /// </value>
    public string? Sha256 { get; init; }

    /// <summary>
    /// MD5 digest string recorded for this entry in the package.
    /// <para>归档中为该条目记录的 MD5 摘要字符串。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> when the package stores no MD5 digest for the entry.
    /// <para>归档未为该条目记录 MD5 摘要时为 <see langword="null"/>。</para>
    /// </value>
    public string? Md5 { get; init; }

    internal long SectorOffset { get; init; }

    internal long SectorSize { get; init; }

    /// <summary>
    /// File name as listed by the package's own file table.
    /// <para>归档自身文件表中列出的文件名。</para>
    /// </summary>
    /// <value>
    /// The raw ZIP entry name for Ozip packages; <see langword="null"/> when the source format provides no separate file name.
    /// <para>Ozip 包为 ZIP 条目的原始名称；来源格式未提供独立文件名时为 <see langword="null"/>。</para>
    /// </value>
    public string? FileName { get; init; }

    /// <summary>
    /// Whether the package marks this entry as a sparse image.
    /// <para>归档是否将该条目标记为稀疏镜像。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the source metadata records the sparse flag for the entry.
    /// <para>来源元数据为该条目记录了 sparse 标志时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsSparse { get; init; }
}