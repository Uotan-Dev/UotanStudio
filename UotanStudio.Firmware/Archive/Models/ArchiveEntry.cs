namespace UotanStudio.Firmware.Archive.Models;

/// <summary>
/// One archive member shared by the readers, editors and extractors; its payload comes from
/// in-memory <see cref="Data"/>, a supplied <see cref="DataStream"/>, or a
/// <see cref="DataStreamFactory"/>.
/// <para>读取、编辑与解包流程共享的单个归档成员；负载来自内存 <see cref="Data"/>、
/// 外部提供的 <see cref="DataStream"/> 或 <see cref="DataStreamFactory"/>。</para>
/// </summary>
public sealed class ArchiveEntry
{
    /// <summary>
    /// Member path exactly as the archive recorded it.
    /// <para>按归档原样记录存储的成员路径。</para>
    /// </summary>
    /// <value>
    /// Consumers normalize separators and leading prefixes themselves, so the value may contain
    /// backslashes or a leading root.
    /// <para>使用方自行规范分隔符与起始前缀，因此值可能含反斜杠或起始根。</para>
    /// </value>
    public required string Name { get; init; }

    /// <summary>
    /// Target path text for symbolic-link and hard-link entries.
    /// <para>符号链接与硬链接条目的目标路径文本。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> for other entry types; for a symbolic link the extractor
    /// cross-checks it against the payload text and rejects a mismatch.
    /// <para>其他类型为 <see langword="null"/>；符号链接条目会被解包器与负载文本交叉校验，
    /// 不一致即拒绝。</para>
    /// </value>
    public string? LinkName { get; init; }

    /// <summary>
    /// Directory-record metadata describing the member.
    /// <para>描述该成员的目录元数据。</para>
    /// </summary>
    /// <value>
    /// Carries file type, permission bits, timestamps and the inode and device numbers used for
    /// hard-link resolution.
    /// <para>包含文件类型、权限位、时间戳，以及硬链接解析所用的 inode 与设备号。</para>
    /// </value>
    public required ArchiveEntryMetadata Metadata { get; init; }

    /// <summary>
    /// In-memory payload bytes of the member.
    /// <para>成员的内存负载字节。</para>
    /// </summary>
    /// <value>
    /// Required and never <see langword="null"/>; an empty array means the payload is streamed via
    /// <see cref="DataStream"/> or <see cref="DataStreamFactory"/>, or the entry carries no
    /// content.
    /// <para>必需且从不为 <see langword="null"/>；空数组表示负载经 <see cref="DataStream"/> 或
    /// <see cref="DataStreamFactory"/> 流式提供，或条目本身无内容。</para>
    /// </value>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Caller-owned payload stream, preferred over <see cref="DataStreamFactory"/> and
    /// <see cref="Data"/> whenever consumers read it.
    /// <para>调用方持有的负载流；消费方读取时优先于 <see cref="DataStreamFactory"/> 与
    /// <see cref="Data"/>。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> when the payload is in-memory or factory-provided; consumers seek it
    /// to the start when seekable and never dispose it.
    /// <para>负载为内存数据或由工厂提供时为 <see langword="null"/>；消费方可寻址时回到起点，
    /// 且从不释放它。</para>
    /// </value>
    public Stream? DataStream { get; init; }

    /// <summary>
    /// Provider of a fresh readable payload stream, used only when <see cref="DataStream"/> is
    /// <see langword="null"/>.
    /// <para>新可读负载流的提供者；仅在 <see cref="DataStream"/> 为 <see langword="null"/>
    /// 时使用。</para>
    /// </summary>
    /// <value>
    /// Each extraction or link-resolution pass invokes it once and disposes the returned stream;
    /// <see langword="null"/> selects the in-memory <see cref="Data"/>.
    /// <para>每次解包或链接解析调用一次并释放返回的流；为 <see langword="null"/> 时使用内存
    /// <see cref="Data"/>。</para>
    /// </value>
    public Func<Stream>? DataStreamFactory { get; init; }

    /// <summary>
    /// Payload byte count as recorded in the archive header.
    /// <para>归档头中记录的负载字节数。</para>
    /// </summary>
    /// <value>
    /// When set it is authoritative for <see cref="DataLength"/> and length checks, and must not be
    /// negative; <see langword="null"/> lets the payload source supply the length.
    /// <para>存在时决定 <see cref="DataLength"/> 与长度校验，且不得为负；
    /// 为 <see langword="null"/> 时由负载来源给出长度。</para>
    /// </value>
    public long? DeclaredDataLength { get; init; }

    /// <summary>
    /// Effective payload length used by copying and length validation.
    /// <para>复制与长度校验使用的有效负载长度。</para>
    /// </summary>
    /// <value>
    /// <see cref="DeclaredDataLength"/> when set; otherwise the length of a seekable
    /// <see cref="DataStream"/>; otherwise the size of <see cref="Data"/>.
    /// <para>优先取 <see cref="DeclaredDataLength"/>；否则取可寻址 <see cref="DataStream"/> 的
    /// 长度；再否则取 <see cref="Data"/> 的大小。</para>
    /// </value>
    /// <exception cref="InvalidDataException">
    /// <see cref="DeclaredDataLength"/> is negative.
    /// <para><see cref="DeclaredDataLength"/> 为负数。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The payload is factory-provided or a non-seekable stream and no length is declared.
    /// <para>负载由工厂提供或为不可寻址流，且未声明长度。</para>
    /// </exception>
    public long DataLength
    {
        get
        {
            if (DeclaredDataLength is < 0)
            {
                throw new InvalidDataException($"Entry '{Name}' has a negative declared payload length.");
            }

            return DeclaredDataLength ?? (DataStream is { CanSeek: true } stream
                ? stream.Length
                : DataStream is not null || DataStreamFactory is not null
                    ? throw new InvalidOperationException($"Entry '{Name}' has a streaming payload with unknown length.")
                    : Data.LongLength);
        }
    }
}