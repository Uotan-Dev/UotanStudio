namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents a chunk in a sparse file, including its header, block position, and optional data provider.
/// <para>表示稀疏文件中的一个数据块，包括其头部、块位置和可选的数据提供程序。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SparseChunk"/> class with the specified chunk header.
/// <para>使用指定的数据块头部初始化 SparseChunk 的新实例。</para>
/// </remarks>
/// <param name="header">The chunk header describing this chunk's type and size.
/// <para>描述此数据块类型和大小的数据块头部。</para></param>
public class SparseChunk(ChunkHeader header) : IDisposable
{

    /// <summary>
    /// Gets or sets the starting block index for this chunk.
    /// <para>获取或设置此数据块的起始块索引。</para>
    /// </summary>
    public uint StartBlock { get; set; } = 0;

    /// <summary>
    /// Gets the <see cref="ChunkHeader"/> that describes this chunk's type, size, and total size.
    /// <para>获取描述此数据块类型、大小和总大小的 ChunkHeader。</para>
    /// </summary>
    public ChunkHeader Header { get; init; } = header;

    /// <summary>
    /// Gets or sets the <see cref="ISparseDataProvider"/> that supplies the chunk's payload data.
    /// May be null for chunks without payload (DontCare) or when data is represented by <see cref="FillValue"/>.
    /// <para>获取或设置提供此数据块载荷数据的 ISparseDataProvider。
    /// 对于无载荷的数据块（DontCare）或数据由 FillValue 表示时，可为 null。</para>
    /// </summary>
    public ISparseDataProvider? DataProvider { get; set; }

    /// <summary>
    /// Gets or sets the 4-byte fill pattern value used only for Fill chunks.
    /// <para>获取或设置仅用于 Fill 数据块的 4 字节填充模式值。</para>
    /// </summary>
    public uint FillValue { get; set; }

    /// <summary>
    /// Releases all resources used by the <see cref="SparseChunk"/>, including its data provider.
    /// <para>释放 SparseChunk 使用的所有资源，包括其数据提供程序。</para>
    /// </summary>
    public void Dispose() => DataProvider?.Dispose();
}