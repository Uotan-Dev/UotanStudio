namespace UotanStudio.Firmware.Cpio.Model;

/// <summary>
/// Represents one entry in an archive with metadata and content bytes.
/// <para>表示归档中的一条带有元数据与内容字节的条目。</para>
/// </summary>
public sealed class ArchiveEntry
{
    /// <summary>
    /// Gets the normalized entry path within the archive.
    /// <para>获取归档内的规范化条目路径。</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the link target path for symbolic and hard-link entries.
    /// <para>获取符号链接与硬链接条目的链接目标路径。</para>
    /// </summary>
    public string? LinkName { get; init; }

    /// <summary>
    /// Gets entry metadata such as file type, permissions, ownership, and timestamps.
    /// <para>获取条目元数据（文件类型、权限、属主与时间戳等）。</para>
    /// </summary>
    public required ArchiveEntryMetadata Metadata { get; init; }

    /// <summary>
    /// Gets the raw payload bytes for the entry.
    /// <para>获取条目的原始负载字节。</para>
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Gets a stream providing the entry payload. When non-null, writers stream from it
    /// instead of <see cref="Data"/>. The writer that consumes the entry disposes the stream.
    /// <para>获取提供条目负载的流。非 null 时写入方优先从其流式读取，而非使用 Data。
    /// 消费该条目的写入方负责释放该流。</para>
    /// </summary>
    public Stream? DataStream { get; init; }

    /// <summary>Gets a payload stream lazily when the writer consumes this entry.</summary>
    public Func<Stream>? DataStreamFactory { get; init; }

    /// <summary>Gets the explicitly declared payload length, required for non-seekable or factory streams.</summary>
    public long? DeclaredDataLength { get; init; }

    /// <summary>
    /// Gets the payload length. Unknown streaming lengths are rejected rather than treated as zero.
    /// </summary>
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