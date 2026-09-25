using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;
namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

/// <summary>
/// A single chunk inside a <see cref="SparseFile"/>: header, start position and either a data provider or a fill pattern.
/// <para><see cref="SparseFile"/> 中的单个数据块：头部、起始位置，以及数据提供器或填充模式二者之一。</para>
/// </summary>
/// <param name="header">The chunk header carrying type, block count and total on-disk size.
/// <para>携带类型、块数与磁盘总大小的数据块头部。</para></param>
public class SparseChunk(ChunkHeader header) : IDisposable
{

    /// <summary>Logical block index where this chunk begins.
    /// <para>该数据块起始的逻辑块索引。</para></summary>
    /// <value>Zero by default; writers rely on chunks being ordered by this value and insert gaps around out-of-order entries.
    /// <para>默认 0；写入器依赖数据块按此值有序，并在乱序条目周围插入空隙。</para></value>
    public uint StartBlock { get; set; } = 0;

    /// <summary>The chunk header supplied at construction; init-only.
    /// <para>构造时提供的数据块头部；仅 init。</para></summary>
    /// <value>Describes the chunk type, covered block count and total size; see <see cref="ChunkHeader"/>.
    /// <para>描述数据块类型、覆盖块数与总大小；参见 <see cref="ChunkHeader"/>。</para></value>
    public ChunkHeader Header { get; init; } = header;

    /// <summary>Payload source for RAW chunks.
    /// <para>RAW 块的载荷来源。</para></summary>
    /// <value><see langword="null"/> for non-RAW chunks and for RAW chunks without data; owning code (the chunk and <see cref="SparseFile.Dispose"/> on the file) disposes it.
    /// <para>非 RAW 块与无数据 RAW 块为 <see langword="null"/>；由持有方（本块以及文件的 <see cref="SparseFile.Dispose"/>）负责释放。</para></value>
    public ISparseDataProvider? DataProvider { get; set; }

    /// <summary>Little-endian 4-byte pattern repeated across a FILL chunk's blocks.
    /// <para>在 FILL 块各块中重复的小端 4 字节模式。</para></summary>
    /// <value>Meaningless for chunk types other than <see cref="ChunkType.Fill"/>; defaults to 0.
    /// <para>除 <see cref="ChunkType.Fill"/> 外的块类型下无意义；默认 0。</para></value>
    public uint FillValue { get; set; }

    /// <summary>
    /// Releases all resources used by the SparseChunk, including its data provider.
    /// <para>释放 SparseChunk 使用的所有资源，包括其数据提供程序。</para>
    /// </summary>
    public void Dispose() => DataProvider?.Dispose();
}